package workspace

import (
	"bufio"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"strconv"
	"strings"
	"time"
)

const graph = "https://graph.microsoft.com/v1.0/me/drive/"
const markerName = "payroll-workspace-v1.json"

type drive struct {
	onTransfer          func(bool)
	transferring        bool
	token, folder, root string
	client              *http.Client
}
type item struct {
	ID          string `json:"id"`
	ETag        string `json:"eTag"`
	Description string `json:"description"`
	Size        int64  `json:"size"`
	Parent      struct {
		Drive string `json:"driveId"`
	} `json:"parentReference"`
}
type head struct {
	item
	Hash, Generation string
}

func validHash(h string) bool {
	b, e := hex.DecodeString(h)
	return e == nil && len(b) == 32 && strings.ToLower(h) == h
}

type retryAfter struct{ until time.Time }

func (r retryAfter) Error() string {
	return fmt.Sprintf("طلب OneDrive تخفيف الطلبات. أعد المحاولة بعد %d ثانية.", int(time.Until(r.until).Seconds())+1)
}
func retryDeadline(header string, now time.Time) time.Time {
	if n, e := strconv.ParseInt(header, 10, 32); e == nil && n > 0 {
		return now.Add(time.Duration(n) * time.Second)
	}
	if date, e := http.ParseTime(header); e == nil && date.After(now) {
		return date
	}
	return now.Add(time.Minute)
}
func (g *drive) request(method, path string, data []byte, headers map[string]string, limit int64) (int, []byte) {
	req, e := http.NewRequest(method, graph+path, bytes.NewReader(data))
	must(e)
	req.Header.Set("Authorization", "Bearer "+g.token)
	for k, v := range headers {
		req.Header.Set(k, v)
	}
	resp, e := g.client.Do(req)
	if e != nil {
		panic("تعذّر الاتصال بـ OneDrive. ستُعاد المحاولة.")
	}
	defer resp.Body.Close()
	if resp.StatusCode == 429 || resp.StatusCode == 503 {
		panic(retryAfter{retryDeadline(resp.Header.Get("Retry-After"), time.Now())})
	}
	b, e := io.ReadAll(io.LimitReader(resp.Body, limit+1))
	must(e)
	check(int64(len(b)) <= limit, "OneDrive response exceeds size limit")
	return resp.StatusCode, b
}
func ok(code int) {
	if code < 200 || code >= 300 {
		panic(fmt.Errorf("OneDrive HTTP %d", code))
	}
}
func connect(token string) *drive {
	g := &drive{token: token, client: &http.Client{Timeout: 2 * time.Minute, CheckRedirect: func(r *http.Request, via []*http.Request) error {
		if len(via) > 3 || via[0].Method != "GET" || r.URL.Scheme != "https" {
			return http.ErrUseLastResponse
		}
		r.Header.Del("Authorization")
		return nil
	}}}
	code, b := g.request("GET", "special/approot", nil, nil, 1<<20)
	ok(code)
	var i item
	must(decode(b, &i))
	check(i.ID != "" && i.Parent.Drive != "", "Invalid OneDrive folder")
	g.folder = i.Parent.Drive + "/" + i.ID
	g.root = "items/" + url.PathEscape(i.ID) + ":/"
	return g
}
func (g *drive) marker(create bool) head {
	path := g.root + markerName
	code, b := g.request("GET", path+"?$select=id,eTag,description", nil, nil, 1<<20)
	if code == 404 && create {
		c, _ := g.request("PUT", path+":/content?@microsoft.graph.conflictBehavior=fail", []byte("{\"format\":1}"), nil, 1<<20)
		if c != 409 {
			ok(c)
		}
		code, b = g.request("GET", path+"?$select=id,eTag,description", nil, nil, 1<<20)
	}
	ok(code)
	var i item
	must(decode(b, &i))
	check(i.ID != "" && i.ETag != "", "Invalid OneDrive revision")
	revision := i.Description
	generation := ""
	if strings.HasPrefix(revision, "restore/") {
		parts := strings.Split(revision, "/")
		check(len(parts) == 3 && validHash(parts[1]) && validHash(parts[2]), "Invalid restore generation")
		generation, revision = parts[1], parts[2]
	}
	check(revision == "" || validHash(revision), "Unknown OneDrive workspace version")
	return head{i, revision, generation}
}
func (g *drive) claim(h head, id, generation string) {
	if generation != "" {
		id = "restore/" + generation + "/" + id
	}
	c, b := g.request("PATCH", "items/"+url.PathEscape(h.ID), encode(Object{"description": id}), map[string]string{"If-Match": h.ETag, "Content-Type": "application/json"}, 1<<20)
	if c == 412 {
		panic(errRace)
	}
	ok(c)
	var updated item
	must(decode(b, &updated))
	check(updated.Description == id, "OneDrive did not confirm the revision")
}
func (g *drive) get(prefix, hash string, limit int64) []byte {
	check(validHash(hash), "Invalid OneDrive hash")
	code, b := g.request("GET", g.root+prefix+hash+":/content", nil, nil, limit)
	ok(code)
	check(sum(b) == hash, "فشل التحقق من بصمة ملف المزامنة.")
	return b
}
func (g *drive) setTransferring(active bool) {
	if g.transferring == active {
		return
	}
	g.transferring = active
	if g.onTransfer != nil {
		g.onTransfer(active)
	}
}
func (g *drive) put(prefix string, b []byte) string {
	id := sum(b)
	path := g.root + prefix + id
	code, data := g.request("GET", path+"?$select=size", nil, nil, 1<<20)
	if code == 200 {
		var i item
		must(decode(data, &i))
		check(i.Size == int64(len(b)), "OneDrive block size mismatch")
		return id
	}
	if code != 404 {
		ok(code)
	}
	if prefix == chunkPrefix {
		g.setTransferring(true)
	}
	code, _ = g.request("PUT", path+":/content?@microsoft.graph.conflictBehavior=fail", b, nil, 1<<20)
	if code != 409 {
		ok(code)
	}
	return id
}

type fileIndex struct {
	At       int64    `json:"at,omitempty"`
	Kind     string   `json:"kind,omitempty"`
	Format   int      `json:"format"`
	Previous string   `json:"previous"`
	Size     int64    `json:"size"`
	SHA      string   `json:"sha256"`
	Blocks   []string `json:"blocks"`
}

const chunkPrefix = "payroll-work-block-v1-"
const indexPrefix = "payroll-work-index-v1-"

func (g *drive) upload(path, previous string) string {
	defer g.setTransferring(false)
	f, e := os.Open(path)
	must(e)
	defer f.Close()
	r := bufio.NewReaderSize(f, 256<<10)
	var block bytes.Buffer
	h := sha256.New()
	idx := fileIndex{Format: 1, Previous: previous, At: time.Now().UnixMilli()}
	var rolling uint64
	flush := func() {
		if block.Len() > 0 {
			data := block.Bytes()
			h.Write(data)
			idx.Size += int64(len(data))
			idx.Blocks = append(idx.Blocks, g.put(chunkPrefix, data))
			block.Reset()
			rolling = 0
		}
	}
	// Content-defined boundaries re-align after an inserted row in the compressed ZIP.
	for {
		b, e := r.ReadByte()
		if e == io.EOF {
			break
		}
		must(e)
		block.WriteByte(b)
		rolling = (rolling << 1) + gear[b]
		if block.Len() >= 8<<20 || (block.Len() >= 1<<20 && rolling&0x1fffff == 0) {
			flush()
		}
	}
	flush()
	idx.SHA = hex.EncodeToString(h.Sum(nil))
	return g.put(indexPrefix, encode(idx))
}

var gear = func() (table [256]uint64) {
	for i := range table {
		v := sha256.Sum256([]byte{byte(i)})
		for _, b := range v[:8] {
			table[i] = table[i]<<8 | uint64(b)
		}
	}
	return
}()

func (g *drive) download(id, dest string) {
	var idx fileIndex
	must(json.Unmarshal(g.get(indexPrefix, id, 4<<20), &idx))
	check(idx.Format == 1 && validHash(idx.SHA) && idx.Size > 0 && len(idx.Blocks) > 0 && int64(len(idx.Blocks)-1) <= idx.Size/(1<<20), "Invalid OneDrive index")
	f, e := os.OpenFile(dest, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	must(e)
	defer f.Close()
	h := sha256.New()
	var total int64
	g.setTransferring(true)
	defer g.setTransferring(false)
	for _, part := range idx.Blocks {
		b := g.get(chunkPrefix, part, 8<<20)
		total += int64(len(b))
		check(total <= idx.Size, "Incomplete download")
		h.Write(b)
		_, e = f.Write(b)
		must(e)
	}
	check(total == idx.Size && hex.EncodeToString(h.Sum(nil)) == idx.SHA, "Invalid archive checksum")
	must(f.Sync())
	must(f.Close())
}

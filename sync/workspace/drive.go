package workspace

import (
	"bytes"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

const graph = "https://graph.microsoft.com/v1.0/me/drive/"
const markerName = "payroll-workspace-v1.json"

type drive struct {
	onTransfer          func(bool)
	onProgress          func(int, int64, int64)
	cacheDir            string
	sources             []string
	transferring        bool
	token, folder, root string
	client              *http.Client
}
type item struct {
	ID          string `json:"id"`
	Name        string `json:"name"`
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
	code, body := g.requestPrefix(method, path, data, headers, limit+1)
	check(int64(len(body)) <= limit, "تجاوز رد OneDrive الحجم المتوقع.")
	return code, body
}

// قراءة محدودة عند التحقق من المحتوى، حتى لو كان الجزء التالف أطول بكثير من المتوقع.
func (g *drive) requestPrefix(method, path string, data []byte, headers map[string]string, limit int64) (int, []byte) {
	endpoint := graph + path
	if path == "$batch" {
		endpoint = "https://graph.microsoft.com/v1.0/$batch"
	}
	req, e := http.NewRequest(method, endpoint, bytes.NewReader(data))
	must(e)
	req.Header.Set("Authorization", "Bearer "+g.token)
	for k, v := range headers {
		req.Header.Set(k, v)
	}
	resp, e := g.client.Do(req)
	if e != nil {
		panic("تعذّر الاتصال بـ OneDrive. تحقق من الاتصال ثم أعد المحاولة.")
	}
	defer resp.Body.Close()
	if resp.StatusCode == 429 || resp.StatusCode == 503 {
		panic(retryAfter{retryDeadline(resp.Header.Get("Retry-After"), time.Now())})
	}
	b, e := io.ReadAll(io.LimitReader(resp.Body, limit))
	must(e)
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
	if code == 404 && !create {
		return head{}
	}
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
	if !g.claimIfCurrent(h, id, generation) {
		panic(errRace)
	}
}
func (g *drive) claimIfCurrent(h head, id, generation string) bool {
	if generation != "" {
		id = "restore/" + generation + "/" + id
	}
	c, b := g.request("PATCH", "items/"+url.PathEscape(h.ID), encode(Object{"description": id}), map[string]string{"If-Match": h.ETag, "Content-Type": "application/json"}, 1<<20)
	if c == 412 {
		return false
	}
	ok(c)
	var updated item
	must(decode(b, &updated))
	check(updated.Description == id, "OneDrive did not confirm the revision")
	return true
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

// الحد الإضافي يكشف المحتوى الأطول من المتوقع حتى إذا بدأت بايتاته بالجزء الصحيح.
func (g *drive) blobMatches(path, hash string, size int) (exists, matches bool) {
	code, actual := g.requestPrefix("GET", path+":/content", nil, nil, int64(size)+1)
	if code == 404 {
		return false, false
	}
	ok(code)
	return true, len(actual) == size && sum(actual) == hash
}

// عند اختلاف الحجم الوصفي نتحقق من البايتات نفسها قبل إصلاح أي ملف في السحابة.
func (g *drive) ensureBlob(prefix, hash string, data []byte, inspect bool) {
	check(validHash(hash) && sum(data) == hash, "تغيّر ملف المزامنة أثناء التجهيز.")
	name := prefix + hash
	path := g.root + name
	for attempt := 0; attempt < 3; attempt++ {
		if !inspect {
			code, _ := g.request("PUT", path+":/content?@microsoft.graph.conflictBehavior=fail", data, map[string]string{"Content-Type": "application/octet-stream"}, 1<<20)
			if code != 409 && code != 412 {
				ok(code)
				return
			}
		}
		code, meta := g.request("GET", path+"?$select=id,name,eTag", nil, nil, 1<<20)
		if code == 404 {
			inspect = false
			continue
		}
		ok(code)
		var current item
		must(decode(meta, &current))
		exists, matches := g.blobMatches(path, hash, len(data))
		if !exists {
			inspect = false
			continue
		}
		if matches {
			return
		}
		check(current.ID != "" && current.Name == name && current.ETag != "", "تعذّر التأكد من هوية جزء المزامنة في OneDrive. أُوقفت المزامنة لحماية البيانات.")
		code, _ = g.request("PUT", "items/"+url.PathEscape(current.ID)+"/content", data,
			map[string]string{"Content-Type": "application/octet-stream", "If-Match": current.ETag}, 1<<20)
		if code == 412 || code == 404 {
			inspect = true
			continue
		}
		ok(code)
		g.get(prefix, hash, int64(len(data)))
		return
	}
	panic("تغيّر جزء المزامنة في OneDrive أثناء التحقق. أعد المزامنة.")
}

func (g *drive) put(prefix string, b []byte) string {
	id := sum(b)
	path := g.root + prefix + id
	code, data := g.request("GET", path+"?$select=size", nil, nil, 1<<20)
	if code == 200 {
		var i item
		must(decode(data, &i))
		if i.Size != int64(len(b)) {
			g.ensureBlob(prefix, id, b, true)
		}
		return id
	}
	if code != 404 {
		ok(code)
	}
	g.ensureBlob(prefix, id, b, false)
	return id
}

type fileIndex struct {
	FileName string   `json:"fileName,omitempty"`
	FileID   string   `json:"fileID,omitempty"`
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

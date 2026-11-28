package workspace

import (
	"crypto/sha256"
	"encoding/hex"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"
)

const maxBlock = 8 << 20
const (
	Upload   = 1
	Download = 2
)

func (g *drive) progress(phase int, done, total int64) {
	if g.onProgress != nil {
		g.onProgress(phase, done, total)
	}
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

type blockRef struct {
	offset int64
	size   int
}

func scanBlocks(file *os.File, visit func(string, blockRef), digest io.Writer) {
	buffer := make([]byte, 256<<10)
	block := make([]byte, 0, maxBlock)
	var rolling uint64
	var offset int64
	flush := func() {
		visit(sum(block), blockRef{offset, len(block)})
		offset += int64(len(block))
		block = block[:0]
		rolling = 0
	}
	for {
		n, err := file.Read(buffer)
		if digest != nil {
			_, e := digest.Write(buffer[:n])
			must(e)
		}
		for _, b := range buffer[:n] {
			block = append(block, b)
			rolling = (rolling << 1) + gear[b]
			if len(block) >= maxBlock || len(block) >= 1<<20 && rolling&0x1fffff == 0 {
				flush()
			}
		}
		if err == io.EOF {
			break
		}
		must(err)
	}
	if len(block) > 0 {
		flush()
	}
}

// يتحقق طلب Graph مجمّع واحد من عشرين جزءاً، بما فيها الأجزاء التي ربما حذفها المستخدم من السحابة.
func (g *drive) missingBlocks(ids []string, refs map[string]blockRef) []string {
	var missing []string
	for len(ids) > 0 {
		group := ids[:min(20, len(ids))]
		ids = ids[len(group):]
		requests := make([]Object, 0, len(group))
		for _, id := range group {
			requests = append(requests, Object{"id": id, "method": "GET", "url": "/me/drive/" + g.root + chunkPrefix + id + "?$select=size"})
		}
		code, body := g.request("POST", "$batch", encode(Object{"requests": requests}), map[string]string{"Content-Type": "application/json"}, 1<<20)
		ok(code)
		var reply struct {
			Responses []struct {
				ID      string
				Status  int
				Headers map[string]string
				Body    item
			}
		}
		must(decode(body, &reply))
		seen := map[string]bool{}
		allowed := map[string]bool{}
		for _, id := range group {
			allowed[id] = true
		}
		for _, r := range reply.Responses {
			check(allowed[r.ID] && !seen[r.ID], "Invalid batch response")
			seen[r.ID] = true
			switch r.Status {
			case 404:
				missing = append(missing, r.ID)
			case 200:
				check(r.Body.Size == int64(refs[r.ID].size), "OneDrive block size mismatch")
			case 429, 503:
				var delay string
				for key, value := range r.Headers {
					if strings.EqualFold(key, "Retry-After") {
						delay = value
					}
				}
				panic(retryAfter{retryDeadline(delay, time.Now())})
			default:
				ok(r.Status)
			}
		}
		check(len(seen) == len(group), "Incomplete batch response")
	}
	return missing
}

func (g *drive) upload(path, previous string) string {
	defer g.setTransferring(false)
	f, e := os.Open(path)
	must(e)
	defer f.Close()
	idx := fileIndex{Format: 1, Previous: previous, At: time.Now().UnixMilli()}
	refs := map[string]blockRef{}
	var ids []string
	h := sha256.New()
	scanBlocks(f, func(id string, r blockRef) {
		idx.Blocks = append(idx.Blocks, id)
		idx.Size += int64(r.size)
		if _, exists := refs[id]; !exists {
			refs[id] = r
			ids = append(ids, id)
		}
	}, h)
	idx.SHA = hex.EncodeToString(h.Sum(nil))
	missing := g.missingBlocks(ids, refs)
	var total, done int64
	for _, id := range missing {
		total += int64(refs[id].size)
	}
	if len(missing) > 0 {
		g.setTransferring(true)
		g.progress(Upload, 0, total)
	}
	// ننتظر الطلبات الجارية عند الفشل قبل إنهاء المعاملة؛ لا تُعتمد نسخة جزئية.
	type result struct {
		size    int
		failure any
	}
	for len(missing) > 0 {
		group := missing[:min(3, len(missing))]
		missing = missing[len(group):]
		completed := make(chan result, len(group))
		for _, id := range group {
			go func() {
				r := refs[id]
				defer func() { completed <- result{r.size, recover()} }()
				data := make([]byte, r.size)
				_, e := f.ReadAt(data, r.offset)
				must(e)
				check(sum(data) == id, "تغيّر ملف المزامنة أثناء الرفع.")
				path := g.root + chunkPrefix + id
				code, _ := g.request("PUT", path+":/content?@microsoft.graph.conflictBehavior=fail", data, map[string]string{"Content-Type": "application/octet-stream"}, 1<<20)
				if code == 409 {
					g.get(chunkPrefix, id, maxBlock)
				} else {
					ok(code)
				}
			}()
		}
		var failure any
		for range group {
			r := <-completed
			if r.failure != nil {
				if wait, ok := r.failure.(retryAfter); ok {
					if prior, ok := failure.(retryAfter); !ok || wait.until.After(prior.until) {
						failure = wait
					}
				} else if failure == nil {
					failure = r.failure
				}
			} else {
				done += int64(r.size)
				g.progress(Upload, done, total)
			}
		}
		if failure != nil {
			panic(failure)
		}
	}
	return g.put(indexPrefix, encode(idx))
}

func (g *drive) download(id, dest string) {
	idx := g.indexFor(id)
	check(idx.Size > 0 && len(idx.Blocks) > 0 && int64(len(idx.Blocks)-1) <= idx.Size/(1<<20), "Invalid OneDrive index")
	needed := map[string]bool{}
	for _, hash := range idx.Blocks {
		check(validHash(hash), "Invalid block hash")
		needed[hash] = true
	}
	type localPart struct {
		file *os.File
		ref  blockRef
	}
	local := map[string]localPart{}
	for _, src := range g.sources {
		if len(local) == len(needed) {
			break
		}
		f, e := os.Open(src)
		if os.IsNotExist(e) {
			continue
		}
		must(e)
		defer f.Close()
		scanBlocks(f, func(hash string, r blockRef) {
			if needed[hash] {
				local[hash] = localPart{f, r}
			}
		}, nil)
	}
	if g.cacheDir != "" {
		must(os.MkdirAll(g.cacheDir, 0700))
		files, e := os.ReadDir(g.cacheDir)
		must(e)
		for _, file := range files {
			if !needed[file.Name()] && (validHash(file.Name()) || strings.HasPrefix(file.Name(), ".sync-")) {
				must(os.Remove(filepath.Join(g.cacheDir, file.Name())))
			}
		}
	}
	f, e := os.OpenFile(dest, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	must(e)
	defer f.Close()
	h := sha256.New()
	var total int64
	defer g.setTransferring(false)
	for _, hash := range idx.Blocks {
		var data []byte
		if part, ok := local[hash]; ok {
			data = make([]byte, part.ref.size)
			_, e := part.file.ReadAt(data, part.ref.offset)
			if e != nil || sum(data) != hash {
				data = nil
			}
		}
		cache := filepath.Join(g.cacheDir, hash)
		if data == nil && g.cacheDir != "" {
			if info, e := os.Stat(cache); e == nil && info.Size() <= maxBlock {
				data, e = os.ReadFile(cache)
				must(e)
				if sum(data) != hash {
					data = nil
				}
			}
		}
		if data == nil {
			g.setTransferring(true)
			g.progress(Download, total, idx.Size)
			data = g.get(chunkPrefix, hash, maxBlock)
			if g.cacheDir != "" {
				must(atomicFile(cache, data))
			}
		}
		total += int64(len(data))
		check(total <= idx.Size, "Incomplete download")
		_, e = f.Write(data)
		must(e)
		h.Write(data)
		if g.transferring {
			g.progress(Download, total, idx.Size)
		}
	}
	check(total == idx.Size && hex.EncodeToString(h.Sum(nil)) == idx.SHA, "Invalid archive checksum")
	must(f.Sync())
	must(f.Close())
	// نحذف الأجزاء المؤقتة بعد تثبيت ملف النسخة في مجلد الجلسة (أو بعد إقرارها).
}

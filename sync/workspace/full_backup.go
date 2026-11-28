package workspace

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// جلسة Graph ترفع ملف ZIP واحداً. يحفظ المؤشر آخر ملف مكتمل فقط.
func backupID() string {
	var id [16]byte
	_, err := rand.Read(id[:])
	must(err)
	return hex.EncodeToString(id[:])
}

func (g *drive) uploadBackup(path, previous string) fileIndex {
	f, err := os.Open(path)
	must(err)
	defer f.Close()
	info, err := f.Stat()
	must(err)
	size := info.Size()
	check(size > 0 && info.Mode().IsRegular(), "ملف النسخة الاحتياطية فارغ.")
	name := "payroll-backup-" + time.Now().UTC().Format("20060102-150405") + "-" + backupID() + ".zip"
	code, b := g.request("POST", g.root+name+":/createUploadSession", encode(Object{"item": Object{"@microsoft.graph.conflictBehavior": "fail", "name": name}}), map[string]string{"Content-Type": "application/json"}, 1<<20)
	ok(code)
	var session struct{ UploadURL string }
	must(decode(b, &session))
	u, err := url.Parse(session.UploadURL)
	check(err == nil && u.Scheme == "https" && u.Host != "" && u.User == nil, "تعذّر فتح جلسة رفع الملف.")
	const fragment int64 = 10 << 20 // مضاعف 320 كيبيبايت كما تتطلب جلسات Graph.
	digest := sha256.New()
	client := *g.client
	client.Timeout = 30 * time.Minute
	client.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	g.setTransferring(true)
	defer g.setTransferring(false)
	g.progress(Upload, 0, size)
	var saved item
	buffer := make([]byte, min(fragment, size))
	for start := int64(0); start < size; {
		n := min(fragment, size-start)
		end := start + n
		payload := buffer[:n]
		_, err := io.ReadFull(f, payload)
		must(err)
		digest.Write(payload)
		req, err := http.NewRequest("PUT", u.String(), bytes.NewReader(payload))
		must(err)
		req.ContentLength = n
		req.Header.Set("Content-Range", fmt.Sprintf("bytes %d-%d/%d", start, end-1, size))
		resp, err := client.Do(req)
		check(err == nil, "انقطع رفع النسخة. أعد الرفع عند توفر الاتصال.")
		body, err := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
		resp.Body.Close()
		must(err)
		if resp.StatusCode == 429 || resp.StatusCode == 503 {
			panic(retryAfter{retryDeadline(resp.Header.Get("Retry-After"), time.Now())})
		}
		if end < size {
			check(resp.StatusCode == 202, "لم يؤكّد OneDrive استلام جزء الرفع.")
			var pending struct{ NextExpectedRanges []string }
			must(decode(body, &pending))
			check(len(pending.NextExpectedRanges) > 0 && strings.HasPrefix(pending.NextExpectedRanges[0], fmt.Sprintf("%d-", end)), "تعذّر تأكيد موضع استئناف رفع النسخة.")
		} else {
			check(resp.StatusCode == 201 || resp.StatusCode == 200, "لم يؤكّد OneDrive اكتمال رفع الملف.")
			must(decode(body, &saved))
			check(saved.ID != "" && saved.Name == name && saved.Size == size, "حجم النسخة المرفوعة أو اسمها غير مطابق.")
		}
		start = end
		g.progress(Upload, start, size)
	}
	return fileIndex{Format: 2, FileID: saved.ID, At: time.Now().UnixMilli(), Kind: "backup", Previous: previous, Size: size, SHA: hex.EncodeToString(digest.Sum(nil))}
}

func (g *drive) downloadBackup(idx fileIndex, dest string) {
	check(idx.Size > 0 && idx.Size < 1<<48 && idx.FileID != "" && validHash(idx.SHA), "فهرس النسخة غير صالح.")
	client := *g.client
	client.Timeout = 0
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	idle := time.AfterFunc(2*time.Minute, cancel)
	defer idle.Stop()
	req, err := http.NewRequestWithContext(ctx, "GET", graph+"items/"+url.PathEscape(idx.FileID)+"/content", nil)
	must(err)
	req.Header.Set("Authorization", "Bearer "+g.token)
	req.Header.Set("Accept-Encoding", "identity")
	resp, err := client.Do(req)
	check(err == nil, "تعذّر تنزيل النسخة من OneDrive.")
	defer resp.Body.Close()
	if resp.StatusCode == 429 || resp.StatusCode == 503 {
		panic(retryAfter{retryDeadline(resp.Header.Get("Retry-After"), time.Now())})
	}
	check(resp.StatusCode == 200, "تعذّر استلام ملف النسخة من OneDrive.")
	file, err := os.OpenFile(dest, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	must(err)
	complete := false
	defer func() {
		file.Close()
		if !complete {
			os.Remove(dest)
		}
	}()
	h := sha256.New()
	w := io.MultiWriter(file, h)
	g.setTransferring(true)
	defer g.setTransferring(false)
	g.progress(Download, 0, idx.Size)
	buf := make([]byte, 256<<10)
	var done int64
	last := time.Now()
	for {
		n, e := resp.Body.Read(buf)
		if n > 0 {
			idle.Reset(2 * time.Minute)
			done += int64(n)
			check(done <= idx.Size, "تجاوز الملف حجم النسخة المتوقع.")
			_, err = w.Write(buf[:n])
			must(err)
			if time.Since(last) >= 150*time.Millisecond {
				g.progress(Download, done, idx.Size)
				last = time.Now()
			}
		}
		if e == io.EOF {
			break
		}
		check(e == nil, "انقطع استلام النسخة. أعد الاستيراد عند توفر الاتصال.")
	}
	check(done == idx.Size && hex.EncodeToString(h.Sum(nil)) == idx.SHA, "لم تتطابق بصمة النسخة المستلمة. بقيت بيانات الجهاز محفوظة.")
	must(file.Sync())
	must(file.Close())
	complete = true
	g.progress(Download, done, idx.Size)
}

type backupReceipt struct{ Transaction, Revision, Generation string }

func receiveBackup(r Request, g *drive, h head) Result {
	revision := h.Hash
	check(revision != "", "لا توجد نسخة مكتملة على OneDrive. ارفع نسخة من الجهاز الذي يحتوي بياناتك.")
	if r.Revision != "" {
		check(r.Head == h.Hash, "تغيّر سجل النسخ. حدّث القائمة.")
		g.findRevision(h.Hash, r.Revision)
		revision = r.Revision
	}
	transaction := backupID()
	file := filepath.Join(r.Dir, "received-"+transaction+".zip")
	complete := false
	defer func() {
		if !complete {
			os.Remove(file)
		}
	}()
	g.download(revision, file)
	must(atomicFile(filepath.Join(r.Dir, "received.json"), encode(backupReceipt{transaction, revision, h.Generation})))
	complete = true
	return Result{Status: "downloaded", File: file, Transaction: transaction}
}

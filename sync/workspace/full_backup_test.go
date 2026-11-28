package workspace

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

type backupGraph struct {
	*mockDrive
	name               string
	staged             []byte
	failAfter          int
	badDownload        bool
	loseRace           bool
	uploads, downloads int
}

func (s *backupGraph) open(token string) *drive {
	d := s.mockDrive.open(token)
	d.client = &http.Client{Transport: s}
	return d
}
func response(code int, b []byte) *http.Response {
	return &http.Response{StatusCode: code, Body: io.NopCloser(bytes.NewReader(b)), Header: http.Header{}}
}
func (s *backupGraph) RoundTrip(r *http.Request) (*http.Response, error) {
	p := r.URL.Path
	if r.URL.Host == "upload.test" {
		check(r.Header.Get("Authorization") == "", "token sent to upload session")
		var from, to, total int64
		_, e := fmt.Sscanf(r.Header.Get("Content-Range"), "bytes %d-%d/%d", &from, &to, &total)
		must(e)
		check(from == int64(len(s.staged)) && r.ContentLength == to-from+1, "wrong upload offset")
		if s.failAfter > 0 && len(s.staged) >= s.failAfter {
			return response(500, nil), nil
		}
		fragment, e := io.ReadAll(r.Body)
		must(e)
		s.staged = append(s.staged, fragment...)
		if to+1 < total {
			check(len(fragment)%(320<<10) == 0, "unaligned fragment")
			return response(202, encode(Object{"nextExpectedRanges": []string{strconv.FormatInt(to+1, 10) + "-"}})), nil
		}
		id := "backup-" + strconv.Itoa(s.uploads)
		s.blobs[id] = s.staged
		s.uploads++
		if s.loseRace {
			s.revision++
			s.loseRace = false
		}
		return response(201, encode(Object{"name": s.name, "size": len(s.staged), "id": id})), nil
	}
	if strings.HasSuffix(p, ":/createUploadSession") {
		check(r.Method == "POST" && r.Header.Get("Authorization") == "Bearer test", "bad upload session")
		s.name = strings.TrimSuffix(strings.TrimPrefix(p, "/v1.0/me/drive/items/folder:/"), ":/createUploadSession")
		s.staged = nil
		return response(200, encode(Object{"uploadUrl": "https://upload.test/resumable"})), nil
	}
	if strings.HasPrefix(p, "/v1.0/me/drive/items/backup-") && r.Method == "DELETE" {
		delete(s.blobs, strings.TrimPrefix(p, "/v1.0/me/drive/items/"))
		return response(204, nil), nil
	}
	if strings.HasPrefix(p, "/v1.0/me/drive/items/backup-") && strings.HasSuffix(p, "/content") {
		check(r.Method == "GET" && r.Header.Get("Authorization") == "Bearer test", "bad download")
		id := strings.TrimSuffix(strings.TrimPrefix(p, "/v1.0/me/drive/items/"), "/content")
		b := s.blobs[id]
		s.downloads++
		if s.badDownload {
			b = bytes.Clone(b)
			b[len(b)-1] ^= 255
		}
		return response(200, b), nil
	}
	return s.mockDrive.RoundTrip(r)
}

func TestFullBackupsOneWayHistoryAndAtomicFailure(t *testing.T) {
	server := &backupGraph{mockDrive: &mockDrive{blobs: map[string][]byte{}}}
	sender := Request{Action: "backup-upload", Token: "test", Dir: t.TempDir(), Stamp: "source"}
	phone := Request{Action: "backup-download", Token: "test", Dir: t.TempDir(), Stamp: "independent local edits"}
	fail := func(r Request) {
		t.Helper()
		defer func() {
			if recover() == nil {
				t.Fatal("unexpected success", r.Action)
			}
		}()
		runWith(r, server.open)
	}
	fail(phone)
	if server.marker {
		t.Fatal("read created cloud state")
	}
	legacy := sender
	legacy.Local = fixture(t, map[string]string{"legacy": "10"})
	syncTest(t, server.mockDrive, legacy, "")
	old := server.hash
	imported := runWith(phone, server.open)
	if imported.Status != "downloaded" || server.hash != old {
		t.Fatal("failed to read legacy snapshot")
	}
	os.Remove(imported.File)
	// ملف غير مضغوط ليتجاوز حجم طلب الرفع الواحد، مع بقاء أرشيف ZIP قابلاً للمعاينة.
	source := fixture(t, map[string]string{"new": "50"})
	f, e := os.Create(filepath.Join(t.TempDir(), "new.zip"))
	must(e)
	z := zip.NewWriter(f)
	original, e := zip.OpenReader(source)
	must(e)
	for _, member := range original.File {
		must(z.Copy(member))
	}
	original.Close()
	member, e := z.CreateHeader(&zip.FileHeader{Name: "fictional.bin", Method: zip.Store})
	must(e)
	_, e = member.Write(make([]byte, 11<<20))
	must(e)
	must(z.Close())
	must(f.Close())
	sender.Local = f.Name()
	server.failAfter = 1
	fail(sender)
	if server.hash != old || server.uploads != 0 {
		t.Fatal("partial upload replaced previous backup")
	}
	server.failAfter = 0
	server.loseRace = true
	fail(sender)
	if server.hash != old || server.blobs["backup-0"] != nil {
		t.Fatal("concurrent change overwritten or failed upload left visible")
	}
	if got := runWith(sender, server.open); got.Status != "uploaded" {
		t.Fatal(got)
	}
	newest := server.open("test").marker(false).Hash
	if newest == old || server.payloadPuts != 1 {
		t.Fatal("expected one new whole file and no new legacy blocks")
	}
	checkData := func(r Request, want string) {
		t.Helper()
		headBefore, uploads := server.hash, server.uploads
		got := runWith(r, server.open)
		a, e := os.ReadFile(got.File)
		must(e)
		b, e := os.ReadFile(want)
		must(e)
		if got.Status != "downloaded" || sha256.Sum256(a) != sha256.Sum256(b) {
			t.Fatal("backup bytes changed")
		}
		ack := Request{Action: "backup-ack", Dir: r.Dir, Stamp: "installed", Transaction: got.Transaction}
		if reply := runWith(ack, func(string) *drive { panic("ack contacted OneDrive") }); reply.Status != "imported" {
			t.Fatal(reply)
		}
		if server.hash != headBefore || server.uploads != uploads {
			t.Fatal("import modified cloud backup")
		}
		os.Remove(got.File)
	}
	checkData(phone, sender.Local)
	history := phone
	history.Action = "history"
	if got := runWith(history, server.open); len(got.Points) != 2 || got.Points[0].ID != newest || got.Points[1].ID != old {
		t.Fatal(got)
	}
	historical := phone
	historical.Revision = old
	historical.Head = newest
	checkData(historical, legacy.Local)
	server.badDownload = true
	before, e := os.ReadFile(filepath.Join(phone.Dir, "checkpoint.json"))
	must(e)
	fail(phone)
	after, e := os.ReadFile(filepath.Join(phone.Dir, "checkpoint.json"))
	must(e)
	files, _ := filepath.Glob(filepath.Join(phone.Dir, "received-*.zip"))
	if !bytes.Equal(before, after) || len(files) != 0 || server.open("test").marker(false).Hash != newest {
		t.Fatal("corruption accepted or leaked partial import")
	}
}

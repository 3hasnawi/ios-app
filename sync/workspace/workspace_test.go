package workspace

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"
)

type mockDrive struct {
	descriptions map[string]string
	rateLimited  bool
	requests     int
	blobs        map[string][]byte
	created      map[string]time.Time
	revision     int
	hash         string
	marker       bool
}

func (m *mockDrive) open(token string) *drive {
	return &drive{token: token, folder: "test-account", root: "items/folder:/", client: &http.Client{Transport: m}}
}
func (m *mockDrive) RoundTrip(r *http.Request) (*http.Response, error) {
	m.requests++
	if m.rateLimited {
		return &http.Response{StatusCode: 429, Header: http.Header{"Retry-After": {"120"}}, Body: io.NopCloser(strings.NewReader(""))}, nil
	}
	if r.Header.Get("Authorization") != "Bearer test" {
		return nil, fmt.Errorf("test access denied")
	}
	path := strings.TrimPrefix(r.URL.Path, "/v1.0/me/drive/")
	name := strings.TrimSuffix(strings.TrimPrefix(path, "items/folder:/"), ":/content")
	status := 200
	var data []byte
	metadata := func() []byte {
		return encode(Object{"id": "marker", "eTag": strconv.Itoa(m.revision), "description": m.hash})
	}
	switch {
	case name == markerName:
		if r.Method == "PUT" {
			if m.marker {
				status = 409
			} else {
				m.marker = true
				m.revision++
			}
		}
		if m.marker {
			data = metadata()
		} else {
			status = 404
		}
	case r.Method == "PATCH" && path == "items/marker":
		if r.Header.Get("If-Match") != strconv.Itoa(m.revision) {
			status = 412
			break
		}
		var payload Object
		raw, _ := io.ReadAll(r.Body)
		must(decode(raw, &payload))
		m.hash = str(payload, "description")
		m.revision++
		data = metadata()
	case r.Method == "PATCH" && strings.HasPrefix(path, "items/"+indexPrefix):
		name := strings.TrimPrefix(path, "items/")
		if r.Header.Get("If-Match") != "idx" {
			status = 412
			break
		}
		if m.descriptions == nil {
			m.descriptions = map[string]string{}
		}
		var payload item
		must(json.NewDecoder(r.Body).Decode(&payload))
		m.descriptions[name] = payload.Description
	case r.Method == "PUT":
		if _, ok := m.blobs[name]; ok {
			status = 409
		} else {
			b, _ := io.ReadAll(r.Body)
			m.blobs[name] = b
			if m.created == nil {
				m.created = map[string]time.Time{}
			}
			m.created[name] = time.Unix(int64(len(m.blobs)), 0).UTC()
		}
	default:
		b, ok := m.blobs[name]
		if !ok {
			status = 404
		} else if strings.HasSuffix(path, ":/content") {
			data = b
		} else {
			data = encode(Object{"size": len(b), "createdDateTime": m.created[name], "id": name, "eTag": "idx", "description": m.descriptions[name]})
		}
	}
	return &http.Response{StatusCode: status, Body: io.NopCloser(bytes.NewReader(data)), Header: http.Header{}}, nil
}
func fixture(t *testing.T, accounts map[string]string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "input.zip")
	f, e := os.Create(path)
	must(e)
	z := zip.NewWriter(f)
	for name, contents := range map[string]string{"manifest.json": `{"version":2}`, "settings.json": "{}", "months.jsonl": "", "staff.jsonl": "", "employees.jsonl": "", "deductions.jsonl": ""} {
		w, e := z.Create(name)
		must(e)
		_, e = w.Write([]byte(contents))
		must(e)
	}
	w, e := z.Create("accounts.jsonl")
	must(e)
	for name, iban := range accounts {
		w.Write(append(encode(Object{"entity": name, "iban": iban, "branch": "test", "swift": "test"}), '\n'))
	}
	must(z.Close())
	must(f.Close())
	return path
}
func syncTest(t *testing.T, g *mockDrive, r Request, expect string) Result {
	t.Helper()
	r.Action = "prepare"
	r.Token = "test"
	p := runWith(r, g.open)
	if expect == "conflict" {
		if p.Status != expect {
			t.Fatalf("%+v", p)
		}
		return p
	}
	if p.Status == "synced" {
		return p
	}
	if p.Status != "ready" {
		t.Fatalf("unexpected %s", p.Status)
	}
	r.Action = "publish"
	r.Transaction = p.Transaction
	v := runWith(r, g.open)
	if v.Status != "committed" {
		t.Fatal(v.Status)
	}
	saved, e := os.ReadFile(v.File)
	must(e)
	result := filepath.Join(t.TempDir(), "result.zip")
	must(os.WriteFile(result, saved, 0600))
	r.Action = "ack"
	if runWith(r, g.open).Status != "synced" {
		t.Fatal("ack")
	}
	v.File = result
	return v
}
func TestThreeDevicesOfflineMergeAndAccountConflict(t *testing.T) {
	g := &mockDrive{blobs: map[string][]byte{}}
	a := Request{Dir: t.TempDir(), Stamp: "a1", Local: fixture(t, map[string]string{"entity": "10"})}
	b := Request{Dir: t.TempDir(), Stamp: "b1", Empty: true}
	c := Request{Dir: t.TempDir(), Stamp: "c1", Empty: true}
	syncTest(t, g, a, "")
	syncTest(t, g, b, "")
	syncTest(t, g, c, "")
	a.Stamp = "a2"
	a.Local = fixture(t, map[string]string{"entity": "20", "office": "29"})
	syncTest(t, g, a, "")
	b.Stamp = "b2"
	b.Empty = false
	b.Local = fixture(t, map[string]string{"entity": "30", "home": "11"})
	conflicts := syncTest(t, g, b, "conflict").Conflicts
	if len(conflicts) != 1 || !strings.Contains(conflicts[0].Local, "30") {
		t.Fatalf("expected one readable conflict: %+v", conflicts)
	}
	b.Choices = map[string]decision{conflicts[0].Key: {Ticket: conflicts[0].Ticket, Side: "local"}}
	syncTest(t, g, b, "")
	result := syncTest(t, g, c, "")
	z, e := zip.OpenReader(result.File)
	must(e)
	defer z.Close()
	got := map[string]string{}
	readLines(&z.Reader, "accounts.jsonl", func(v Object) { got[str(v, "entity")] = str(v, "iban") })
	if got["entity"] != "30" || got["home"] != "11" || got["office"] != "29" || len(got) != 3 {
		t.Fatal(got)
	}
	if syncTest(t, g, c, "").Status != "synced" {
		t.Fatal("restart reuploaded")
	}
}
func TestNestedRefundEditsDeletionAndStaleDecision(t *testing.T) {
	build := func(a, b string) []byte {
		v := []any{}
		for i, amount := range []string{a, b} {
			if amount != "" {
				v = append(v, Object{"candidate": Object{"month": "test", "person": Object{"iban": strconv.Itoa(i)}}, "amount": amount})
			}
		}
		return encode(Object{"rows": v})
	}
	p := []string{"refund", "batch"}
	r := reconciliation{}
	base := build("10", "20")
	got := r.pick(p, base, build("12", "20"), build("10", "24"), true)
	if !bytes.Equal(got, build("12", "24")) || len(r.conflicts) != 0 {
		t.Fatal(string(got))
	}
	r.pick(p, base, build("", "20"), build("18", "20"), true)
	if len(r.conflicts) != 1 {
		t.Fatal(r.conflicts)
	}
	choice := r.conflicts[0]
	r = reconciliation{choices: map[string]decision{choice.Key: {choice.Ticket, "local"}}}
	got = r.pick(p, base, build("", "20"), build("18", "20"), true)
	if !bytes.Equal(got, build("", "20")) || len(r.conflicts) != 0 {
		t.Fatal("delete not retained")
	}
	r.pick(p, base, build("", "20"), build("19", "20"), true)
	if len(r.conflicts) != 1 {
		t.Fatal("stale choice accepted")
	}
}
func TestConditionalPublishAndCorruptDownload(t *testing.T) {
	g := &mockDrive{blobs: map[string][]byte{}}
	a := Request{Action: "prepare", Token: "test", Dir: t.TempDir(), Local: fixture(t, map[string]string{"account": "10"}), Stamp: "a"}
	b := a
	b.Dir = t.TempDir()
	pending := runWith(a, g.open)
	syncTest(t, g, b, "")
	mustFail := func(f func()) {
		t.Helper()
		defer func() {
			if recover() == nil {
				t.Fatal("unexpected success")
			}
		}()
		f()
	}
	a.Action = "publish"
	a.Transaction = pending.Transaction
	mustFail(func() { runWith(a, g.open) })
	for k := range g.blobs {
		if strings.HasPrefix(k, chunkPrefix) {
			g.blobs[k] = []byte("corrupt")
		}
	}
	mustFail(func() { runWith(Request{Dir: t.TempDir(), Action: "prepare", Token: "test", Empty: true}, g.open) })
}

// Shared by Apple restore and Windows Store validation jobs. Inputs are generated
// by the independent Python fixture encoder, including Swift's compact headers.
func TestPortableRoundTrip(t *testing.T) {
	dir := os.Getenv("PAYROLL_TEST_FIXTURES")
	if dir == "" {
		t.Skip("set PAYROLL_TEST_FIXTURES to the generated accounting fixtures")
	}
	for _, name := range []string{"refunds", "duplicate"} {
		t.Run(name, func(t *testing.T) {
			dirDB := t.TempDir()
			a, e := openIndex(filepath.Join(dirDB, "a"))
			must(e)
			defer a.close()
			source := filepath.Join(dir, name+".zip")
			archiveIndex(a, source)
			output := filepath.Join(dir, "merged-"+name+".zip")
			os.Remove(output)
			render(a, output)
			b, e := openIndex(filepath.Join(dirDB, "b"))
			must(e)
			defer b.close()
			archiveIndex(b, output)
			if !sameIndex(a, b) {
				t.Fatal("round trip changed the portable records")
			}
			input, e := zip.OpenReader(source)
			must(e)
			defer input.Close()
			result, e := zip.OpenReader(output)
			must(e)
			defer result.Close()
			var before, after Object
			must(decode(readEntry(&input.Reader, "manifest.json", 1<<20), &before))
			must(decode(readEntry(&result.Reader, "manifest.json", 1<<20), &after))
			for _, key := range []string{"payrollDigest", "deductions", "employees", "books"} {
				if fmt.Sprint(before[key]) != fmt.Sprint(after[key]) {
					t.Fatalf("%s changed", key)
				}
			}
		})
	}
}

func TestContractBinaryLedger(t *testing.T) {
	makeRow := func(id, month string) Object {
		return Object{"id": id, "month": month, "held": 12.5, "compressed": []byte{0, 255, 8}, "prepared": Object{"one": 7}, "confirmed": Object{}}
	}
	id := "00000000-0000-4000-8000-000000000001"
	other := "00000000-0000-4000-8000-000000000002"
	input := []any{makeRow(id, "first"), makeRow(other, "second")}
	db, e := openIndex(filepath.Join(t.TempDir(), "contract"))
	must(e)
	defer db.close()
	w := &batchWriter{index: db}
	defer w.stop()
	contractRows(w, plistData(Object{"version": 1, "records": input}))
	w.flush()
	path := filepath.Join(t.TempDir(), "merged.zip")
	render(db, path)
	file, e := zip.OpenReader(path)
	must(e)
	defer file.Close()
	var m Object
	must(decode(readEntry(&file.Reader, "manifest.json", 1<<20), &m))
	if number(m["contractBatchCount"]) != 2 || len(array(m["contractExecutionMarkers"])) != 2 {
		t.Fatal("contract execution was dropped")
	}
	var settings map[string][]byte
	must(decode(readEntry(&file.Reader, "settings.json", 1<<20), &settings))
	bytesValue := readPlist(settings[contractKey]).([]byte)
	actual := array(obj(readPlist(bytesValue))["records"])
	if len(actual) != 2 || !bytes.Equal(obj(actual[0])["compressed"].([]byte), []byte{0, 255, 8}) {
		t.Fatal("sealed contract data changed")
	}
}

func TestCrossPlatformAccountingExchange(t *testing.T) {
	source := os.Getenv("PAYROLL_SOURCE_ARCHIVE")
	destination := os.Getenv("PAYROLL_RENDERED_ARCHIVE")
	if source == "" || destination == "" {
		t.Skip("requires the fictional Windows accounting exchange")
	}
	dir := t.TempDir()
	a, e := openIndex(filepath.Join(dir, "source"))
	must(e)
	defer a.close()
	b, e := openIndex(filepath.Join(dir, "result"))
	must(e)
	defer b.close()
	archiveIndex(a, source)
	render(a, destination)
	archiveIndex(b, destination)
	if !sameIndex(a, b) {
		t.Fatal("the portable ledger changed after rendering")
	}
}

func TestRestoredPhoneHandleDoesNotConflictWithPaymentEdit(t *testing.T) {
	makeRow := func(handle, amount string) []byte {
		return encode(Object{"candidate": Object{"id": handle, "person": Object{"iban": "test"}}, "amount": amount})
	}
	resolver := reconciliation{}
	got := resolver.pick([]string{"refund", "batch", "rows", "test"}, makeRow("old", "10"), makeRow("new", "10"), makeRow("old", "12"), false)
	if len(resolver.conflicts) != 0 || !bytes.Equal(got, makeRow("old", "12")) {
		t.Fatal("a device-local employee handle masked a payment edit")
	}
}

func TestHistoryRestoreSafetyOfflineReviewAndUndo(t *testing.T) {
	g := &mockDrive{blobs: map[string][]byte{}}
	a := Request{Dir: t.TempDir(), Token: "test", Stamp: "1", Local: fixture(t, map[string]string{"entity": "10"})}
	syncTest(t, g, a, "")
	first := g.hash
	a.Local = fixture(t, map[string]string{"entity": "20"})
	a.Stamp = "2"
	syncTest(t, g, a, "")
	latest := g.hash
	b := Request{Dir: t.TempDir(), Token: "test", Stamp: "old-home", Empty: true}
	syncTest(t, g, b, "")
	a.Action = "history"
	history := runWith(a, g.open)
	if len(history.Points) != 2 || history.Points[0].ID != latest || history.Head != latest {
		t.Fatal(history)
	}
	a.Until = history.Points[1].At
	if len(runWith(a, g.open).Points) != 1 {
		t.Fatal("date filter")
	}
	a.Until = 0
	a.Head = latest
	a.Revision = first
	a.Action = "preview"
	if runWith(a, g.open).Preview == nil || g.hash != latest {
		t.Fatal("preview modified cloud")
	}
	restore := func(r Request) Result {
		r.Action = "restore"
		p := runWith(r, g.open)
		r.Action = "publish"
		r.Transaction = p.Transaction
		v := runWith(r, g.open)
		if !v.Apply {
			t.Fatal("restore not applied")
		}
		r.Action = "ack"
		runWith(r, g.open)
		return v
	}
	expectError := func(r Request) {
		t.Helper()
		defer func() {
			if recover() == nil {
				t.Fatal("stale restore succeeded")
			}
		}()
		runWith(r, g.open)
	}
	stale := a
	stale.Action = "restore"
	stale.Stamp = "unsynced"
	expectError(stale)
	stale.Stamp = a.Stamp
	stale.Head = first
	expectError(stale)
	restore(a)
	a.Action = "history"
	history = runWith(a, g.open)
	if len(history.Points) != 4 || history.Points[0].Kind != "restore" || history.Points[1].Kind != "beforeRestore" || history.Points[2].ID != latest {
		t.Fatal(history)
	}
	safeguard := history.Points[1].ID
	d := g.open("test")
	if d.indexFor(safeguard).SHA != d.indexFor(latest).SHA || d.indexFor(history.Head).SHA != d.indexFor(first).SHA {
		t.Fatal("wrong restore payload")
	}
	// A previously offline device must explicitly review even a new, nonoverlapping entry.
	b.Empty = false
	b.Local = fixture(t, map[string]string{"entity": "20", "offline": "30"})
	b.Stamp = "offline-edit"
	c := syncTest(t, g, b, "conflict").Conflicts
	if len(c) != 1 || !strings.Contains(c[0].Key, "offline") {
		t.Fatal(c)
	}
	b.Choices = map[string]decision{c[0].Key: {c[0].Ticket, "remote"}}
	syncTest(t, g, b, "")
	a.Head = history.Head
	a.Revision = safeguard
	restore(a)
	if d.indexFor(d.marker(false).Hash).SHA != d.indexFor(latest).SHA {
		t.Fatal("undo failed")
	}
}

func TestBeforeImportAndDeleteNotesFromOfflineDevice(t *testing.T) {
	g := &mockDrive{blobs: map[string][]byte{}}
	r := Request{Dir: t.TempDir(), Token: "test", Local: fixture(t, map[string]string{"office": "10"}), Stamp: "1"}
	syncTest(t, g, r, "")
	previous := g.hash
	r.Action = "note"
	for _, kind := range []string{"import", "delete"} {
		r.Reason = kind
		runWith(r, func(string) *drive { panic("note contacted OneDrive") })
	}
	r.Stamp = "2"
	r.Local = fixture(t, map[string]string{"office": "20"})
	syncTest(t, g, r, "")
	r.Dir = t.TempDir()
	r.Action = "history"
	history := runWith(r, g.open)
	if len(history.Points) != 2 || history.Points[1].ID != previous || len(history.Points[1].Notes) != 2 {
		t.Fatal(history)
	}
}
func TestThrottleStopsRequestsUntilServerDeadline(t *testing.T) {
	g := &mockDrive{rateLimited: true}
	r := Request{Dir: t.TempDir(), Action: "prepare", Empty: true, Token: "test"}
	for attempt := 0; attempt < 2; attempt++ {
		func() {
			defer func() {
				p, ok := recover().(retryAfter)
				if !ok || time.Until(p.until) < 119*time.Second {
					t.Fatal("bad retry delay")
				}
			}()
			runWith(r, g.open)
		}()
	}
	if g.requests != 1 {
		t.Fatal("retried during backoff", g.requests)
	}
}

func TestFinancialHistoryPreview(t *testing.T) {
	dir := os.Getenv("PAYROLL_TEST_FIXTURES")
	if dir == "" {
		t.Skip("requires the synthetic Windows archive")
	}
	g := &mockDrive{blobs: map[string][]byte{}}
	r := Request{Dir: t.TempDir(), Token: "test", Stamp: "1", Local: filepath.Join(dir, "refunds.zip")}
	syncTest(t, g, r, "")
	r.Head = g.hash
	r.Revision = g.hash
	r.Action = "preview"
	p := runWith(r, g.open).Preview
	if p == nil || p.Staff != 0 || p.Employees != 1 || p.Refunds != 1 || p.Net != "100000.00" || p.Deductions != "2260000.00" {
		t.Fatal(p)
	}
}

func TestTransferIndicatorTracksPayload(t *testing.T) {
	server := &mockDrive{blobs: map[string][]byte{}}
	d := server.open("test")
	var events []bool
	d.onTransfer = func(active bool) { events = append(events, active) }
	checkEvents := func(expected ...bool) {
		t.Helper()
		if !slices.Equal(events, expected) {
			t.Fatalf("indicator events %v, want %v", events, expected)
		}
		events = nil
	}
	path := fixture(t, map[string]string{"office": "10"})
	d.marker(true)
	checkEvents()
	id := d.upload(path, "")
	checkEvents(true, false)
	d.indexFor(id)
	d.upload(path, id)
	checkEvents()
	d.download(id, filepath.Join(t.TempDir(), "received.zip"))
	checkEvents(true, false)
	idx := d.indexFor(id)
	delete(server.blobs, chunkPrefix+idx.Blocks[0])
	func() {
		defer func() {
			if recover() == nil {
				t.Fatal("missing payload was accepted")
			}
		}()
		d.download(id, filepath.Join(t.TempDir(), "failed.zip"))
	}()
	checkEvents(true, false)
}

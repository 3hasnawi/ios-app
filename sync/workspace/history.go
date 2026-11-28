package workspace

import (
	"archive/zip"
	"encoding/json"
	"math/big"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

type historyPoint struct {
	ID    string   `json:"id"`
	At    int64    `json:"at"`
	Kind  string   `json:"kind"`
	Notes []string `json:"notes,omitempty"`
}
type historyPreview struct {
	Staff      int         `json:"staff"`
	Employees  int         `json:"employees"`
	Refunds    int         `json:"refunds"`
	Net        json.Number `json:"net"`
	Deductions json.Number `json:"deductions"`
}

func (g *drive) indexFor(id string) fileIndex {
	var idx fileIndex
	must(decode(g.get(indexPrefix, id, 4<<20), &idx))
	check(idx.Format == 1 && validHash(idx.SHA) && (idx.Previous == "" || validHash(idx.Previous)), "Invalid OneDrive history index")
	return idx
}
func (g *drive) point(id string, idx fileIndex) historyPoint {
	code, b := g.request("GET", g.root+indexPrefix+id+"?$select=createdDateTime,description", nil, nil, 1<<20)
	ok(code)
	var metadata struct {
		Description string    `json:"description"`
		Created     time.Time `json:"createdDateTime"`
	}
	must(decode(b, &metadata))
	check(!metadata.Created.IsZero(), "تعذرت قراءة تاريخ النقطة من OneDrive.")
	p := historyPoint{ID: id, At: metadata.Created.UnixMilli(), Kind: idx.Kind}
	for _, reason := range []string{"import", "delete"} {
		if hasNote(metadata.Description, reason) {
			p.Notes = append(p.Notes, reason)
		}
	}
	return p
}
func (g *drive) findRevision(current, target string) historyPoint {
	check(validHash(target), "اختر نقطة من سجل المزامنة.")
	for current != "" {
		idx := g.indexFor(current)
		if current == target {
			return g.point(current, idx)
		}
		current = idx.Previous
	}
	panic("هذه النقطة غير موجودة في سجل الحساب الحالي.")
}
func (g *drive) history(h head, r Request) Result {
	id := h.Hash
	if r.Cursor != "" {
		check(h.Hash == r.Head && validHash(r.Cursor), "تغيّر سجل المزامنة. حدّث القائمة.")
		id = r.Cursor
	}
	result := Result{Status: "history", Head: h.Hash}
	for id != "" && len(result.Points) < 15 {
		idx := g.indexFor(id)
		point := g.point(id, idx)
		id = idx.Previous
		if r.Until == 0 || point.At <= r.Until {
			result.Points = append(result.Points, point)
		}
	}
	result.Cursor = id
	return result
}
func (g *drive) preview(h head, r Request) Result {
	check(h.Hash == r.Head, "تغيّر سجل المزامنة. حدّث القائمة.")
	g.findRevision(h.Hash, r.Revision)
	dir, e := os.MkdirTemp(r.Dir, "preview-")
	must(e)
	defer os.RemoveAll(dir)
	path := filepath.Join(dir, "snapshot.zip")
	g.download(r.Revision, path)
	z, e := zip.OpenReader(path)
	must(e)
	defer z.Close()
	var manifest Object
	must(decode(readEntry(&z.Reader, "manifest.json", 1<<20), &manifest))
	var p historyPreview
	if v := manifest["refundBatchCount"]; v != nil {
		p.Refunds = number(v)
	}
	if v := manifest["staff"]; v != nil {
		p.Staff = number(v)
	} else {
		readLines(&z.Reader, "staff.jsonl", func(Object) { p.Staff++ })
	}
	var net, deductions big.Rat
	add := func(total *big.Rat, v any) {
		if v == nil {
			return
		}
		n, ok := v.(json.Number)
		check(ok, "Invalid archive amount")
		var amount big.Rat
		_, ok = amount.SetString(n.String())
		check(ok, "Invalid archive amount")
		total.Add(total, &amount)
	}
	readLines(&z.Reader, "employees.jsonl", func(row Object) { p.Employees++; add(&net, row["net"]) })
	readLines(&z.Reader, "deductions.jsonl", func(row Object) { add(&deductions, row["amount"]) })
	p.Net = json.Number(net.FloatString(2))
	p.Deductions = json.Number(deductions.FloatString(2))
	return Result{Status: "preview", Head: h.Hash, Preview: &p}
}

// تُحفظ علامة آخر مزامنة ناجحة محلياً حتى يعود الاتصال، دون تأخير الاستيراد بطلب شبكي.
func rememberPoint(r Request) Result {
	check(r.Reason == "import" || r.Reason == "delete", "Invalid sync note")
	var s checkpoint
	if load(filepath.Join(r.Dir, "checkpoint.json"), &s) && validHash(s.Base) {
		must(atomicFile(filepath.Join(r.Dir, "note-"+s.Base+"-"+r.Reason), nil))
	}
	return Result{Status: "noted"}
}
func hasNote(description, reason string) bool {
	for _, line := range strings.Split(description, "\n") {
		if line == "payroll-before-"+reason {
			return true
		}
	}
	return false
}
func (g *drive) flushNotes(dir string) {
	names, e := filepath.Glob(filepath.Join(dir, "note-*-*"))
	must(e)
	for _, name := range names {
		p := strings.Split(filepath.Base(name), "-")
		if len(p) != 3 || !validHash(p[1]) || (p[2] != "import" && p[2] != "delete") {
			continue
		}
		code, b := g.request("GET", g.root+indexPrefix+p[1]+"?$select=id,eTag,description", nil, nil, 1<<20)
		ok(code)
		var i item
		must(decode(b, &i))
		if !hasNote(i.Description, p[2]) {
			check(i.ID != "" && i.ETag != "", "Invalid revision metadata")
			description := strings.TrimSpace(i.Description + "\npayroll-before-" + p[2])
			code, _ = g.request("PATCH", "items/"+url.PathEscape(i.ID), encode(Object{"description": description}), map[string]string{"Content-Type": "application/json", "If-Match": i.ETag}, 1<<20)
			if code == 412 {
				continue
			}
			ok(code)
		}
		must(os.Remove(name))
	}
}

package workspace

import (
	"archive/zip"
	"bufio"
	"bytes"
	"encoding/base64"
	"errors"
	"fmt"
	bolt "go.etcd.io/bbolt"
	"howett.net/plist"
	"io"
	"sort"
	"strings"
	"unicode/utf8"
)

const rowLimit = 64 << 20

var b64 = base64.StdEncoding

func must(e error) {
	if e != nil {
		panic(e)
	}
}
func check(ok bool, msg string) {
	if !ok {
		panic(errors.New(msg))
	}
}
func un64(s string) []byte { b, e := b64.DecodeString(s); must(e); return b }
func readEntry(z *zip.Reader, name string, limit int64) []byte {
	f, e := z.Open(name)
	must(e)
	defer f.Close()
	b, e := io.ReadAll(io.LimitReader(f, limit+1))
	must(e)
	check(int64(len(b)) <= limit, "Archive member exceeds size limit")
	return b
}

type batchWriter struct {
	index *index
	tx    *bolt.Tx
	size  int
}

func (w *batchWriter) put(k string, v []byte) {
	if w.tx == nil {
		tx, e := w.index.db.Begin(true)
		must(e)
		w.tx = tx
	}
	b := rows(w.tx)
	check(b.Get([]byte(k)) == nil, "Duplicate record identity: "+k)
	must(b.Put([]byte(k), v))
	w.size += len(v) + len(k)
	if w.size > 4<<20 {
		w.flush()
	}
}
func (w *batchWriter) flush() {
	if w.tx != nil {
		must(w.tx.Commit())
		w.tx = nil
		w.size = 0
	}
}
func (w *batchWriter) stop() {
	if w.tx != nil {
		w.tx.Rollback()
	}
}
func (w *batchWriter) group(k string, m Object) {
	// Exact duplicates are allowed for legacy payroll rows. Preserve their multiplicity.
	if w.tx == nil {
		tx, e := w.index.db.Begin(true)
		must(e)
		w.tx = tx
	}
	b := rows(w.tx)
	v := b.Get([]byte(k))
	list := [][]byte{}
	if v != nil {
		must(decode(v, &list))
	}
	list = append(list, encode(m))
	sort.Slice(list, func(i, j int) bool { return bytes.Compare(list[i], list[j]) < 0 })
	must(b.Put([]byte(k), encode(list)))
	w.size += len(list[len(list)-1]) + len(k)
	if w.size > 4<<20 {
		w.flush()
	}
}
func readLines(z *zip.Reader, name string, visit func(Object)) {
	f, e := z.Open(name)
	must(e)
	defer f.Close()
	s := bufio.NewScanner(f)
	s.Buffer(make([]byte, 65536), rowLimit)
	for s.Scan() {
		var m Object
		must(decode(s.Bytes(), &m))
		check(m != nil, "Invalid accounting row")
		visit(m)
	}
	must(s.Err())
}
func number(v any) int { var n int; _, e := fmt.Sscan(fmt.Sprint(v), &n); must(e); return n }
func settingRows(w *batchWriter, k string, raw []byte) {
	var value any
	_, e := plist.Unmarshal(raw, &value)
	must(e)
	// JSON ledgers split at stable item IDs or dictionary keys. Opaque plists retain their bytes.
	data, ok := value.([]byte)
	var v any
	if !ok || len(data) == 0 || decode(data, &v) != nil {
		w.put(recKey("setting", k), encode(raw))
		return
	}
	w.put(recKey("jsonSetting", k), encode(v))
}
func archiveIndex(i *index, path string) {
	if path == "" {
		return
	}
	z, e := zip.OpenReader(path)
	must(e)
	defer z.Close()
	var manifest Object
	must(decode(readEntry(&z.Reader, "manifest.json", 1000000), &manifest))
	ver := number(manifest["version"])
	check(ver >= 1 && (ver <= 14 && ver != 7), "Unsupported payroll archive")
	w := &batchWriter{index: i}
	defer w.stop()
	headers := []string{}
	if ver >= 12 {
		must(decode(readEntry(&z.Reader, "payroll-headers.json", 48<<20), &headers))
	}
	for _, section := range []string{"months", "employees", "deductions", "staff", "accounts"} {
		if section == "accounts" && ver < 2 {
			continue
		}
		readLines(&z.Reader, section+".jsonl", func(m Object) {
			switch section {
			case "months":
				m["syncEmployeeCount"] = m["employeeCount"]
				delete(m, "employeeCount")
				delete(m, "deductionCount")
				if n := m["syncEmployeeCount"]; n != nil && number(n) > 0 {
					delete(m, "summary")
				}
				delete(m, "syncEmployeeCount")
				w.put(recKey(section, str(m, "name")), encode(m))
			case "employees":
				if n := m["headerIndex"]; n != nil {
					idx := number(n)
					check(idx >= 0 && idx < len(headers), "Invalid payroll schema")
					header := headers[idx]
					id := sum([]byte(header))
					if w.tx == nil {
						tx, e := i.db.Begin(true)
						must(e)
						w.tx = tx
					}
					if rows(w.tx).Get([]byte(recKey("schema", id))) == nil {
						w.put(recKey("schema", id), []byte(header))
					}
					m["syncHeader"] = id
					delete(m, "headerIndex")
				}
				var detail map[string]any
				if raw := str(m, "details"); raw != "" {
					_ = decode(un64(raw), &detail)
				}
				identity := str(m, "name")
				for _, key := range []string{"الرقم الموحد", "الرقم الوظيفي"} {
					if id := strings.TrimSpace(str(detail, key)); id != "" && id != "0" {
						identity = key + " " + id
						break
					}
				}
				w.group(recKey(section, str(m, "month"), identity), m)
			case "deductions":
				note := str(m, "note")
				if strings.HasPrefix(note, "⟦حالة الاستقطاع:") {
					if n := strings.Index(note, "⟧"); n >= 0 {
						note = strings.TrimSpace(note[n+len("⟧"):])
					}
				}
				w.group(recKey(section, str(m, "month"), str(m, "name"), str(m, "title"), note), m)
			case "staff":
				id := str(m, "unified")
				if id == "" {
					id = str(m, "stat")
				}
				if strings.TrimSpace(id) != "" && id != "0" {
					w.group(recKey(section, id), m)
				} else {
					w.group(recKey(section, "", str(m, "name"), str(m, "mother")), m)
				}
			case "accounts":
				w.group(recKey(section, str(m, "entity")), m)
			}
		})
	}
	if ver >= 3 {
		var books []Object
		must(decode(readEntry(&z.Reader, "books/index.json", 10000000), &books))
		for _, book := range books {
			id := str(book, "id")
			check(validID(id), "Invalid PDF id")
			pdf := readEntry(&z.Reader, "books/"+id+".pdf", 60_000_000)
			book["syncPDF"] = sum(pdf)
			w.put(recKey("books", strings.ToUpper(id)), encode(book))
			// Content address the bytes. The chosen book metadata selects its matching PDF.
			hash := sum(pdf)
			if w.tx == nil {
				tx, e := i.db.Begin(true)
				must(e)
				w.tx = tx
			}
			if rows(w.tx).Get([]byte(recKey("pdf", hash))) == nil {
				w.put(recKey("pdf", hash), pdf)
			}
		}
	}
	if ver == 14 {
		counts := map[string]int{}
		readLines(&z.Reader, "refunds.jsonl", func(m Object) {
			if str(m, "id") != "draft" {
				var batch Object
				must(decode(un64(str(m, "contents")), &batch))
				counts[strings.ToUpper(str(batch, "batchID"))]++
			}
		})
		readLines(&z.Reader, "refunds.jsonl", func(m Object) {
			var batch Object
			must(decode(un64(str(m, "contents")), &batch))
			id := strings.ToUpper(str(batch, "batchID"))
			check(validID(id), "Invalid refund batch")
			if str(m, "id") == "draft" {
				id = "draft"
			}
			// Legacy histories can contain two archived instances with the same batchID. Keep both.
			archiveID := str(m, "id")
			if id == "draft" || counts[id] == 1 {
				archiveID = id
			}
			w.put(recKey("refund", archiveID), encode(batch))
		})
	}
	var settings map[string][]byte
	must(decode(readEntry(&z.Reader, "settings.json", 64_000_000), &settings))
	for k, raw := range settings {
		settingRows(w, k, raw)
	}
	w.flush()
}
func validID(s string) bool {
	if len(s) != 36 {
		return false
	}
	for i, c := range s {
		if i == 8 || i == 13 || i == 18 || i == 23 {
			if c != '-' {
				return false
			}
		} else if !strings.ContainsRune("abcdefABCDEF0123456789", c) {
			return false
		}
	}
	return utf8.ValidString(s)
}

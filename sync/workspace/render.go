package workspace

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"encoding/binary"
	"encoding/json"
	"errors"
	bolt "go.etcd.io/bbolt"
	"hash"
	"howett.net/plist"
	"io"
	"math"
	"os"
	"sort"
	"strings"
	"time"
)

func iterate(t *bolt.Tx, section string, f func([]string, []byte)) {
	c := rows(t).Cursor()
	prefix := string(encode([]string{section}))
	prefix = prefix[:len(prefix)-1] + ","
	for k, v := c.Seek([]byte(prefix)); k != nil && strings.HasPrefix(string(k), prefix); k, v = c.Next() {
		f(parts(k), v)
	}
}

func integer(h hash.Hash, n uint64) {
	var b [8]byte
	binary.BigEndian.PutUint64(b[:], n)
	h.Write(b[:])
}
func field(h hash.Hash, b []byte) {
	if b == nil {
		integer(h, math.MaxUint64)
	} else {
		integer(h, uint64(len(b)))
		h.Write(b)
	}
}
func render(i *index, path string) {
	f, e := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	must(e)
	defer f.Close()
	z := zip.NewWriter(f)
	tx, e := i.db.Begin(false)
	must(e)
	defer tx.Rollback()
	digests := map[string][]byte{}
	entry := func(name string, body func(io.Writer)) {
		wr, e := z.Create(name)
		must(e)
		h := sha256.New()
		body(io.MultiWriter(wr, h))
		digests[name] = h.Sum(nil)
	}
	output := func(name string, v any) { entry(name, func(w io.Writer) { _, e := w.Write(encode(v)); must(e) }) }
	line := func(w io.Writer, v any) { _, e := w.Write(append(encode(v), '\n')); must(e) }
	man := Object{"version": 14, "contentVersion": 5, "createdAt": float64(time.Now().UnixMilli())/1000 - 978307200, "hasRefundDraft": false, "refundBatchCount": 0}
	schemas := []string{}
	schemaIds := map[string]int{}
	pay := sha256.New()
	counts := map[string]map[string]int{"employees": {}, "deductions": {}}
	type stat struct {
		Count int     `json:"count"`
		Total float64 `json:"total"`
	}
	totals := map[string]map[string]stat{}
	for _, s := range []string{"employees", "deductions", "staff", "accounts"} {
		count := 0
		entry(s+".jsonl", func(w io.Writer) {
			iterate(tx, s, func(_ []string, b []byte) {
				var group [][]byte
				must(decode(b, &group))
				for _, data := range group {
					var m Object
					must(decode(data, &m))
					mo := str(m, "month")
					if counts[s] != nil {
						counts[s][mo]++
					}
					if s == "employees" {
						for _, k := range []string{"month", "name", "rank", "workplace"} {
							field(pay, []byte(str(m, k)))
						}
						if n, ok := m["net"].(json.Number); ok {
							v, e := n.Float64()
							must(e)
							integer(pay, 1)
							integer(pay, math.Float64bits(v))
						} else {
							integer(pay, 0)
						}
						for _, k := range []string{"items", "details", "rawColumns"} {
							var data []byte
							if a, ok := m[k].(string); ok {
								data = un64(a)
							}
							if k == "items" && len(data) > 0 {
								var items map[string]float64
								must(decode(data, &items))
								if totals[mo] == nil {
									totals[mo] = map[string]stat{}
								}
								for title, n := range items {
									t := totals[mo][title]
									t.Count++
									t.Total += n
									totals[mo][title] = t
								}
							}
							if k == "rawColumns" && m["syncHeader"] != nil {
								schema := rows(tx).Get([]byte(recKey("schema", str(m, "syncHeader"))))
								check(schema != nil, "Missing payroll schema")
								header := string(schema)
								idx, ok := schemaIds[header]
								if !ok {
									idx = len(schemas)
									schemaIds[header] = idx
									schemas = append(schemas, header)
								}
								m["headerIndex"] = idx
								delete(m, "syncHeader")
								offset := number(m["headerOffset"])
								check(offset >= 0 && offset <= len(data), "Invalid payroll offset")
								h := un64(header)
								integer(pay, uint64(len(data)+len(h)))
								pay.Write(data[:offset])
								pay.Write(h)
								pay.Write(data[offset:])
								continue
							}
							field(pay, data)
						}
					}
					line(w, m)
					count++
				}
			})
		})
		man[s] = count
	}
	months := []string{}
	known := map[string]bool{}
	entry("months.jsonl", func(w io.Writer) {
		iterate(tx, "months", func(p []string, b []byte) {
			var m Object
			must(decode(b, &m))
			mo := p[1]
			months = append(months, mo)
			known[mo] = true
			m["employeeCount"] = counts["employees"][mo]
			m["deductionCount"] = counts["deductions"][mo]
			if totals[mo] != nil {
				m["summary"] = encode(totals[mo])
			}
			line(w, m)
		})
	})
	for _, c := range counts {
		for mo := range c {
			check(known[mo], "Missing month for accounting rows")
		}
	}
	man["months"] = months
	man["payrollDigest"] = pay.Sum(nil)
	output("payroll-headers.json", schemas)
	books := []Object{}
	iterate(tx, "books", func(p []string, b []byte) {
		var book Object
		must(decode(b, &book))
		pdf := rows(tx).Get([]byte(recKey("pdf", str(book, "syncPDF"))))
		check(pdf != nil && sum(pdf) == str(book, "syncPDF"), "Missing or mismatched book PDF")
		delete(book, "syncPDF")
		book["id"] = p[1]
		books = append(books, book)
		entry("books/"+p[1]+".pdf", func(w io.Writer) { _, e := w.Write(pdf); must(e) })
	})
	man["books"] = len(books)
	output("books/index.json", books)
	entry("refunds.jsonl", func(w io.Writer) {
		iterate(tx, "refund", func(p []string, b []byte) {
			line(w, Object{"id": p[1], "contents": b})
			if p[1] == "draft" {
				man["hasRefundDraft"] = true
			} else {
				man["refundBatchCount"] = man["refundBatchCount"].(int) + 1
			}
		})
	})
	settings := map[string][]byte{}
	decoded := map[string]Object{}
	iterate(tx, "setting", func(p []string, b []byte) { var raw []byte; must(decode(b, &raw)); settings[p[1]] = raw })
	iterate(tx, "jsonSetting", func(p []string, b []byte) {
		check(settings[p[1]] == nil, "Conflicting setting representations")
		raw, e := plist.Marshal(bytes.Clone(b), plist.BinaryFormat)
		must(e)
		settings[p[1]] = raw
		var v any
		must(decode(b, &v))
		decoded[p[1]] = obj(v)
	})
	var contracts []any
	if rows(tx).Get([]byte(recKey("contractFormat", contractKey))) != nil {
		groups := map[string]Object{}
		iterate(tx, "contract", func(p []string, b []byte) { var group Object; must(decode(b, &group)); groups[p[1]] = group })
		settings[contractKey], contracts = applyContracts(groups)
	}
	output("settings.json", settings)
	for _, x := range []struct {
		k string
		v int
	}{{"bankStatementContractOverridesV1", 6}, {"fundingExpenseApprovalsV1", 8}, {"customTransferTemplatesV1", 9}} {
		if settings[x.k] != nil {
			man["contentVersion"] = x.v
		}
	}
	style := decoded["transferLetterCustomizations"]
	if obj(style["common"])["letterTemplate"] != nil {
		man["contentVersion"] = 10
	}
	for _, v := range obj(style["individual"]) {
		if obj(v)["letterTemplate"] != nil {
			man["contentVersion"] = 10
		}
	}
	if trust := decoded["salaryTrustLedgerV1"]; trust != nil {
		man["salaryTrustEntries"] = len(array(trust["entries"]))
		if number(trust["version"]) == 2 {
			man["contentVersion"] = 11
		}
	}

	markers := []string{}
	for _, v := range contracts {
		c := obj(v)
		id := strings.ToUpper(str(c, "id"))
		if c["cancelledAt"] != nil {
			markers = append(markers, id+"|cancelled")
		}
		for _, k := range keys(obj(c["prepared"])) {
			markers = append(markers, id+"|P|"+k)
		}
		for _, k := range keys(obj(c["confirmed"])) {
			markers = append(markers, id+"|C|"+k)
		}
	}
	sort.Strings(markers)
	man["contractExecutionMarkers"] = markers
	man["contractBatchCount"] = len(contracts)
	check(len(months)+len(books)+man["accounts"].(int)+man["staff"].(int)+man["refundBatchCount"].(int)+len(contracts)+len(array(decoded["salaryTrustLedgerV1"]["entries"])) > 0 || man["hasRefundDraft"] == true, "Empty workspace requires explicit recovery")
	delete(digests, "employees.jsonl")
	h := sha256.New()
	names := []string{}
	for k := range digests {
		names = append(names, k)
	}
	sort.Strings(names)
	for _, k := range names {
		h.Write([]byte(k))
		h.Write([]byte{0})
		h.Write(digests[k])
	}
	man["supportingDigest"] = h.Sum(nil)
	output("manifest.json", man)
	must(z.Close())
	must(f.Sync())
	must(f.Close())
}

var errRace = errors.New("تغيرت البيانات أثناء المزامنة. أعد المحاولة.")

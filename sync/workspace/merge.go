// Package workspace reconciles the portable accounting records shared by both apps.
package workspace

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	bolt "go.etcd.io/bbolt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

type Object = map[string]any

func decode(raw []byte, out any) error {
	d := json.NewDecoder(bytes.NewReader(raw))
	d.UseNumber()
	if err := d.Decode(out); err != nil {
		return err
	}
	if err := d.Decode(new(any)); err != io.EOF {
		return errors.New("invalid trailing JSON")
	}
	return nil
}
func encode(v any) []byte {
	b, e := json.Marshal(v)
	if e != nil {
		panic(e)
	}
	return b
}
func sum(b []byte) string           { s := sha256.Sum256(b); return hex.EncodeToString(s[:]) }
func str(m Object, k string) string { v, _ := m[k].(string); return v }
func obj(v any) Object              { m, _ := v.(map[string]any); return m }
func array(v any) []any             { a, _ := v.([]any); return a }
func recKey(parts ...string) string { return string(encode(parts)) }
func parts(key []byte) []string {
	var p []string
	if decode(key, &p) != nil || len(p) < 2 {
		panic("invalid record key")
	}
	return p
}
func keys(m Object) []string {
	v := make([]string, 0, len(m))
	for k := range m {
		v = append(v, k)
	}
	sort.Strings(v)
	return v
}

// Each index is disposable. Baselines are promoted only after native storage commits.
type index struct{ db *bolt.DB }

func openIndex(path string) (*index, error) {
	db, e := bolt.Open(path, 0600, nil)
	if e != nil {
		return nil, e
	}
	e = db.Update(func(t *bolt.Tx) error { _, e := t.CreateBucketIfNotExists([]byte("rows")); return e })
	if e != nil {
		db.Close()
		return nil, e
	}
	return &index{db}, nil
}
func (i *index) close()            { i.db.Close() }
func rows(t *bolt.Tx) *bolt.Bucket { return t.Bucket([]byte("rows")) }

type conflict struct {
	Key    string `json:"key"`
	Ticket string `json:"ticket"`
	Local  string `json:"local"`
	Remote string `json:"remote"`
}
type decision struct {
	Ticket string `json:"ticket"`
	Side   string `json:"side"`
}

func describe(b []byte) string {
	if b == nil {
		return "حذف"
	}
	var v any
	if decode(b, &v) != nil {
		return fmt.Sprintf("%d bytes · %s", len(b), sum(b)[:12])
	}
	if m := obj(v); m != nil {
		var fields []string
		for _, k := range []string{"name", "title", "source", "amount", "net", "iban", "bank", "month", "status", "account", "description", "rank", "workplace"} {
			if a := m[k]; a != nil {
				fields = append(fields, fmt.Sprint(a))
			}
		}
		if len(fields) > 0 {
			return strings.Join(fields, " · ")
		}
	}
	if group := groupedDescription(b); group != "" {
		return group
	}
	r := []rune(string(b))
	if len(r) > 250 {
		return string(r[:250]) + "…"
	}
	return string(r)
}

// Three sorted cursors keep payroll and PDF payloads off the heap between records.
func merge(base, local, remote, out *index, decisions map[string]decision) (conflicts []conflict, changed bool, err error) {
	indexes := []*index{base, local, remote}
	var curs [3]*bolt.Cursor
	var current, values [3][]byte
	for j, i := range indexes {
		tx, e := i.db.Begin(false)
		if e != nil {
			return nil, false, e
		}
		defer tx.Rollback()
		curs[j] = rows(tx).Cursor()
		current[j], values[j] = curs[j].First()
	}
	err = out.db.Update(func(t *bolt.Tx) error {
		for {
			var key []byte
			for _, k := range current {
				if k != nil && (key == nil || bytes.Compare(k, key) < 0) {
					key = k
				}
			}
			if key == nil {
				break
			}
			var v [3][]byte
			for j := range curs {
				if bytes.Equal(current[j], key) {
					v[j] = values[j]
				}
			}
			section := parts(key)
			resolver := reconciliation{choices: decisions}
			selected := resolver.pick(section, v[0], v[1], v[2], section[0] == "refund" || section[0] == "jsonSetting")
			conflicts = append(conflicts, resolver.conflicts...)
			if !bytes.Equal(selected, v[1]) {
				changed = true
			}
			if selected != nil {
				if e := rows(t).Put(key, selected); e != nil {
					return e
				}
			}
			for j := range curs {
				if bytes.Equal(current[j], key) {
					current[j], values[j] = curs[j].Next()
				}
			}
		}
		return nil
	})
	return
}
func atomicFile(path string, b []byte) error {
	f, e := os.CreateTemp(filepath.Dir(path), ".sync-")
	if e != nil {
		return e
	}
	defer os.Remove(f.Name())
	if _, e = f.Write(b); e == nil {
		e = f.Sync()
	}
	if c := f.Close(); e == nil {
		e = c
	}
	if e != nil {
		return e
	}
	return os.Rename(f.Name(), path)
}

package workspace

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	bolt "go.etcd.io/bbolt"
	"io"
	"os"
	"path/filepath"
	"time"
)

type Request struct {
	Action, Dir, Token, Stamp, Local, Transaction string
	Empty                                         bool
	Choices                                       map[string]decision
}
type Result struct {
	Status      string     `json:"status"`
	Error       string     `json:"error,omitempty"`
	Conflicts   []conflict `json:"conflicts,omitempty"`
	File        string     `json:"file,omitempty"`
	Transaction string     `json:"transaction,omitempty"`
	Apply       bool       `json:"apply,omitempty"`
}
type checkpoint struct{ Folder, Marker, Base, Stamp string }
type prepared struct {
	Transaction, ETag, Seen, Target, Stamp string
	Apply, Publish                         bool
}

func load(path string, v any) bool {
	b, e := os.ReadFile(path)
	if os.IsNotExist(e) {
		return false
	}
	must(e)
	must(decode(b, v))
	return true
}
func copyFile(src, dest string) {
	s, e := os.Open(src)
	must(e)
	defer s.Close()
	f, e := os.CreateTemp(filepath.Dir(dest), "copy-")
	must(e)
	defer os.Remove(f.Name())
	defer f.Close()
	_, e = io.Copy(f, s)
	must(e)
	must(f.Sync())
	must(f.Close())
	must(os.Rename(f.Name(), dest))
}
func sameIndex(a, b *index) bool {
	x, e := a.db.Begin(false)
	must(e)
	defer x.Rollback()
	y, e := b.db.Begin(false)
	must(e)
	defer y.Rollback()
	ac, bc := rows(x).Cursor(), rows(y).Cursor()
	ak, av := ac.First()
	bk, bv := bc.First()
	for ak != nil && bk != nil {
		if !bytes.Equal(ak, bk) || !bytes.Equal(av, bv) {
			return false
		}
		ak, av = ac.Next()
		bk, bv = bc.Next()
	}
	return ak == nil && bk == nil
}

// Every active device retains its last acknowledged common ancestor until its local commit succeeds.
func run(r Request) Result { return runWith(r, connect) }
func runWith(r Request, openDrive func(string) *drive) Result {
	check(filepath.IsAbs(r.Dir), "Missing workspace directory")
	must(os.MkdirAll(r.Dir, 0700))
	lock, e := bolt.Open(filepath.Join(r.Dir, "session.lock"), 0600, &bolt.Options{Timeout: time.Second})
	must(e)
	defer lock.Close()
	stateFile := filepath.Join(r.Dir, "checkpoint.json")
	pendingFile := filepath.Join(r.Dir, "prepared.json")
	fileFor := func(transaction string) string {
		id, e := hex.DecodeString(transaction)
		check(e == nil && len(id) == 16, "Invalid sync transaction")
		return filepath.Join(r.Dir, "candidate-"+transaction+".zip")
	}
	var s checkpoint
	bound := load(stateFile, &s)
	var p prepared
	cached := func(hash string) string {
		check(validHash(hash), "Invalid revision")
		return filepath.Join(r.Dir, "revision-"+hash+".zip")
	}
	if r.Action == "ack" {
		check(load(pendingFile, &p) && r.Transaction == p.Transaction && validHash(p.Target), "Expired sync transaction")
		candidate := fileFor(p.Transaction)
		dest := cached(p.Target)
		copyFile(candidate, dest)
		previous := s.Base
		s.Base = p.Target
		s.Stamp = r.Stamp
		must(atomicFile(stateFile, encode(s)))
		os.Remove(pendingFile)
		os.Remove(candidate)
		if previous != "" && previous != s.Base {
			os.Remove(cached(previous))
		}
		return Result{Status: "synced"}
	}
	check(r.Token != "", "Sign in required")
	g := openDrive(r.Token)
	check(!bound || s.Folder == g.folder, "هذا الجهاز مرتبط بحساب OneDrive آخر. استخدم الحساب المرتبط.")
	h := g.marker(!bound)
	check(!bound || s.Marker == h.ID, "تغير مجلد المزامنة. تحقق من حساب OneDrive.")
	check(s.Base == "" || h.Hash != "", "Missing cloud history")
	if !bound {
		s.Folder = g.folder
		s.Marker = h.ID
		must(atomicFile(stateFile, encode(s)))
	}
	if r.Action == "publish" {
		check(load(pendingFile, &p) && r.Transaction == p.Transaction && r.Stamp == p.Stamp, "Expired sync transaction")
		candidate := fileFor(p.Transaction)
		if p.Target == "" || p.Target != h.Hash {
			check(h.ETag == p.ETag && h.Hash == p.Seen, errRace.Error())
			if p.Publish {
				p.Target = g.upload(candidate, p.Seen)
				must(atomicFile(pendingFile, encode(p)))
				g.claim(h, p.Target)
			} else {
				p.Target = h.Hash
				must(atomicFile(pendingFile, encode(p)))
			}
		}
		return Result{Status: "committed", File: candidate, Transaction: p.Transaction, Apply: p.Apply}
	}
	check(r.Action == "prepare", "Unknown action")
	if load(pendingFile, &p) && p.Target != "" && p.Target == h.Hash && r.Stamp == p.Stamp {
		if _, e := os.Stat(fileFor(p.Transaction)); e == nil {
			return Result{Status: "ready", Transaction: p.Transaction, File: fileFor(p.Transaction), Apply: p.Apply}
		}
	}
	// A local checkpoint and the current cloud revision suffice for the next three-way merge.
	for _, pattern := range []string{"revision-*.zip", "working-*", "copy-*", "candidate-*.zip"} {
		matches, _ := filepath.Glob(filepath.Join(r.Dir, pattern))
		for _, file := range matches {
			if s.Base != "" && file == cached(s.Base) || h.Hash != "" && file == cached(h.Hash) || p.Transaction != "" && file == fileFor(p.Transaction) {
				continue
			}
			_ = os.RemoveAll(file)
		}
	}
	if s.Base != "" && h.Hash == s.Base && s.Stamp == r.Stamp {
		return Result{Status: "synced"}
	}
	if r.Local == "" && !r.Empty && !(s.Base != "" && s.Stamp == r.Stamp) {
		return Result{Status: "export"}
	}
	if r.Empty && h.Hash == "" {
		return Result{Status: "waiting"}
	}
	scratch, e := os.MkdirTemp(r.Dir, "working-")
	must(e)
	defer os.RemoveAll(scratch)
	remote := ""
	if h.Hash != "" {
		remote = cached(h.Hash)
		if _, e := os.Stat(remote); os.IsNotExist(e) {
			tmp := filepath.Join(scratch, "download.zip")
			g.download(h.Hash, tmp)
			must(os.Rename(tmp, remote))
		} else {
			must(e)
		}
	}
	id := make([]byte, 16)
	_, e = rand.Read(id)
	must(e)
	p = prepared{Transaction: hex.EncodeToString(id), ETag: h.ETag, Seen: h.Hash, Stamp: r.Stamp}
	candidate := fileFor(p.Transaction)
	switch {
	case r.Empty || (s.Base != "" && s.Stamp == r.Stamp):
		copyFile(remote, candidate)
		p.Apply = true
	case h.Hash == s.Base:
		copyFile(r.Local, candidate)
		p.Publish = true
	default:
		base := ""
		if s.Base != "" {
			base = cached(s.Base)
		}
		idx := []*index{}
		for n, src := range []string{base, r.Local, remote, ""} {
			db, e := openIndex(filepath.Join(scratch, fmt.Sprintf("%d.db", n)))
			must(e)
			defer db.close()
			archiveIndex(db, src)
			idx = append(idx, db)
		}
		conflicts, changed, e := merge(idx[0], idx[1], idx[2], idx[3], r.Choices)
		must(e)
		if len(conflicts) > 0 {
			return Result{Status: "conflict", Conflicts: conflicts}
		}
		p.Apply = changed
		p.Publish = !sameIndex(idx[3], idx[2])
		if !p.Publish {
			copyFile(remote, candidate)
		} else if !p.Apply {
			copyFile(r.Local, candidate)
		} else {
			tmp := filepath.Join(scratch, "merged.zip")
			render(idx[3], tmp)
			must(os.Rename(tmp, candidate))
		}
	}
	must(atomicFile(pendingFile, encode(p)))
	return Result{Status: "ready", Transaction: p.Transaction, File: candidate, Apply: p.Apply}
}
func Call(raw []byte) (reply []byte) {
	defer func() {
		if e := recover(); e != nil {
			reply = encode(Result{Status: "error", Error: fmt.Sprint(e)})
		}
	}()
	var r Request
	must(decode(raw, &r))
	return encode(run(r))
}

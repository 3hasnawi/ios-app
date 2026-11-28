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
	Reason                                        string
	Revision, Head, Cursor                        string
	Until                                         int64
	Empty, ResumeDeferred                         bool
	Choices                                       map[string]decision
}
type Result struct {
	Head        string          `json:"head,omitempty"`
	Cursor      string          `json:"cursor,omitempty"`
	Points      []historyPoint  `json:"points,omitempty"`
	Preview     *historyPreview `json:"preview,omitempty"`
	Status      string          `json:"status"`
	Error       string          `json:"error,omitempty"`
	Conflicts   []conflict      `json:"conflicts,omitempty"`
	File        string          `json:"file,omitempty"`
	Transaction string          `json:"transaction,omitempty"`
	Apply       bool            `json:"apply,omitempty"`
}
type checkpoint struct{ Folder, Marker, Base, Stamp, Generation string }
type prepared struct {
	Transaction, ETag, Seen, Target, Stamp, Generation, Restore string
	ChoicesDigest                                               string
	Apply, Publish                                              bool
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

func runWith(r Request, openDrive func(string) *drive) Result {
	check(filepath.IsAbs(r.Dir), "Missing workspace directory")
	must(os.MkdirAll(r.Dir, 0700))
	if r.Action == "note" {
		return rememberPoint(r)
	}
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
		s.Generation = p.Generation
		s.Base = p.Target
		s.Stamp = r.Stamp
		must(atomicFile(stateFile, encode(s)))
		os.Remove(pendingFile)
		os.Remove(candidate)
		_ = os.RemoveAll(filepath.Join(r.Dir, "incoming-blocks"))
		if previous != "" && previous != s.Base {
			os.Remove(cached(previous))
		}
		return Result{Status: "synced"}
	}
	check(r.Token != "", "Sign in required")
	retryFile := filepath.Join(r.Dir, "retry-after.json")
	var until time.Time
	if load(retryFile, &until) && until.After(time.Now()) {
		panic(retryAfter{until})
	}
	defer func() {
		if failure := recover(); failure != nil {
			if pause, ok := failure.(retryAfter); ok {
				must(atomicFile(retryFile, encode(pause.until)))
			}
			panic(failure)
		}
	}()
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
	g.cacheDir = filepath.Join(r.Dir, "incoming-blocks")
	if s.Base != "" {
		g.sources = append(g.sources, cached(s.Base))
	}
	if r.Local != "" {
		g.sources = append(g.sources, r.Local)
	}
	g.flushNotes(r.Dir)
	if r.Action == "history" {
		return g.history(h, r)
	}
	if r.Action == "preview" {
		return g.preview(h, r)
	}
	if r.Action == "restore" {
		check(s.Base == h.Hash && s.Base != "" && s.Stamp == r.Stamp, "زامن بيانات هذا الجهاز أولاً، ثم حدّث سجل المزامنة.")
		check(h.Hash == r.Head && r.Revision != h.Hash, "تغيّر سجل المزامنة أو تم اختيار النقطة الحالية. حدّث السجل.")
		point := g.findRevision(h.Hash, r.Revision)
		id := make([]byte, 16)
		_, e := rand.Read(id)
		must(e)
		transaction := hex.EncodeToString(id)
		candidate := fileFor(transaction)
		g.download(point.ID, candidate)
		p = prepared{Transaction: transaction, ETag: h.ETag, Seen: h.Hash, Stamp: r.Stamp, Generation: sum(id), Restore: point.ID, Apply: true, Publish: true}
		must(atomicFile(pendingFile, encode(p)))
		return Result{Status: "ready", Transaction: transaction, File: candidate, Apply: true}
	}
	if r.Action == "publish" {
		check(load(pendingFile, &p) && r.Transaction == p.Transaction && r.Stamp == p.Stamp, "Expired sync transaction")
		candidate := fileFor(p.Transaction)
		if p.Target == "" || p.Target != h.Hash {
			check(h.ETag == p.ETag && h.Hash == p.Seen, errRace.Error())
			if p.Publish {
				if p.Restore != "" {
					previous := g.indexFor(p.Seen)
					previous.Previous = p.Seen
					previous.Kind = "beforeRestore"
					previous.At = time.Now().UnixMilli()
					safety := g.put(indexPrefix, encode(previous))
					selected := g.indexFor(p.Restore)
					selected.Previous = safety
					selected.Kind = "restore"
					selected.At = time.Now().UnixMilli()
					p.Target = g.put(indexPrefix, encode(selected))
				} else {
					p.Target = g.upload(candidate, p.Seen)
				}
				must(atomicFile(pendingFile, encode(p)))
				g.claim(h, p.Target, p.Generation)
			} else {
				p.Target = h.Hash
				must(atomicFile(pendingFile, encode(p)))
			}
		}
		return Result{Status: "committed", File: candidate, Transaction: p.Transaction, Apply: p.Apply}
	}
	check(r.Action == "prepare", "Unknown action")
	choicesDigest := sum(encode(r.Choices))
	if load(pendingFile, &p) && r.Stamp == p.Stamp {
		committed := p.Target != "" && p.Target == h.Hash
		// عند تأجيل تطبيق نسخة أثناء التصفح نعيد استخدامها ما دامت البيانات والاختيارات مطابقة.
		deferred := r.ResumeDeferred && p.Apply && p.Target == "" && p.Restore == "" && p.Seen == h.Hash &&
			p.ETag == h.ETag && p.Generation == h.Generation && p.ChoicesDigest == choicesDigest
		if committed || deferred {
			if info, e := os.Stat(fileFor(p.Transaction)); e == nil && info.Mode().IsRegular() && info.Size() > 0 {
				return Result{Status: "ready", Transaction: p.Transaction, File: fileFor(p.Transaction), Apply: p.Apply}
			}
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
	p = prepared{Transaction: hex.EncodeToString(id), ETag: h.ETag, Seen: h.Hash, Stamp: r.Stamp, Generation: h.Generation, ChoicesDigest: choicesDigest}
	candidate := fileFor(p.Transaction)
	localUnchanged := s.Base != "" && s.Stamp == r.Stamp
	local := r.Local
	base := ""
	if s.Base != "" {
		base = cached(s.Base)
	}
	reconcileHistory := false
	if s.Base != "" && !r.Empty && h.Generation == s.Generation {
		if localUnchanged {
			local = base
			if _, e := os.Stat(base); os.IsNotExist(e) {
				if r.Local == "" {
					return Result{Status: "export"}
				}
				base, local = r.Local, r.Local
			} else {
				must(e)
			}
			reconcileHistory = needsContractMerge(local, remote)
		} else if h.Hash == s.Base {
			reconcileHistory = needsContractMerge(remote, local)
		}
	}
	switch {
	case r.Empty || (localUnchanged && !reconcileHistory):
		copyFile(remote, candidate)
		p.Apply = true
	case h.Hash == s.Base && !reconcileHistory:
		copyWorkspaceExport(local, candidate)
		p.Publish = true
	default:
		idx := []*index{}
		for n, src := range []string{base, local, remote, ""} {
			db, e := openIndex(filepath.Join(scratch, fmt.Sprintf("%d.db", n)))
			must(e)
			defer db.close()
			archiveIndex(db, src)
			idx = append(idx, db)
		}
		conflicts, _, e := merge(idx[0], idx[1], idx[2], idx[3], r.Choices, s.Generation != h.Generation)
		must(e)
		if len(conflicts) > 0 {
			return Result{Status: "conflict", Conflicts: conflicts}
		}
		keepReferencedRecords(idx[3], idx[1], idx[2], idx[0])
		p.Apply = !sameIndex(idx[3], idx[1])
		p.Publish = !sameIndex(idx[3], idx[2])
		if !p.Publish {
			copyFile(remote, candidate)
		} else if !p.Apply {
			copyWorkspaceExport(local, candidate)
		} else {
			tmp := filepath.Join(scratch, "merged.zip")
			render(idx[3], tmp)
			must(os.Rename(tmp, candidate))
		}
	}
	must(atomicFile(pendingFile, encode(p)))
	return Result{Status: "ready", Transaction: p.Transaction, File: candidate, Apply: p.Apply}
}
func Call(raw []byte) []byte { return CallWithTransfer(raw, nil) }
func CallWithTransfer(raw []byte, onTransfer func(bool)) []byte {
	return CallWithProgress(raw, onTransfer, nil)
}
func CallWithProgress(raw []byte, onTransfer func(bool), onProgress func(int, int64, int64)) (reply []byte) {
	defer func() {
		if e := recover(); e != nil {
			reply = encode(Result{Status: "error", Error: fmt.Sprint(e)})
		}
	}()
	var r Request
	must(decode(raw, &r))
	return encode(runWith(r, func(token string) *drive {
		g := connect(token)
		g.onProgress = onProgress
		g.onTransfer = onTransfer
		return g
	}))
}

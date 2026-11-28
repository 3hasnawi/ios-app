package workspace

import (
	"net/url"
	"strings"
)

const retainedBackups = 2

func (g *drive) requireRetainedBackup(h head, revision string) {
	check(validHash(revision) && h.Hash != "", "اختر نسخة محفوظة.")
	if revision != h.Hash {
		check(g.indexFor(h.Hash).Previous == revision, "النسخة المختارة لم تعد ضمن آخر نسختين. حدّث السجل.")
	}
}

func (g *drive) removeBackupObject(path string, headers map[string]string) {
	code, _ := g.request("DELETE", path, nil, headers, 1<<20)
	if code != 404 {
		ok(code)
	}
}

func (g *drive) removeWholeArchive(idx fileIndex) {
	path := "items/" + url.PathEscape(idx.FileID)
	name := idx.FileName
	if name == "" {
		code, b := g.request("GET", path+"?$select=name", nil, nil, 1<<20)
		if code == 404 {
			return
		}
		ok(code)
		var file item
		must(decode(b, &file))
		name = file.Name
	}
	check(strings.HasPrefix(name, "payroll-backup-") && strings.HasSuffix(name, ".zip") && !strings.ContainsAny(name, "/\\?%#:"), "اسم ملف النسخة القديمة غير مطابق.")
	// لا نحذف ملفاً نقله المستخدم أو استبدله بملف آخر له الاسم نفسه.
	code, b := g.request("GET", g.root+url.PathEscape(name)+"?$select=id,eTag", nil, nil, 1<<20)
	if code == 404 {
		return
	}
	ok(code)
	var file item
	must(decode(b, &file))
	if file.ID != idx.FileID {
		return
	}
	check(file.ETag != "", "تعذّر التحقق من إصدار الملف القديم.")
	g.removeBackupObject(path, map[string]string{"If-Match": file.ETag})
}

// نحذف أقدم الفهارس أولاً حتى تبقى سلسلة الوصول قابلة للاستئناف إذا انقطع التنظيف.
func (g *drive) pruneBackups(current string) (warning string) {
	defer func() {
		if recover() != nil {
			warning = "اكتمل الرفع، وتعذّر إكمال حذف النسخ الأقدم. ستُعاد محاولة التنظيف عند الرفع التالي."
		}
	}()
	protected := map[string]bool{}
	cursor := current
	for n := 0; n < retainedBackups && cursor != ""; n++ {
		idx := g.indexFor(cursor)
		protected[idx.FileID] = true
		for _, id := range idx.Blocks {
			protected[id] = true
		}
		cursor = idx.Previous
	}
	var expired []string
	for cursor != "" {
		idx, found := g.readIndex(cursor)
		if !found {
			break
		}
		expired = append(expired, cursor)
		cursor = idx.Previous
	}
	for i := len(expired) - 1; i >= 0; i-- {
		id := expired[i]
		idx, found := g.readIndex(id)
		if !found {
			continue
		}
		if idx.Format == 2 && !protected[idx.FileID] {
			g.removeWholeArchive(idx)
		}
		for _, block := range idx.Blocks {
			check(validHash(block), "فهرس النسخة القديمة غير صالح.")
			if !protected[block] {
				g.removeBackupObject(g.root+chunkPrefix+block, nil)
			}
		}
		g.removeBackupObject(g.root+indexPrefix+id, nil)
	}
	return ""
}

package workspace

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"io"
	"os"
	"sort"
)

const appearanceKey = "appAppearance"

// نسخة ويندوز المحمولة قد تتضمن الثيم. ننسخ الأعضاء المضغوطة كما هي حتى لا يتغير محتوى ملفات الرواتب.
func copyWorkspaceExport(src, dest string) {
	z, err := zip.OpenReader(src)
	must(err)
	defer z.Close()
	var settings map[string][]byte
	must(decode(readEntry(&z.Reader, "settings.json", 64_000_000), &settings))
	if _, present := settings[appearanceKey]; !present {
		copyFile(src, dest)
		return
	}
	delete(settings, appearanceKey)
	changed := map[string][]byte{"settings.json": encode(settings)}
	var manifest Object
	must(decode(readEntry(&z.Reader, "manifest.json", 1_000_000), &manifest))
	if number(manifest["version"]) >= 12 {
		// نتحقق من البصمة الأصلية قبل تحديث بصمة الإعدادات حتى لا نخفي تلف ملف آخر.
		var members []*zip.File
		for _, f := range z.File {
			if f.Name != "employees.jsonl" && f.Name != "manifest.json" {
				members = append(members, f)
			}
		}
		sort.Slice(members, func(i, j int) bool { return members[i].Name < members[j].Name })
		before, after := sha256.New(), sha256.New()
		for _, f := range members {
			r, err := f.Open()
			must(err)
			digest := sha256.New()
			_, err = io.Copy(digest, r)
			r.Close()
			must(err)
			prefix := []byte(f.Name + "\x00")
			before.Write(prefix)
			after.Write(prefix)
			original := digest.Sum(nil)
			before.Write(original)
			if f.Name == "settings.json" {
				replacement := sha256.Sum256(changed[f.Name])
				after.Write(replacement[:])
			} else {
				after.Write(original)
			}
		}
		check(bytes.Equal(un64(str(manifest, "supportingDigest")), before.Sum(nil)), "فشل التحقق من سلامة أرشيف المزامنة.")
		manifest["supportingDigest"] = after.Sum(nil)
		changed["manifest.json"] = encode(manifest)
	}
	out, err := os.OpenFile(dest, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	must(err)
	defer out.Close()
	writer := zip.NewWriter(out)
	for _, f := range z.File {
		if data, replace := changed[f.Name]; replace {
			header := f.FileHeader
			w, err := writer.CreateHeader(&header)
			must(err)
			_, err = w.Write(data)
			must(err)
		} else {
			must(writer.Copy(f))
		}
	}
	must(writer.Close())
	must(out.Sync())
}

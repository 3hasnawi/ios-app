package workspace

import (
	"archive/zip"
	"bytes"
	"reflect"
	"sort"
	"strings"

	"howett.net/plist"
)

// Apple's contract ledger is a binary plist inside a plist Data value. Keep the
// sealed LZFSE payload, dates and numeric types intact on both platforms.
const contractKey = "contractTransferBatchesV1"

func plistData(v any) []byte    { data, e := plist.Marshal(v, plist.BinaryFormat); must(e); return data }
func readPlist(data []byte) any { var v any; _, e := plist.Unmarshal(data, &v); must(e); return v }

func contractRows(w *batchWriter, data []byte) {
	ledger := obj(readPlist(contractDateObjects(data, false)))
	check(ledger != nil, "Invalid contract ledger")
	groups := map[string]Object{}
	for _, entry := range array(ledger["records"]) {
		record := obj(entry)
		month := str(record, "month")
		id := strings.ToUpper(str(record, "id"))
		check(month != "" && validID(id), "Invalid contract record")
		if groups[month] == nil {
			groups[month] = Object{}
		}
		group := groups[month]
		check(group[id] == nil, "Duplicate contract record")
		// Keep the actual archived record opaque. Display metadata is derived from
		// those same bytes and is never used to reconstruct the financial payload.
		group[id] = Object{"id": id, "month": month, "amount": record["held"], "cancelled": record["cancelledAt"] != nil, "archive": plistData(record)}
	}
	// An intentionally empty ledger still round-trips.
	w.put(recKey("contractFormat", contractKey), encode(1))
	for month, records := range groups {
		w.put(recKey("contract", month), encode(records))
	}
}

// نحفظ الدفعات الملغاة بمعرّفاتها حتى عند وجود دفعة نشطة أخرى للشهر نفسه.
func mergeContracts(r *reconciliation, path []string, values [3][]byte) []byte {
	groups := [3]Object{}
	archived := Object{}
	for i, data := range values {
		groups[i] = Object{}
		if data != nil {
			must(decode(data, &groups[i]))
		}
		if i != 0 {
			for id, row := range groups[i] {
				if obj(row)["cancelled"] == true {
					archived[id] = row
				}
			}
		}
	}
	for _, id := range keys(archived) {
		left, right := obj(groups[1][id]), obj(groups[2][id])
		if left != nil && right != nil && !reflect.DeepEqual(left, right) {
			// نختار الحالة الملغاة التي تحمل كل الإثباتات الموجودة على الجهازين.
			switch {
			case left["cancelled"] == true && coversContract(left, right):
				archived[id] = left
			case right["cancelled"] == true && coversContract(right, left):
				archived[id] = right
			default:
				panic("تختلف بيانات تنفيذ دفعة العقود الملغاة لشهر " + path[1] + " بين الجهازين. حُفظت النسختان للمراجعة.")
			}
		}
		for _, group := range groups {
			delete(group, id)
		}
	}
	for i, group := range groups {
		values[i] = nil
		if len(group) > 0 {
			values[i] = encode(group)
		}
	}
	// تبقى المفاضلة بين دفعتين نشطتين مستقلة عن أي سجل ملغى.
	picked := r.pick(path, values[0], values[1], values[2], false)
	if len(archived) == 0 {
		return picked
	}
	if picked != nil {
		var active Object
		must(decode(picked, &active))
		for id, row := range active {
			archived[id] = row
		}
	}
	return encode(archived)
}

func coversContract(newer, older Object) bool {
	record := func(row Object) Object { return obj(readPlist(un64(str(row, "archive")))) }
	a, b := record(newer), record(older)
	for _, field := range []string{"prepared", "confirmed"} {
		for key, value := range obj(b[field]) {
			if !reflect.DeepEqual(obj(a[field])[key], value) {
				return false
			}
		}
		delete(a, field)
		delete(b, field)
	}
	if b["cancelledAt"] != nil && !reflect.DeepEqual(b["cancelledAt"], a["cancelledAt"]) {
		return false
	}
	for _, field := range []string{"cancelledAt", "reviewedImport"} {
		delete(a, field)
		delete(b, field)
	}
	return reflect.DeepEqual(a, b)
}

// نراجع سجل الإلغاء أيضاً في مساري الرفع والاستلام السريعين عند اختلاف سجل العقود.
func needsContractMerge(base, candidate string) bool {
	read := func(path string) []byte {
		z, e := zip.OpenReader(path)
		must(e)
		defer z.Close()
		var settings map[string][]byte
		must(decode(readEntry(&z.Reader, "settings.json", 64_000_000), &settings))
		return settings[contractKey]
	}
	before := read(base)
	if len(before) == 0 {
		return false
	}
	after := read(candidate)
	if bytes.Equal(before, after) {
		return false
	}
	ledger := obj(readPlist(contractDateObjects(readPlist(before).([]byte), false)))
	for _, value := range array(ledger["records"]) {
		if obj(value)["cancelledAt"] != nil {
			return true
		}
	}
	return false
}
func applyContracts(all map[string]Object) ([]byte, []any) {
	list := []any{}
	version := 1
	months := []string{}
	for k := range all {
		months = append(months, k)
	}
	sort.Strings(months)
	for _, month := range months {
		active := 0
		records := all[month]
		for _, id := range keys(records) {
			rec := obj(readPlist(un64(str(obj(records[id]), "archive"))))
			check(strings.EqualFold(str(rec, "id"), id) && str(rec, "month") == month, "Invalid archived contract")
			if rec["cancelledAt"] != nil {
				version = 2
			} else {
				active++
			}
			list = append(list, rec)
		}
		check(active <= 1, "Conflicting active contracts")
	}
	return plistData(contractDateObjects(plistData(Object{"version": version, "records": list}), true)), list
}

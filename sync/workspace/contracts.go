package workspace

import (
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
	ledger := obj(readPlist(data))
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
func activeContract(data []byte) string {
	if data == nil {
		return ""
	}
	var records Object
	must(decode(data, &records))
	for _, id := range keys(records) {
		if obj(records[id])["cancelled"] == false {
			return id
		}
	}
	return ""
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
	return plistData(plistData(Object{"version": version, "records": list})), list
}

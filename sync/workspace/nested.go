package workspace

import (
	"bytes"
	"sort"
	"strings"
)

// A missing record is distinct from an explicit JSON null. Deletion against an
// edit is resolved at the parent, so a deleted ledger cannot leave orphan rows.
type reconciliation struct {
	choices   map[string]decision
	conflicts []conflict
}

func (r *reconciliation) pick(path []string, b, l, c []byte, structured bool) []byte {
	if len(path) >= 4 && path[0] == "refund" && path[len(path)-2] == "rows" && path[len(path)-1] != "ترتيب القيود" {
		// Core Data regenerates a candidate's handle on import. Compare the
		// portable person and amount, but retain a full, decodable native row.
		portable := func(raw []byte) []byte {
			if raw == nil {
				return nil
			}
			var row Object
			must(decode(raw, &row))
			if candidate := obj(row["candidate"]); candidate != nil {
				delete(candidate, "id")
			}
			return encode(row)
		}
		left, right, ancestor := portable(l), portable(c), portable(b)
		switch {
		case bytes.Equal(left, right) || bytes.Equal(ancestor, right):
			return l
		case bytes.Equal(ancestor, left):
			return c
		}
	}
	switch {
	case bytes.Equal(l, c) || bytes.Equal(b, c):
		return l
	case bytes.Equal(b, l):
		return c
	}
	if len(path) == 3 && path[0] == "contract" {
		structured = false
	}
	if structured && l != nil && c != nil {
		var v [3]any
		for i, data := range [][]byte{b, l, c} {
			if data != nil {
				must(decode(data, &v[i]))
			}
		}
		if obj(v[1]) != nil && obj(v[2]) != nil && (b == nil || obj(v[0]) != nil) {
			maps := [3]Object{obj(v[0]), obj(v[1]), obj(v[2])}
			all := Object{}
			for _, m := range maps {
				for k := range m {
					all[k] = nil
				}
			}
			result := Object{}
			for _, k := range keys(all) {
				var data [3][]byte
				for i, m := range maps {
					if value, ok := m[k]; ok {
						data[i] = encode(value)
					}
				}
				x := r.pick(append(path, k), data[0], data[1], data[2], true)
				if x != nil {
					var value any
					must(decode(x, &value))
					result[k] = value
				}
			}
			return encode(result)
		}
		if _, ok := v[1].([]any); ok {
			if _, ok := v[2].([]any); ok && (b == nil || array(v[0]) != nil) {
				maps := [3]map[string][]byte{}
				orders := [3][]string{}
				all := Object{}
				valid := true
				for i, list := range v {
					maps[i] = map[string][]byte{}
					for _, entry := range array(list) {
						m := obj(entry)
						id := str(m, "id")
						if candidate := obj(m["candidate"]); candidate != nil {
							person := obj(candidate["person"])
							id = str(person, "iban")
							if id == "" {
								id = str(person, "name")
							}
							id = str(candidate, "month") + " · " + id
						}
						if id == "" || maps[i][id] != nil {
							valid = false
							break
						}
						all[id] = nil
						maps[i][id] = encode(entry)
						orders[i] = append(orders[i], id)
					}
				}
				if valid {
					live := map[string][]byte{}
					for _, id := range keys(all) {
						// Containers such as a reconciliation period own several independent movements.
						nested := false
						for _, source := range maps {
							if data := source[id]; data != nil {
								var entity Object
								must(decode(data, &entity))
								for _, value := range entity {
									if _, ok := value.([]any); ok {
										nested = true
									}
								}
							}
						}
						if x := r.pick(append(path, id), maps[0][id], maps[1][id], maps[2][id], nested); x != nil {
							live[id] = x
						}
					}
					// Preserve existing order, including unilateral reordering. Simultaneous
					// insertions append in stable ID order so peers produce the same sequence.
					var common [3][]string
					for i, ids := range orders {
						for _, id := range ids {
							if live[id] != nil && maps[0][id] != nil && maps[1][id] != nil && maps[2][id] != nil {
								common[i] = append(common[i], id)
							}
						}
					}
					selected := r.pick(append(path, "ترتيب القيود"), encode(common[0]), encode(common[1]), encode(common[2]), false)
					var ordered []string
					must(decode(selected, &ordered))
					seen := map[string]bool{}
					// Keep insertion positions when just one peer has added items.
					if bytes.Equal(encode(orders[0]), encode(orders[1])) {
						ordered = orders[2]
					} else if bytes.Equal(encode(orders[0]), encode(orders[2])) {
						ordered = orders[1]
					}
					result := []any{}
					appendEntry := func(id string) {
						if !seen[id] && live[id] != nil {
							seen[id] = true
							var x any
							must(decode(live[id], &x))
							result = append(result, x)
						}
					}
					for _, id := range ordered {
						appendEntry(id)
					}
					for _, id := range keys(all) {
						appendEntry(id)
					}
					return encode(result)
				}
			}
		}
	}
	k := recKey(path...)
	ticket := sum(encode([]string{k, sum(b), sum(l), sum(c)}))
	choice := r.choices[k]
	if choice.Ticket == ticket {
		if choice.Side == "local" {
			return l
		}
		if choice.Side == "remote" {
			return c
		}
	}
	r.conflicts = append(r.conflicts, conflict{k, ticket, describe(l), describe(c)})
	return l
}

func groupedDescription(b []byte) string {
	var group [][]byte
	if decode(b, &group) != nil || len(group) == 0 {
		return ""
	}
	var names []string
	for _, v := range group {
		var m Object
		if decode(v, &m) != nil || m == nil {
			return ""
		}
		names = append(names, describe(v))
		if len(names) == 3 {
			break
		}
	}
	sort.Strings(names)
	return strings.Join(names, " / ")
}

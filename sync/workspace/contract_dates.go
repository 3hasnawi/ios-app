package workspace

import (
	"bytes"
	"encoding/binary"
)

// يحوّل مفكك plist التاريخ إلى توقيت Unix وقد يغيّر آخر بت من كسر الثانية.
// نستعمل نوع UID داخل الفهرس المؤقت فقط لحمل البتات الثمانية الأصلية.
func contractDateObjects(data []byte, toApple bool) []byte {
	const invalid = "تعذّرت قراءة تواريخ سجل دفعات العقود."
	check(len(data) >= 40 && string(data[:8]) == "bplist00", invalid)
	trailer := bytes.Clone(data[len(data)-32:])
	width := int(trailer[6])
	count := binary.BigEndian.Uint64(trailer[8:16])
	table := binary.BigEndian.Uint64(trailer[24:32])
	check(table >= 8 && table <= uint64(len(data)-32) && width >= 1 && width <= 8 && count > 0 &&
		count <= (uint64(len(data)-32)-table)/uint64(width), invalid)
	result := bytes.Clone(data[:table])
	offsets := bytes.Clone(data[table : table+count*uint64(width)])
	var expanded map[int]uint64
	read := func(raw []byte) (value uint64) {
		for _, b := range raw {
			value = (value << 8) | uint64(b)
		}
		return
	}
	for i := 0; i < int(count); i++ {
		offset := read(offsets[i*width : (i+1)*width])
		check(offset >= 8 && offset < table, invalid)
		tag := data[offset]
		if !toApple {
			check(tag>>4 != 8, invalid)
			if tag == 0x33 {
				check(offset+9 <= table, invalid)
				result[offset] = 0x87
			}
			continue
		}
		if tag>>4 != 8 {
			continue
		}
		n := uint64(tag&15) + 1
		check(n <= 8 && offset+1+n <= table, invalid)
		if n == 8 {
			result[offset] = 0x33
		} else {
			// يدعم أيضاً التاريخ عند بداية حقبة Apple، الذي يختصره مُرمّز UID إلى بايت واحد.
			if expanded == nil {
				expanded = map[int]uint64{}
			}
			expanded[i] = uint64(len(result))
			result = append(result, 0x33)
			result = binary.BigEndian.AppendUint64(result, read(data[offset+1:offset+1+n]))
		}
	}
	binary.BigEndian.PutUint64(trailer[24:32], uint64(len(result)))
	if len(expanded) == 0 {
		result = append(result, offsets...)
	} else {
		trailer[6] = 8
		for i := 0; i < int(count); i++ {
			address, ok := expanded[i]
			if !ok {
				address = read(offsets[i*width : (i+1)*width])
			}
			result = binary.BigEndian.AppendUint64(result, address)
		}
	}
	return append(result, trailer...)
}

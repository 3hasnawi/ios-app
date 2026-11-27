"""Compare the two production renderers with the same fictional payroll rows."""
import re
import statistics
import sys

import pymupdf

values = [473000 + i * 10000 for i in range(12)]
expected = [f"{n:,}" for n in [*values, sum(values)]]
dimensions = []
pitches = []
for path in sys.argv[1:]:
    with pymupdf.open(path) as pdf:
        assert len(pdf) == 1, path
        page = pdf[0]
        dimensions.append((page.rect.width, page.rect.height))
        words = page.get_text("words")
        amounts = [(w[4], w[1]) for w in words if re.fullmatch(r"[0-9]{1,3}(?:,[0-9]{3})+", w[4])]
        assert [v for v, y in sorted(amounts, key=lambda p: p[1])] == expected, (path, amounts)
        positions = sorted(y for value, y in amounts if value != expected[-1])
        pitches.append(statistics.median(b-a for a,b in zip(positions,positions[1:])))
        borders = [d for d in page.get_drawings() if d["type"] in ("s","fs") and
                   10 < d["rect"].width < 400 and 15 < d["rect"].height < 40]
        assert len(borders) >= 12 and all(abs(d["width"]-1) < .02 for d in borders), (path,len(borders))
assert all(abs(a-b) < 1 for a,b in zip(*dimensions)), dimensions
assert max(abs(p-24) for p in pitches) <= 1, pitches
print("PASS all twelve amounts, sum, A4 page, row pitch and one-point table borders")

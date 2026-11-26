"""Compare extracted amounts, paper, font and printed row pitch across the two PDF engines."""
from pathlib import Path
import re
import statistics
import sys
import pymupdf

windows, apple = map(Path,sys.argv[1:])
values = [f"{125000 + i*2500:,}" for i in range(12)]
metrics = []
for source in [next(windows.rglob("entity-reference.pdf")), apple/"deduction-preview.pdf"]:
    with pymupdf.open(source) as pdf:
        assert len(pdf)==1 and pdf[0].rect.width < pdf[0].rect.height
        page=pdf[0]
        spans=[s for b in page.get_text("dict")["blocks"] if "lines" in b for l in b["lines"] for s in l["spans"]]
        # CoreText can emit one span per amount, while Edge can combine a full row.
        amounts = {m.group():s for s in spans for m in re.finditer(r"[0-9]+,[0-9]{3}",s["text"]) if m.group() in values}
        assert len(amounts)==12, (source,len(amounts),[s["text"] for s in spans if "," in s["text"]])
        lines = sorted(s["origin"][1] for s in amounts.values())
        pitch = statistics.median(b-a for a,b in zip(lines,lines[1:]))
        assert 19 <= pitch <= 21, (source,pitch)
        assert all("Arial" in s["font"] and "Bold" in s["font"] and 11.2 <= s["size"] <= 11.8 for s in amounts.values()), source
        metrics.append(tuple(page.rect))
        print("PASS agency print reference",source.name,"12 equal amounts; Arial Bold 11.5; row pitch",pitch)
assert max(abs(a-b) for a,b in zip(*metrics)) < 2

"""Check actual pages exported from the installed copy using a separate PDF engine."""
from pathlib import Path
import re
import sys

import pymupdf

screens, native = map(Path, sys.argv[1:])
for path in sorted([*screens.glob("*.pdf"), *native.glob("*.pdf")]):
    with pymupdf.open(path) as document:
        assert len(document) > 0, path
        for page in document:
            width, height = sorted((page.rect.width, page.rect.height))
            assert abs(width - 595.3) < 2 and abs(height - 841.9) < 2, (path, page.rect)
            words = page.get_text("words")
            assert len(words) > 5, (path, page.number)
            for x0, y0, x1, y1, *_ in words:
                assert x0 >= -1 and y0 >= -1 and x1 <= page.rect.width + 1 and y1 <= page.rect.height + 1, (path,page.number,x0,y0,x1,y1)
        if path.stem == "agency-long":
            assert len(document) >= 4
            assert "16,290" in document[-1].get_text(), "Missing final amount"
            # Header labels, not just the page number, must be repeated on each page.
            tops = [{w[4] for w in p.get_text("words") if w[1] < 140} for p in document]
            assert len(set.intersection(*tops)) > 4
        if path.stem == "employee-years":
            assert len(document) == 2 and all(p.rect.width > p.rect.height for p in document)
            assert all(str(year) in document[0].get_text() for year in range(2022,2026))
            assert "2026" in document[-1].get_text() and "61,770" in document[-1].get_text()
        if path.stem == "bank-long":
            assert len(document) >= 3
            for page in document:
                assert len([w for w in page.get_text("words") if w[1] > 817]) >= 4
            chars = [c for block in document[-1].get_text("rawdict")["blocks"]
                     for line in block.get("lines", []) for span in line["spans"] for c in span["chars"]
                     if c["c"] in "٠١٢٣٤٥٦٧٨٩/" and 780 < c["origin"][1] < 817]
            # ترتيب المحارف الفعلي من يسار الورقة إلى يمينها، لكل توقيع على حدة.
            for right_column, month in [(True, "١١"), (False, "١٢")]:
                signature = [c for c in chars if (c["origin"][0] > document[-1].rect.width / 2) == right_column]
                visual = "".join(c["c"] for c in sorted(signature, key=lambda c: c["origin"][0]))
                assert visual == "٢٠٢٦/" + month, (path, right_column, visual)
        if path.stem == "command":
            assert len(document) == 1 and document[0].rect.width > document[0].rect.height
        print("PASS printed", path.name, len(document), "A4 pages with selectable text")

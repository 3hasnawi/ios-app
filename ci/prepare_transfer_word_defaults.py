"""Prepare a private Word-settings file from the owner's DOCX and PDF references.

The output contains private names and must never be committed. The script prints
only identifiers and validation results, not the extracted content.
"""
import argparse
import difflib
import hashlib
import json
import re
import unicodedata
from pathlib import Path
from zipfile import ZipFile

import fitz
from lxml import etree

N = {"w": "http://schemas.openxmlformats.org/wordprocessingml/2006/main"}
BOOKS = {
    "food_company": ("شركة عراق الجوال.docx", 638.770, 638.666, 86.304, 131.669),
    "tax_social": ("خزينة الانبار حماية اجتماعية وضريبة.docx", 678.040, 677.936, 60.864, 182.501),
    "contracts": ("الضمان الاجتماعي.docx", 659.080, 658.976, 60.864, 182.501),
    "rafidain_anbar": ("مصرف الرافدين فرع الانبار.docx", 693.280, 693.176, 60.864, 182.501),
    "water": ("الموارد المائية.docx", 643.720, 643.616, 60.864, 182.501),
    "housing": ("صندوق الاسكان فرع الانبار.docx", 657.040, 656.936, 60.864, 182.501),
    "ramadi": ("الرمادي.docx", 626.650, 626.546, 60.864, 182.501),
    "rafidain_group": ("مجموعة مصارف الرافدين.docx", 661.240, 661.136, 70.464, 163.445),
    "other_banks_group": ("مجموعة مصارف الرشيد.docx", 661.240, 661.136, 70.464, 163.445),
    "cash_salary": ("امانات رواتب.docx", 629.890, 629.786, 60.864, 182.501),
    "food_tax": ("امانات ضريبة.docx", 654.760, 654.656, 60.864, 182.501),
}


def normalize(value):
    table = str.maketrans({"أ": "ا", "إ": "ا", "آ": "ا", "ٱ": "ا", "ة": "ه", "ى": "ي", "ئ": "ي", "ؤ": "و", "ھ": "ه"})
    return "".join(c for c in unicodedata.normalize("NFKC", value).translate(table) if c.isalnum())


def paragraphs(root):
    return ["".join(p.xpath("./w:r/w:t/text()", namespaces=N)).strip() for p in root.xpath(".//w:p", namespaces=N)]


def source_file(root, name):
    files = list(root.rglob(name))
    if not files or len({hashlib.sha256(p.read_bytes()).digest() for p in files}) != 1:
        raise ValueError("missing or conflicting DOCX: " + name)
    return files[0]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--pdf-pack", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    if args.output.resolve().is_relative_to(repo):
        raise ValueError("private output must be outside the repository")
    defaults = {}
    headers, numbers, contract_references = [], [], []
    with ZipFile(args.pdf_pack) as pack:
        manifest = json.loads(pack.read("manifest.json"))
        references = {item["id"]: item for item in manifest["templates"] if item["id"] in BOOKS}
        if set(references) != set(BOOKS):
            raise ValueError("PDF reference pack is incomplete")
        reference_hashes = {}
        for book_id, item in references.items():
            if item["path"] != "templates/" + book_id + ".pdf":
                raise ValueError("unexpected PDF reference path: " + book_id)
            digest = hashlib.sha256(pack.read(item["path"])).hexdigest()
            if digest != item["sha256"].lower():
                raise ValueError("PDF reference digest mismatch: " + book_id)
            reference_hashes[book_id] = digest
        for book_id, (name, y, y3, x3, w3) in BOOKS.items():
            with ZipFile(source_file(args.source_root, name)) as doc:
                header = paragraphs(etree.fromstring(doc.read("word/header1.xml")))
                header = [s for s in header if s]
                blocks = [tuple(header[i:i + 5]) for i, s in enumerate(header)
                          if "جمهورية" in s and i + 4 < len(header) and "الحسابات" in header[i + 4]]
                if not blocks or len(set(blocks)) != 1:
                    raise ValueError("ambiguous header: " + book_id)
                headers.append(blocks[0])
                number_candidates = []
                for value in header:
                    if sum(c.isdigit() for c in value) == 3 and value.count("/") == 3:
                        number_candidates += re.findall(r"[0-9٠-٩۰-۹]+\s*/\s*[0-9٠-٩۰-۹]+\s*/\s*[\u0600-\u06ff]\s*/", value)
                number_candidates = {"".join(s.split()) for s in number_candidates}
                if len(number_candidates) != 1:
                    raise ValueError("ambiguous fixed number: " + book_id)
                numbers.append(next(iter(number_candidates)))
                body = etree.fromstring(doc.read("word/document.xml"))
                body_paragraphs = [s for s in paragraphs(body) if s]
                candidates = list(set(s for s in body_paragraphs if 4 < len(normalize(s)) < 80))
                reference = fitz.open(stream=pack.read("templates/" + book_id + ".pdf"), filetype="pdf")
                page = reference[0]
                rects = [(452.260, y, 452.260 + 94.289, y + 17.668),
                         (295.610, y, 295.610 + 98.711, y + 17.668),
                         (x3, y3, x3 + w3, y3 + 17.795)]
                members = []
                for rect in rects:
                    expected = normalize(page.get_textbox(fitz.Rect(rect)))
                    ranked = sorted(((difflib.SequenceMatcher(None, expected, normalize(s)).ratio(), s)
                                     for s in candidates), reverse=True)
                    if not expected or len(ranked) < 2 or ranked[0][0] < 0.88 or ranked[0][0] - ranked[1][0] < 0.18:
                        raise ValueError("ambiguous committee field: " + book_id)
                    members.append(ranked[0][1])
                footer_parts = []
                for part in doc.namelist():
                    if re.fullmatch(r"word/footer\d+\.xml", part):
                        lines = [s for s in paragraphs(etree.fromstring(doc.read(part))) if s]
                        footer_parts.append(lines)
                footer = max(footer_parts, key=lambda lines: sum(map(len, lines)), default=[])
                if not footer:
                    raise ValueError("missing footer: " + book_id)
                defaults[book_id] = {"committeeFirst": members[0], "committeeSecond": members[1],
                                     "committeeThird": members[2], "footer": "\n".join(footer)}
                if book_id in ("food_company", "food_tax"):
                    matches = []
                    for paragraph in body_paragraphs:
                        if "نرجو تحويل" not in paragraph or "عقد تجهيز" not in paragraph:
                            continue
                        suffix = paragraph[paragraph.index("عقد تجهيز"):]
                        match = re.search(r"المرقم\s*\(([^()]*)\)", suffix)
                        if match:
                            matches.append(match.group(1).strip())
                    if len(matches) != 1:
                        raise ValueError("ambiguous food contract reference: " + book_id)
                    contract_references.append(matches[0])
            print(book_id + ": checked")
    if len(set(headers)) != 1 or len(set(numbers)) != 1 or len(contract_references) != 2 or len(set(contract_references)) != 1:
        raise ValueError("common reference fields do not match")
    package = {"version": 1, "referenceHashes": reference_hashes, "defaults": {"common": {"header": "\n".join(headers[0]),
        "fixedNumber": numbers[0], "foodContractReference": contract_references[0]}, "individual": defaults}}
    args.output.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    args.output.write_text(json.dumps(package, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    args.output.chmod(0o600)
    print("private settings file prepared")


if __name__ == "__main__":
    main()

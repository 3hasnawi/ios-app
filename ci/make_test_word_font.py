"""Generate a tiny synthetic font for simulator-only Word embedding tests.

The glyphs are boxes and do not contain the user's private fonts or data.
"""
import sys
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

chars = set("ابتثجحخدذرزسشصضطظعغفقكلمنهويأإآءؤئةى0123456789٠١٢٣٤٥٦٧٨٩:/\u00a0ترويسة وهمية")
names = {ord(char): f"u{ord(char):04X}" for char in chars}
glyph_order = [".notdef"] + sorted(set(names.values()))
glyphs = {}
for name in glyph_order:
    pen = TTGlyphPen(None)
    if name != ".notdef" and name not in ("u0020", "u00A0"):
        pen.moveTo((80, 0))
        pen.lineTo((520, 0))
        pen.lineTo((520, 700))
        pen.lineTo((80, 700))
        pen.closePath()
    glyphs[name] = pen.glyph()

font = FontBuilder(1000, isTTF=True)
font.setupGlyphOrder(glyph_order)
font.setupCharacterMap(names)
font.setupGlyf(glyphs)
font.setupHorizontalMetrics({name: (200 if name in ("u0020", "u00A0") else 600, 0) for name in glyph_order})
font.setupHorizontalHeader(ascent=800, descent=-200)
font.setupOS2(sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200,
              usWeightClass=700, fsType=0x0008)
font.setupNameTable({"familyName": "Sakkal Majalla", "styleName": "Bold",
                     "uniqueFontIdentifier": "Synthetic Word Test Font",
                     "fullName": "Sakkal Majalla Bold", "psName": "SakkalMajalla-Bold"})
font.setupPost()
font.setupMaxp()
font.save(sys.argv[1])

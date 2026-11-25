
import PDFKit
let directory = URL(fileURLWithPath: CommandLine.arguments[1])
let file = directory.appendingPathComponent("command-deductions.pdf")
var box = CommandDeductionPDF.page
let cg = CGContext(file as CFURL, mediaBox: &box, nil)!
let renderer = UIGraphicsPDFRendererContext(cg)
CommandDeductionPDF.render(month: "تشرين الثاني 2026", amounts: novemberTotals, in: renderer)
renderer.finishPage()
cg.closePDF()
let pdf = PDFDocument(url: file)!
precondition(pdf.pageCount == 1)
let page = pdf.page(at: 0)!
precondition(abs(page.bounds(for: .mediaBox).width - 841.68) < 1)
let allDigits = ArabicText.latinDigits(page.string ?? "").filter(\.isNumber)
for digits in ["2026", "7000", "4000", "10350"] { precondition(allDigits.contains(digits)) }
let image = page.thumbnail(of: CGSize(width: 1684, height: 1190), for: .mediaBox)
let bitmap = NSBitmapImageRep(cgImage: image.cgImage(forProposedRect: nil, context: nil, hints: nil)!)
try bitmap.representation(using: .png, properties: [:])!.write(to: directory.appendingPathComponent("command-deductions.png"))

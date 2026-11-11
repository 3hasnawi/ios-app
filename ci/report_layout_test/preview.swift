import PDFKit

// أسماء وأرقام وهمية فقط؛ لا يقرأ هذا المسار أياً من بيانات المستخدم.
let previewDirectory = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try FileManager.default.createDirectory(at: previewDirectory, withIntermediateDirectories: true)
let previewNames = ["أحمد سامر محمود", "باسم علي حسن", "حسن فاضل ناصر", "خالد سعد كريم", "رائد عماد علي", "سامي كاظم حسن", "عادل حامد سالم", "علي حسن ناصر", "محمد ناصر علي", "مصطفى سعد سالم", "نزار حامد فاضل", "يوسف سامر كريم"]
let previewRows = previewNames.enumerated().map { index, name in
    [String(index + 1), index.isMultiple(of: 2) ? "مفوض" : "شرطي", name, "", "\(101 + index) / ٢٠٢٦"]
}
let previewSheet = ReportSheet(header: "قيادة شرطة محافظة الانبار",
    title: "قائمة الاستقطاعات لشهر تشرين الثاني ٢٠٢٦ - جهة تجريبية",
    columns: deductionColumns, rows: previewRows,
    amounts: previewNames.indices.map { Double(125000 + $0 * 2500) }, entityDeduction: true, deductionList: true).fitted()
let previewLayout = try EntityDeductionPDF.prepare(previewSheet)
precondition(previewLayout.rowHeights.allSatisfy { $0 == 20 })
for column in deductionColumns.dropFirst() {
    let font = EntityDeductionPDF.dataFont(column, sheet: previewSheet)
    precondition(font.fontName.hasPrefix("Arial") && font.pointSize == 11.5)
}
let pdfURL = previewDirectory.appendingPathComponent("deduction-preview.pdf")
var mediaBox = previewLayout.page
let previewCG = CGContext(pdfURL as CFURL, mediaBox: &mediaBox, nil)!
let previewRenderer = UIGraphicsPDFRendererContext(previewCG)
EntityDeductionPDF.render(previewSheet, layout: previewLayout, in: previewRenderer)
previewRenderer.finishPage()
previewCG.closePDF()
try PDFDecor.apply(to: pdfURL, date: Date(timeIntervalSince1970: 1794387600))
let previewPDF = PDFDocument(url: pdfURL)!
precondition(previewPDF.pageCount == 1)
let rendered = previewPDF.page(at: 0)!.thumbnail(of: CGSize(width: 1190, height: 1684), for: .mediaBox)
let bitmap = NSBitmapImageRep(cgImage: rendered.cgImage(forProposedRect: nil, context: nil, hints: nil)!)
try bitmap.representation(using: .png, properties: [:])!.write(to: previewDirectory.appendingPathComponent("deduction-preview.png"))
print("Production CoreText deduction preview exported as PDF and PNG, using Arial Bold 11.5 and 20-point rows")

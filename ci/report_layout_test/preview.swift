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
func exportPreview(_ sheet: ReportSheet, stem: String) throws {
    let layout = try EntityDeductionPDF.prepare(sheet)
    let url = previewDirectory.appendingPathComponent(stem + ".pdf")
    var mediaBox = layout.page
    let cg = CGContext(url as CFURL, mediaBox: &mediaBox, nil)!
    let renderer = UIGraphicsPDFRendererContext(cg)
    EntityDeductionPDF.render(sheet, layout: layout, in: renderer)
    renderer.finishPage()
    cg.closePDF()
    try PDFDecor.apply(to: url, date: Date(timeIntervalSince1970: 1794387600))
    let pdf = PDFDocument(url: url)!
    precondition(pdf.pageCount == 1)
    let rendered = pdf.page(at: 0)!.thumbnail(of: CGSize(width: 1190, height: 1684), for: .mediaBox)
    let bitmap = NSBitmapImageRep(cgImage: rendered.cgImage(forProposedRect: nil, context: nil, hints: nil)!)
    try bitmap.representation(using: .png, properties: [:])!.write(to: previewDirectory.appendingPathComponent(stem + ".png"))
}
try exportPreview(previewSheet, stem: "deduction-preview")
let genericColumns: [ReportSheet.Column] = [
    .init(title: "ت", width: 5, kind: .serial), .init(title: "الرتبة", width: 11, kind: .text),
    .init(title: "الاسم الكامل", width: 34, kind: .text), .init(title: "المبلغ", width: 14, kind: .amount),
    .init(title: "محل العمل", width: 30, kind: .text)
]
let genericRows = previewNames.enumerated().map { index, name in
    [String(index + 1), index.isMultiple(of: 2) ? "مفوض" : "شرطي", name, "", "قسم تجريبي"]
}
let genericSheet = ReportSheet(header: "قيادة شرطة محافظة الانبار", title: "قائمة أسماء ورواتب تجريبية",
    columns: genericColumns, rows: genericRows, amounts: previewNames.indices.map { Double(473000 + $0 * 10000) }).fitted()
try exportPreview(genericSheet, stem: "general-report-preview")
print("Production CoreText previews exported for deduction and general reports with unified table borders")

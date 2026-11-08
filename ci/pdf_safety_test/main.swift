import CoreGraphics
import Foundation

let directory = FileManager.default.temporaryDirectory.appendingPathComponent("salary-pdf-safety-\(UUID().uuidString)", isDirectory: true)
try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: directory) }
let invalid = directory.appendingPathComponent("invalid.pdf")
let damaged = Data("damaged PDF".utf8)
try damaged.write(to: invalid)
do { try PDFDecor.apply(to: invalid); fatalError("Invalid PDF was accepted") } catch { }
let savedInvalid = try Data(contentsOf: invalid)
precondition(savedInvalid == damaged)
let file = directory.appendingPathComponent("valid.pdf")
var box = CGRect(x: 0, y: 0, width: 595, height: 842)
guard let context = CGContext(file as CFURL, mediaBox: &box, nil) else { fatalError("Cannot create fixture") }
for _ in 0..<2 { context.beginPDFPage(nil); context.endPDFPage() }
context.closePDF()
try PDFDecor.apply(to: file)
guard let result = CGPDFDocument(file as CFURL), result.numberOfPages == 2,
      result.page(at: 1) != nil, result.page(at: 2) != nil else { fatalError("Lost PDF pages") }
let remaining = try FileManager.default.contentsOfDirectory(atPath: directory.path)
precondition(!remaining.contains(where: { $0.hasPrefix(".decor-") }))
print("PDF page-preservation checks passed with fictional files")

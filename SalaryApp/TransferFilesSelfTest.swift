#if targetEnvironment(simulator)
import Foundation
import SwiftData
import ZIPFoundation
import PDFKit
import UIKit
import CryptoKit

/// يتحقق من صيغة الملف وقواعد التصنيف باستخدام حسابات وهمية فقط.
@MainActor enum TransferFilesSelfTest {
    struct Failure: LocalizedError { let errorDescription: String? }
    static func run() async throws -> String {
        func check(_ value: Bool, _ message: String) throws { if !value { throw Failure(errorDescription: message) } }
        var customization = TransferLetterCustomizationStore()
        customization.applyToAll(.init(header: "ترويسة عامة", footer: "هامش عام"))
        customization.apply(.init(header: "ترويسة خاصة", committeeFirst: "عضو وهمي"), to: "food_company")
        try check(customization.effective(for: "food_company").header == "ترويسة خاصة" && customization.effective(for: "food_company").footer == "هامش عام", "individual customization inheritance")
        try check(customization.effective(for: "cash_salary").header == "ترويسة عامة", "common customization inheritance")
        customization.applyToAll(.init(header: "ترويسة جديدة"))
        try check(customization.effective(for: "food_company").header == "ترويسة جديدة" && customization.effective(for: "food_company").committeeFirst == "عضو وهمي", "apply to all preserves unrelated fields")
        let savedCustomization = try JSONEncoder().encode(customization)
        try check(try JSONDecoder().decode(TransferLetterCustomizationStore.self, from: savedCustomization) == customization, "customization roundtrip")
        let iban = "IQ06TEST123000000000001"
        let account = SavedExecutionAccount(entity: "جهة وهمية", iban: iban, branch: "مصرف الرافدين فرع تجريبي", swift: "TESTIQBAAAA")
        let anbar = SavedExecutionAccount(entity: "جهة ثانية", iban: iban, branch: "مصرف الرافدين فرع الانبار", swift: "TESTIQBAAAA")
        let other = SavedExecutionAccount(entity: "جهة ثالثة", iban: iban, branch: "مصرف الرشيد فرع تجريبي", swift: "TESTIQBAAAA")
        try check(TransferRules.classify(account) == .rafidain && TransferRules.classify(anbar) == .anbar && TransferRules.classify(other) == .otherBanks, "classification")
        let ambiguous = SavedExecutionAccount(entity: "جهة وهمية", iban: iban, branch: "مصرف الرافدين محافظة الانبار فرع آخر", swift: "TESTIQBAAAA")
        try check(TransferRules.classify(ambiguous) == .unresolved, "ambiguous Anbar branch")
        let sampleRows = (0..<44).map { i in ["\(i + 1)", "جهة وهمية", "IQ00TEST000000000000000", "فرع تجريبي", "100000"] }
        let widths: [CGFloat] = [30, 145, 150, 105, 90]
        let singlePage = try TransferSinglePageFit.fit(rows: sampleRows, widths: widths, availableHeight: 700, fontName: ReportFont.regular(10).fontName)
        try check(singlePage.rowHeights.count == 44 && singlePage.rowHeights.reduce(0, +) <= 700, "single-page group layout")
        do {
            _ = try TransferSinglePageFit.fit(rows: Array(repeating: sampleRows[0], count: 500), widths: widths, availableHeight: 700, fontName: ReportFont.regular(10).fontName)
            throw Failure(errorDescription: "overflowing single-page table accepted")
        } catch is Failure { throw Failure(errorDescription: "overflowing single-page table accepted") }
        catch { }
        try check(TransferRules.classify(nil) == .unresolved && TransferRules.validIBAN(iban) && !TransferRules.validIBAN("IQ00"), "invalid accounts")
        let group = ReportGroup(kind: .deductions, month: "تشرين الاول 2026", title: "جهة وهمية", count: 1, total: 1200, byEntity: false)
        let item = TransferEntity(group: group, account: account, category: .rafidain, missingAmounts: 0)
        let secondGroup = ReportGroup(kind: .deductions, month: group.month, title: "جهة وهمية ثانية", count: 1, total: 1200, byEntity: false)
        let secondItem = TransferEntity(group: secondGroup, account: account, category: .rafidain, missingAmounts: 0)
        let data = try TransferRules.csv([item, secondItem], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        let text = String(decoding: data, as: UTF8.self)
        let lines = text.components(separatedBy: "\r\n")
        try check(!data.starts(with: [0xef, 0xbb, 0xbf]) && lines.count == 3 && !text.hasSuffix("\n"), "encoding")
        let first = lines[1].components(separatedBy: "|")
        let second = lines[2].components(separatedBy: "|")
        try check(first.count == 11 && first[0] == first[1] + "300001" && second[0] == second[1] + "300002", "references")
        try check(first[4] == "1200" && first[5] == "IQD" && first[9] == "استقطاعات \(group.month)" && first[10] == "SLEV", "fields")
        do {
            _ = try TransferRules.csv([item, item], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
            throw Failure(errorDescription: "duplicate transfer accepted")
        } catch is Failure { throw Failure(errorDescription: "duplicate transfer accepted") }
        catch { }
        do {
            _ = try TransferRules.csv([item], category: .rafidain, month: "شهر آخر", payer: "دافع وهمي", payerIBAN: iban)
            throw Failure(errorDescription: "wrong transfer month accepted")
        } catch is Failure { throw Failure(errorDescription: "wrong transfer month accepted") }
        catch { }
        let otherItem = TransferEntity(group: group, account: other, category: .otherBanks, missingAmounts: 0)
        let otherData = try TransferRules.csv([otherItem], category: .otherBanks, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        let otherFields = String(decoding: otherData, as: UTF8.self).components(separatedBy: "\r\n")[1].components(separatedBy: "|")
        try check(otherFields[0] == otherFields[1] + "200001", "other bank reference")
        let invalid = TransferEntity(group: group, account: account, category: .rafidain, missingAmounts: 1)
        do {
            _ = try TransferRules.csv([invalid], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
            throw Failure(errorDescription: "invalid amount accepted")
        } catch is Failure { throw Failure(errorDescription: "invalid amount accepted") }
        catch { }
        var summary = BankStatementSummary(month: group.month)
        summary.payrollCount = 1
        summary.contractCount = 1
        summary.contractNominal = 10001
        summary.contractRetirement = 200
        summary.cashNet = 700
        summary.breakdown = ["شركة عراق الجوال": 100000, "الرعاية الصحية": 100,
                             "صندوق شهداء الشرطة": 200, "النشاط الرياضي": 300,
                             "الضريبة": 400, "الحماية الاجتماعية": 500]
        let columns: Set<String> = ["الرعاية الصحية", "صندوق شهداء الشرطة", "النشاط الرياضي", "الضريبة", "الحماية الاجتماعية", "شركة عراق الجوال", "الاسمي", "التوقيفات التقاعدية", "طريقة الدفع"]
        let figures = try TransferLetterFigureRules.make(summary: summary, available: columns)
        try check(figures.foodFund == 3000 && figures.foodTax == 1746 && figures.foodCompany == 95254, "food allocation")
        try check(figures.treasuryTotal == 900 && figures.contractNominalShare == 1200 && figures.contractTotal == 1400 && figures.payrollCash == 700, "letter totals")
        var unknownPayment = summary
        unknownPayment.unclassifiedCount = 1
        try check(try TransferLetterFigureRules.make(summary: unknownPayment, available: columns).payrollCash == nil, "unclassified payroll accepted as complete cash")
        try check(TransferPayrollRecipient.health.amount(figures) == 100 && TransferPayrollRecipient.fundFood.amount(figures) == 3000 &&
                  TransferPayrollRecipient.fundDonations.amount(figures) == 200 && TransferPayrollRecipient.sports.amount(figures) == 300, "payroll transfer sources")
        let period = try TransferLetterPeriod("تشرين الأول 2026")
        try check(period.month == 10 && period.year == 2026 && period.signatureDate == " / 10 / 2026", "letter month")
        let cases: [(Int64, String)] = [(0, "صفر دينار"), (1, "دينار واحد"), (2, "ديناران"), (3, "ثلاثة دنانير"), (12, "اثنا عشر دينار"),
                                         (1001, "ألف ودينار واحد"), (2000000, "مليونان دينار"),
                                         (95254, "خمسة وتسعون ألف ومائتان وأربعة وخمسون دينار")]
        for (amount, expected) in cases { try check(try ArabicAmountWords.dinars(amount) == expected, "amount words") }
        let duplicated = TransferEntity(group: group, account: account, category: .rafidain, missingAmounts: 0, duplicateSource: true)
        try check(!TransferRules.issues(duplicated).isEmpty, "duplicate source not flagged")
        let source = [TransferReconcileLine(sourceID: "execution:a", title: "جهة وهمية أ", amount: 100),
                      TransferReconcileLine(sourceID: "execution:b", title: "جهة وهمية ب", amount: 200)]
        try check(TransferReconciliation.check(expected: source, book: source, file: source, target: .grouped).isEmpty, "matching transfers")
        let missing = TransferReconciliation.check(expected: source, book: source, file: [source[0]], target: .grouped)
        try check(missing.contains { $0.kind == .missingEntityInFile && $0.sourceID == "execution:b" }, "missing entity not flagged")
        let changed = [source[0], TransferReconcileLine(sourceID: "execution:b", title: "جهة وهمية ب", amount: 199)]
        try check(TransferReconciliation.check(expected: source, book: source, file: changed, target: .grouped).contains { $0.kind == .fileAmount }, "changed amount not flagged")
        try check(TransferReconciliation.check(expected: source, book: changed, file: source, target: .grouped).contains { $0.kind == .bookAmount }, "book amount not flagged")
        try check(TransferReconciliation.check(expected: source, book: source, file: source + [source[0]], target: .grouped).contains { $0.kind == .duplicateSource }, "duplicate not flagged")
        try check(TransferReconciliation.check(expected: source, book: source, file: nil, target: .grouped).contains { $0.kind == .missingFile }, "missing group file not flagged")
        try check(TransferReconciliation.check(expected: source, book: source, file: nil, target: .individual).isEmpty, "individual letter incorrectly requires CSV")
        try check(TransferReconciliation.check(expected: source, book: nil, file: nil, target: .individual).contains { $0.kind == .missingBook }, "missing individual letter")
        let absentGroup = TransferReconciliation.check(expected: source, book: nil, file: nil, target: .grouped)
        try check(absentGroup.contains { $0.kind == .missingBook } && absentGroup.contains { $0.kind == .missingFile }, "missing group letter and file")
        let employeeRows = [TransferReconcileEmployee(name: "أحمد وهمي", amount: 100), TransferReconcileEmployee(name: "أحمد وهمي", amount: 200)]
        let wrongAmounts = [TransferReconcileEmployee(name: "احمد وهمي", amount: 100), TransferReconcileEmployee(name: "احمد وهمي", amount: 199)]
        let employeeIssues = TransferEmployeeReconciliation.check(expected: employeeRows, book: wrongAmounts, previousCount: 2)
        try check(employeeIssues.contains { $0.kind == .employeeAmounts } && employeeIssues.contains { $0.kind == .countChanged }, "individual names and count")
        let missingEmployee = TransferEmployeeReconciliation.check(expected: employeeRows, book: [wrongAmounts[0]])
        try check(missingEmployee.contains { $0.kind == .employeeCount } && missingEmployee.contains { $0.kind == .employeeNames }, "missing employee row")
        do {
            _ = try TransferLetterFigureRules.make(summary: summary, available: [])
            throw Failure(errorDescription: "missing payroll columns accepted")
        } catch is Failure { throw Failure(errorDescription: "missing payroll columns accepted") }
        catch { }
        let schema = Schema([PayrollMonth.self, Employee.self, Deduction.self, StaffMember.self, ExecutionAccount.self])
        let config = ModelConfiguration(schema: schema, isStoredInMemoryOnly: true)
        let container = try ModelContainer(for: schema, configurations: [config])
        let context = ModelContext(container)
        let headers = ["الرعاية الصحية", "صندوق الشهداء", "نشاط رياضي", "الضريبة", "الحماية الاجتماعية", "شركة عراق الجوال", "الاسمي", "توقيفات تقاعدية", "طريقة الدفع"]
        let values = ["100", "200", "300", "400", "500", "100000", "10001", "200", "كاش"]
        let employee = Employee(month: group.month, name: "موظف وهمي", rank: "عقود", workplace: "قسم وهمي", netSalary: 1000,
                                items: ["الرعاية الصحية": 100, "صندوق الشهداء": 200, "نشاط رياضي": 300, "الضريبة": 400,
                                        "الحماية الاجتماعية": 500, "شركة عراق الجوال": 100000, "الاسمي": 10001, "توقيفات تقاعدية": 200],
                                details: ["طريقة الدفع": "كاش"],
                                rawColumnsData: PayrollColumnSnapshot.encode(headers: headers, values: values))
        context.insert(employee)
        context.insert(Employee(month: group.month, name: "موظف بطاقة وهمي", rank: "موظف", workplace: "قسم وهمي", netSalary: 250,
                                details: ["طريقة الدفع": "بطاقة"]))
        try context.save()
        let engine = TransferLetterFigureEngine(modelContainer: container)
        let actual = try await engine.figures(month: group.month)
        let cashList = try await TransferCashListEngine(modelContainer: container).list(month: group.month)
        try check(actual.treasuryTotal == 900 && actual.foodCompany == 95254 && actual.contractTotal == 1400 && actual.payrollCash == 1000 && cashList.total == 1000 && cashList.employees.count == 1, "payroll data integration")
        let syntheticRows = (1...80).map { i in TransferCashEmployee(id: i, name: "منتسب وهمي \(i)", rank: "موظف", workplace: "قسم تجريبي", amount: 1000) }
        let syntheticList = TransferCashList(month: group.month, employees: syntheticRows)
        let cashPDF = try await TransferCashListPDF.make(syntheticList)
        defer { try? FileManager.default.removeItem(at: cashPDF.deletingLastPathComponent()) }
        guard let pdf = PDFDocument(url: cashPDF) else { throw Failure(errorDescription: "cash list PDF unreadable") }
        try check(pdf.pageCount >= 2 && syntheticList.total == 80000, "cash list PDF pagination")
        let incompleteList = TransferCashList(month: group.month, employees: [TransferCashEmployee(id: 1, name: "موظف وهمي", rank: "", workplace: "قسم وهمي", amount: 1000)])
        do {
            _ = try await TransferCashListPDF.make(incompleteList)
            throw Failure(errorDescription: "cash list missing rank accepted")
        } catch is Failure { throw Failure(errorDescription: "cash list missing rank accepted") }
        catch { }
        let fakeLetterURL = FileManager.default.temporaryDirectory.appendingPathComponent("cash-letter-test-\(UUID().uuidString).pdf")
        defer { try? FileManager.default.removeItem(at: fakeLetterURL) }
        let fakeLetterPDF = UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 595, height: 842)).pdfData { $0.beginPage() }
        try fakeLetterPDF.write(to: fakeLetterURL)
        let combined = try await TransferCashLetterPackage.make(letter: fakeLetterURL, list: cashList, figures: actual)
        defer { try? FileManager.default.removeItem(at: combined.deletingLastPathComponent()) }
        try check(PDFDocument(url: combined)?.pageCount == 2, "cash letter appendix")
        do {
            _ = try await TransferCashLetterPackage.make(letter: fakeLetterURL, list: syntheticList, figures: actual)
            throw Failure(errorDescription: "cash appendix total mismatch accepted")
        } catch is Failure { throw Failure(errorDescription: "cash appendix total mismatch accepted") }
        catch { }
        var broken = values
        broken[0] = "100 خطأ"
        employee.rawColumnsData = PayrollColumnSnapshot.encode(headers: headers, values: broken)
        try context.save()
        do {
            _ = try await TransferLetterFigureEngine(modelContainer: container).figures(month: group.month)
            throw Failure(errorDescription: "invalid payroll cell accepted")
        } catch is Failure { throw Failure(errorDescription: "invalid payroll cell accepted") }
        catch { }
        context.insert(Employee(month: group.month, name: "موظف غير مصنف وهمي", rank: "موظف", workplace: "قسم وهمي", netSalary: 300))
        try context.save()
        do {
            _ = try await TransferCashListEngine(modelContainer: container).list(month: group.month)
            throw Failure(errorDescription: "unknown payment accepted")
        } catch is Failure { throw Failure(errorDescription: "unknown payment accepted") }
        catch { }
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("word-test-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("word"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try Data("<types/>".utf8).write(to: root.appendingPathComponent("[Content_Types].xml"))
        let templateXML = "<document xmlns:w=\"urn:word-test\"><text>{{AMOUNT}}</text><text>{{MONTH}}</text><w:tbl><w:tr><w:tc><w:p><w:r><w:t>{{ROW_NAME}}</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>{{ROW_AMOUNT}}</w:t></w:r></w:p></w:tc></w:tr></w:tbl></document>"
        try Data(templateXML.utf8).write(to: root.appendingPathComponent("word/document.xml"))
        let input = root.appendingPathComponent("template.docx"), output = root.appendingPathComponent("result.docx")
        let template = try Archive(url: input, accessMode: .create)
        try template.addEntry(with: "[Content_Types].xml", relativeTo: root, compressionMethod: .deflate)
        try template.addEntry(with: "word/document.xml", relativeTo: root, compressionMethod: .deflate)
        let wordRows = [["ROW_NAME": "شخص تجريبي أ", "ROW_AMOUNT": "100"], ["ROW_NAME": "شخص تجريبي ب", "ROW_AMOUNT": "200"]]
        try TransferWordTemplate.render(template: input, destination: output, fields: ["AMOUNT": "1 & 2", "MONTH": "تشرين الأول"], tableRows: wordRows)
        let result = try Archive(url: output, accessMode: .read)
        guard let document = result["word/document.xml"] else { throw Failure(errorDescription: "word document missing") }
        var xml = Data()
        _ = try result.extract(document) { xml.append($0) }
        let xmlText = String(decoding: xml, as: UTF8.self)
        try check(xmlText.contains("1 &amp; 2") && xmlText.contains("تشرين الأول") && xmlText.contains("شخص تجريبي أ") && xmlText.contains("شخص تجريبي ب") && !xmlText.contains("{{"), "word template fields and table")
        do {
            try TransferWordTemplate.render(template: input, destination: root.appendingPathComponent("bad.docx"), fields: ["AMOUNT": "1"], tableRows: wordRows)
            throw Failure(errorDescription: "incomplete template accepted")
        } catch is Failure { throw Failure(errorDescription: "incomplete template accepted") }
        catch { }
        let referencesDir = root.appendingPathComponent("templates", isDirectory: true)
        try FileManager.default.createDirectory(at: referencesDir, withIntermediateDirectories: true)
        let renderer = UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 595.3, height: 841.9))
        let singlePDF = renderer.pdfData { $0.beginPage() }
        let groupPDF = renderer.pdfData { $0.beginPage(); $0.beginPage() }
        var records: [TransferReferenceManifest.Template] = []
        for (id, count) in TransferReferencePack.expected {
            let contents = count == 2 ? groupPDF : singlePDF
            let path = "templates/\(id).pdf"
            try contents.write(to: root.appendingPathComponent(path))
            let digest = SHA256.hash(data: contents).map { String(format: "%02x", $0) }.joined()
            records.append(.init(id: id, path: path, sha256: digest, pages: count))
        }
        let manifest = TransferReferenceManifest(version: 1, templates: records)
        try JSONEncoder().encode(manifest).write(to: root.appendingPathComponent("manifest.json"))
        let package = root.appendingPathComponent("references.zip")
        let archive = try Archive(url: package, accessMode: .create)
        try archive.addEntry(with: "manifest.json", relativeTo: root, compressionMethod: .deflate)
        for record in records { try archive.addEntry(with: record.path, relativeTo: root, compressionMethod: .deflate) }
        try check(try TransferReferencePack.validate(package).templates.count == 11, "reference package")
        try check(try TransferReferencePack.validateData(Data(contentsOf: package)).templates.count == 11, "backup reference package")
        let serialized = try PropertyListSerialization.data(fromPropertyList: Data(contentsOf: package), format: .binary, options: 0)
        guard let restoredData = try PropertyListSerialization.propertyList(from: serialized, format: nil) as? Data else { throw Failure(errorDescription: "backup reference encoding") }
        try check(try TransferReferencePack.validateData(restoredData).templates.count == 11, "backup reference roundtrip")
        let digitRenderer = UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 612, height: 792))
        let digitPDF = digitRenderer.pdfData { context in
            context.beginPage()
            ("0123456789" as NSString).draw(at: CGPoint(x: 40, y: 40), withAttributes: [.font: UIFont.boldSystemFont(ofSize: 18)])
        }
        let digitGlyphs = try TransferMonthDigits.extract(from: digitPDF)
        try check(digitGlyphs.count == 10 && digitGlyphs.allSatisfy { !$0.png.isEmpty && $0.width > 0 && $0.height > 0 }, "month digit glyphs")
        let digitPath = "templates/\(TransferReferencePack.digitTemplateID).pdf"
        try digitPDF.write(to: root.appendingPathComponent(digitPath))
        let digitDigest = SHA256.hash(data: digitPDF).map { String(format: "%02x", $0) }.joined()
        let versionTwoRecords = records + [.init(id: TransferReferencePack.digitTemplateID, path: digitPath, sha256: digitDigest, pages: 1)]
        try JSONEncoder().encode(TransferReferenceManifest(version: 2, templates: versionTwoRecords)).write(to: root.appendingPathComponent("manifest.json"))
        let secondPackage = root.appendingPathComponent("references-v2.zip")
        let secondArchive = try Archive(url: secondPackage, accessMode: .create)
        try secondArchive.addEntry(with: "manifest.json", relativeTo: root, compressionMethod: .deflate)
        for record in versionTwoRecords { try secondArchive.addEntry(with: record.path, relativeTo: root, compressionMethod: .deflate) }
        try check(try TransferReferencePack.validateData(Data(contentsOf: secondPackage)).templates.count == 12, "digit reference package")
        let selectedReferences = try TransferReferencePack.load(["food_company", TransferReferencePack.digitTemplateID], from: Data(contentsOf: secondPackage))
        try check(selectedReferences.count == 2 && selectedReferences[TransferReferencePack.digitTemplateID] == digitPDF, "reference selection")
        // مرجع وهمي بالأبعاد نفسها لاختبار خط الكتاب كاملاً دون استخدام بيانات المالك.
        let letterRenderer = UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 595.3, height: 841.9))
        let fakeLetter = letterRenderer.pdfData { context in
            context.beginPage()
            let regular = UIFont(name: "TimesNewRomanPSMT", size: 14.04) ?? ReportFont.regular(14.04)
            let bold = UIFont(name: "TimesNewRomanPS-BoldMT", size: 16) ?? ReportFont.bold(16)
            PDFText.draw("100,000", in: CGRect(x: 298.61, y: 317.41, width: 105.02, height: 17.79), font: ReportFont.bold(16), align: .right)
            PDFText.draw("مبلغ وهمي", in: CGRect(x: 149.71, y: 319.34, width: 128.87, height: 15.54), font: regular, align: .right)
            PDFText.draw("دينار", in: CGRect(x: 266.05, y: 335.78, width: 294.99, height: 15.54), font: regular, align: .right)
            let yearBoxes = [CGRect(x: 35.52, y: 180.60, width: 33.63, height: 16.73), CGRect(x: 428.47, y: 416.30, width: 31.65, height: 15.54), CGRect(x: 468.46, y: 675.64, width: 35.97, height: 17.67), CGRect(x: 312.05, y: 675.64, width: 35.97, height: 17.67), CGRect(x: 94.22, y: 675.42, width: 36.06, height: 17.79)]
            let yearFonts = [ReportFont.bold(15), UIFont(name: "TimesNewRomanPS-BoldMT", size: 14.04) ?? ReportFont.bold(14.04), bold, bold, ReportFont.bold(16)]
            for (box, font) in zip(yearBoxes, yearFonts) { PDFText.draw("2026", in: box, font: font, align: .right) }
            for box in [CGRect(x: 82.94, y: 178.27, width: 15.16, height: 21.65), CGRect(x: 512.86, y: 675.64, width: 17.97, height: 17.67), CGRect(x: 356.59, y: 675.64, width: 17.97, height: 17.67)] { PDFText.draw("10", in: box, font: bold, align: .right) }
            PDFText.draw("شهر", in: CGRect(x: 471.10, y: 416.30, width: 55.86, height: 15.54), font: regular, align: .right)
            PDFText.draw("أول", in: CGRect(x: 172.94, y: 675.64, width: 33.93, height: 17.67), font: bold, align: .right)
            PDFText.draw("شهر", in: CGRect(x: 143.31, y: 675.64, width: 25.61, height: 17.67), font: bold, align: .right)
            PDFText.draw(iban, in: CGRect(x: 384, y: 350, width: 175, height: 19), font: ReportFont.regular(10), align: .right)
        }
        let fakeCompanyPath = "templates/food_company.pdf"
        try fakeLetter.write(to: root.appendingPathComponent(fakeCompanyPath))
        let fakeCompanyDigest = SHA256.hash(data: fakeLetter).map { String(format: "%02x", $0) }.joined()
        let fakeCompanyRecords = versionTwoRecords.map { record in
            record.id == "food_company" ? TransferReferenceManifest.Template(id: record.id, path: record.path, sha256: fakeCompanyDigest, pages: 1) : record
        }
        try JSONEncoder().encode(TransferReferenceManifest(version: 2, templates: fakeCompanyRecords)).write(to: root.appendingPathComponent("manifest.json"))
        let fakeCompanyPackage = root.appendingPathComponent("fake-company.zip")
        let fakeCompanyArchive = try Archive(url: fakeCompanyPackage, accessMode: .create)
        try fakeCompanyArchive.addEntry(with: "manifest.json", relativeTo: root, compressionMethod: .deflate)
        for record in fakeCompanyRecords { try fakeCompanyArchive.addEntry(with: record.path, relativeTo: root, compressionMethod: .deflate) }
        let companyOutput = try await TransferCompanyLetter.make(figures: figures, payerIBAN: iban, references: Data(contentsOf: fakeCompanyPackage))
        defer { try? FileManager.default.removeItem(at: companyOutput.deletingLastPathComponent()) }
        guard let companyDoc = PDFDocument(url: companyOutput), let outputText = companyDoc.page(at: 0)?.string else { throw Failure(errorDescription: "company PDF unreadable") }
        try check(companyDoc.pageCount == 1 && outputText.contains("95,254") && !outputText.contains("100,000"), "company PDF source and amount")
        let fakeCashPDF = letterRenderer.pdfData { context in
            context.beginPage()
            let regular = UIFont(name: "TimesNewRomanPSMT", size: 14.04) ?? ReportFont.regular(14.04)
            let bold = UIFont(name: "TimesNewRomanPS-BoldMT", size: 16) ?? ReportFont.bold(16)
            PDFText.draw("100,000", in: CGRect(x: 355.27, y: 317.344, width: 72.166, height: 15.654), font: ReportFont.bold(14.04), align: .right)
            PDFText.draw("مبلغ وهمي", in: CGRect(x: 168.122, y: 317.54, width: 167.244, height: 15.543), font: regular, align: .right)
            PDFText.draw("دينار", in: CGRect(x: 289.25, y: 333.62, width: 271.846, height: 15.543), font: regular, align: .right)
            let boxes = [CGRect(x: 35.52, y: 180.605, width: 33.654, height: 16.725), CGRect(x: 40.2, y: 381.86, width: 31.52, height: 15.543), CGRect(x: 468.46, y: 666.76, width: 35.97, height: 17.667), CGRect(x: 312.05, y: 666.76, width: 35.99, height: 17.667), CGRect(x: 94.224, y: 666.656, width: 36.086, height: 17.796)]
            let fonts = [ReportFont.bold(15), UIFont(name: "TimesNewRomanPS-BoldMT", size: 14.04) ?? ReportFont.bold(14.04), bold, bold, ReportFont.bold(16)]
            for (box, font) in zip(boxes, fonts) { PDFText.draw("2026", in: box, font: font, align: .right) }
            for box in [CGRect(x: 82.944, y: 178.268, width: 15.156, height: 21.654), CGRect(x: 512.86, y: 666.76, width: 17.97, height: 17.667), CGRect(x: 356.59, y: 666.76, width: 17.97, height: 17.667)] { PDFText.draw("10", in: box, font: bold, align: .right) }
            PDFText.draw("شهر وهمي", in: CGRect(x: 85.704, y: 381.86, width: 66.351, height: 15.543), font: regular, align: .right)
            PDFText.draw("شهر وهمي", in: CGRect(x: 143.308, y: 666.88, width: 63.563, height: 17.667), font: bold, align: .right)
            PDFText.draw(iban, in: CGRect(x: 385.99, y: 349.70, width: 170.59, height: 15.55), font: ReportFont.regular(10), align: .right)
        }
        let fakeCashPath = "templates/cash_salary.pdf"
        try fakeCashPDF.write(to: root.appendingPathComponent(fakeCashPath))
        let fakeCashDigest = SHA256.hash(data: fakeCashPDF).map { String(format: "%02x", $0) }.joined()
        let fakeCashRecords = fakeCompanyRecords.map { record in
            record.id == "cash_salary" ? TransferReferenceManifest.Template(id: record.id, path: record.path, sha256: fakeCashDigest, pages: 1) : record
        }
        try JSONEncoder().encode(TransferReferenceManifest(version: 2, templates: fakeCashRecords)).write(to: root.appendingPathComponent("manifest.json"))
        let fakeCashPackage = root.appendingPathComponent("fake-cash.zip")
        let fakeCashArchive = try Archive(url: fakeCashPackage, accessMode: .create)
        try fakeCashArchive.addEntry(with: "manifest.json", relativeTo: root, compressionMethod: .deflate)
        for record in fakeCashRecords { try fakeCashArchive.addEntry(with: record.path, relativeTo: root, compressionMethod: .deflate) }
        let cashLetter = try await TransferCashLetter.makeWithList(figures: actual, list: cashList, payerIBAN: iban, references: Data(contentsOf: fakeCashPackage))
        defer { try? FileManager.default.removeItem(at: cashLetter.deletingLastPathComponent()) }
        guard let cashLetterDoc = PDFDocument(url: cashLetter), let cashLetterText = cashLetterDoc.page(at: 0)?.string else { throw Failure(errorDescription: "cash letter unreadable") }
        try check(cashLetterDoc.pageCount == 2 && cashLetterText.contains("1,000") && !cashLetterText.contains("100,000"), "cash letter and appendix")
        let blankRenderer = UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 595.3, height: 841.9))
        let blankPDF = blankRenderer.pdfData { context in
            context.beginPage()
            ("Static heading" as NSString).draw(at: CGPoint(x: 40, y: 40), withAttributes: [.font: UIFont.systemFont(ofSize: 12)])
        }
        let slot = TransferPDFTextSlot(rect: CGRect(x: 45, y: 100, width: 160, height: 28), fontName: ReportFont.regular(12).fontName, fontSize: 12, alignment: .right)
        let monthSlot = TransferPDFMonthSlot(rect: CGRect(x: 230, y: 100, width: 35, height: 30), month: 10, glyphs: digitGlyphs)
        let composed = try await TransferPDFOverlay.make(blankReference: blankPDF, flows: [TransferPDFTextFlow(value: "123456", slots: [slot])], months: [monthSlot])
        defer { try? FileManager.default.removeItem(at: composed.deletingLastPathComponent()) }
        guard let composedDoc = PDFDocument(url: composed), let composedText = composedDoc.page(at: 0)?.string else { throw Failure(errorDescription: "composed PDF missing") }
        try check(composedDoc.pageCount == 1 && composedText.contains("123456"), "PDF overlay")
        let occupiedPDF = blankRenderer.pdfData { context in
            context.beginPage()
            ("OLD" as NSString).draw(at: CGPoint(x: 55, y: 104), withAttributes: [.font: UIFont.systemFont(ofSize: 12)])
        }
        do {
            _ = try await TransferPDFOverlay.make(blankReference: occupiedPDF, flows: [TransferPDFTextFlow(value: "123456", slots: [slot])])
            throw Failure(errorDescription: "old PDF field was not rejected")
        } catch is Failure { throw Failure(errorDescription: "old PDF field was not rejected") }
        catch { }
        let occupiedHash = SHA256.hash(data: occupiedPDF).map { String(format: "%02x", $0) }.joined()
        let preparedBackground = try await TransferPDFBackground.make(reference: occupiedPDF, sha256: occupiedHash, pageIndex: 0,
                                                                       masks: [CGRect(x: 45, y: 100, width: 160, height: 28)])
        guard let preparedDocument = PDFDocument(data: preparedBackground) else { throw Failure(errorDescription: "prepared background unreadable") }
        try check(!(preparedDocument.page(at: 0)?.string ?? "").contains("OLD"), "old field survived in PDF text")
        do {
            _ = try await TransferPDFBackground.make(reference: occupiedPDF, sha256: String(repeating: "0", count: 64), pageIndex: 0,
                                                      masks: [CGRect(x: 45, y: 100, width: 160, height: 28)])
            throw Failure(errorDescription: "wrong template hash accepted")
        } catch is Failure { throw Failure(errorDescription: "wrong template hash accepted") }
        catch { }
        do {
            _ = try await TransferPDFOverlay.make(blankReference: blankPDF, flows: [TransferPDFTextFlow(value: String(repeating: "1234567890", count: 100), slots: [slot])])
            throw Failure(errorDescription: "overflowing PDF field accepted")
        } catch is Failure { throw Failure(errorDescription: "overflowing PDF field accepted") }
        catch { }
        var badRecords = records
        badRecords[0] = .init(id: records[0].id, path: records[0].path, sha256: String(repeating: "0", count: 64), pages: records[0].pages)
        try JSONEncoder().encode(TransferReferenceManifest(version: 1, templates: badRecords)).write(to: root.appendingPathComponent("manifest.json"))
        let badPackage = root.appendingPathComponent("bad-references.zip")
        let badArchive = try Archive(url: badPackage, accessMode: .create)
        try badArchive.addEntry(with: "manifest.json", relativeTo: root, compressionMethod: .deflate)
        for record in records { try badArchive.addEntry(with: record.path, relativeTo: root, compressionMethod: .deflate) }
        do {
            _ = try TransferReferencePack.validateData(Data(contentsOf: badPackage))
            throw Failure(errorDescription: "damaged template package accepted")
        } catch is Failure { throw Failure(errorDescription: "damaged template package accepted") }
        catch { }
        return "transferFiles=ok encoding=ok classification=ok references=ok validation=ok letterFigures=ok wordTemplate=ok referencePack=ok companyLetter=ok"
    }
}
#endif

#if targetEnvironment(simulator)
import Foundation
import UIKit
import CoreText
import PDFKit
import ZIPFoundation
import CryptoKit

/// اختبارات مستقلة لمسار Word، لا تستخدم قوالب أو حسابات حقيقية.
@MainActor enum TransferWordSelfTest {
    struct Failure: LocalizedError { let errorDescription: String? }
    private(set) static var phase = "start"
    static func run() throws -> String {
        phase = "font-permissions"
        func check(_ ok: Bool, _ message: String) throws { if !ok { throw Failure(errorDescription: message) } }
        func fontWithFlags(_ flags: UInt16) -> Data {
            var bytes = [UInt8](repeating: 0, count: 38)
            bytes[5] = 1
            bytes[12] = 0x4f; bytes[13] = 0x53; bytes[14] = 0x2f; bytes[15] = 0x32
            bytes[23] = 28; bytes[27] = 10
            bytes[36] = UInt8(flags >> 8); bytes[37] = UInt8(flags & 0xff)
            return Data(bytes)
        }
        try check(TransferWordFont.permitsEditableEmbedding(fontWithFlags(0x0008)), "editable font permission")
        try check(!TransferWordFont.permitsEditableEmbedding(fontWithFlags(0x0002)) &&
                  !TransferWordFont.permitsEditableEmbedding(fontWithFlags(0x0200)) &&
                  !TransferWordFont.permitsEditableEmbedding(Data()), "restricted font permission")
        phase = "kashida"
        var oldForm = CustomTransferTemplate()
        oldForm.title = "قالب وهمي"
        oldForm.bodyAlignment = .right
        let oldRoundTrip = try CustomTransferTemplate.decode(CustomTransferTemplate.encode([oldForm]))
        try check(oldRoundTrip.first?.bodyKashida == nil && oldRoundTrip.first?.bodyAlignment == .right, "old template alignment")
        oldForm.fontSizes = ["body": 19, "attachments": 11, "signatures": 14]
        let sizedRoundTrip = try CustomTransferTemplate.decode(CustomTransferTemplate.encode([oldForm]))
        try check(sizedRoundTrip.first?.halfPoints(.body, default: 28) == 38 &&
                  sizedRoundTrip.first?.halfPoints(.attachments, default: 22) == 22 &&
                  sizedRoundTrip.first?.halfPoints(.subject, default: 32) == 32, "font size template round trip")
        try check(TransferWordGroupDraft.bodyParagraph("متن تجريبي", template: sizedRoundTrip.first).halfPoints == 38, "body font size applied")
        var invalidSizes = oldForm
        invalidSizes.fontSizes = ["body": 99]
        do {
            _ = try CustomTransferTemplate.encode([invalidSizes])
            throw Failure(errorDescription: "invalid font size accepted")
        } catch is Failure { throw Failure(errorDescription: "invalid font size accepted") }
        catch { }
        oldForm.bodyKashida = .high
        let newRoundTrip = try CustomTransferTemplate.decode(CustomTransferTemplate.encode([oldForm]))
        try check(newRoundTrip.first?.bodyKashida == .high, "kashida template round trip")
        let formatted = TransferWordGroupDraft.bodyParagraph("نرجو تحويل مبلغ إلى الحساب", template: oldForm)
        try check(formatted.kashida == .high && formatted.alignment == .justified, "kashida body style")
        let face = CTFontCreateWithName("TimesNewRomanPSMT" as CFString, 14, nil)
        let sample = "نرجو تحويل مبلغ إلى الحساب IQ06TEST123000000000001 123,456"
        let unchanged = TransferAdaptivePDF.stretchArabic(sample, font: face, width: 500, mode: .none)
        let small = TransferAdaptivePDF.stretchArabic(sample, font: face, width: 500, mode: .low)
        let large = TransferAdaptivePDF.stretchArabic(sample, font: face, width: 500, mode: .high)
        try check(unchanged == sample && small.replacingOccurrences(of: "ـ", with: "") == sample &&
                  large.replacingOccurrences(of: "ـ", with: "") == sample && large.count > small.count && small.count > sample.count &&
                  large.contains("IQ06TEST123000000000001") && large.contains("123,456"), "safe kashida rendering")
        try check(TransferAdaptivePDF.stretchArabic("IQ06TEST123000000000001", font: face, width: 500, mode: .high) == "IQ06TEST123000000000001", "IBAN unchanged")
        var defaults = TransferLetterCustomizationStore()
        defaults.common = .init(header: "ترويسة وهمية", fixedNumber: "عدد وهمي", foodContractReference: "عقد وهمي")
        for id in TransferReferencePack.expected.keys {
            defaults.individual[id] = .init(committeeFirst: "عضو أول", committeeSecond: "عضو ثان", committeeThird: "عضو ثالث", footer: "هامش وهمي")
        }
        let placeholderHashes = Dictionary(uniqueKeysWithValues: TransferReferencePack.expected.keys.map { ($0, String(repeating: "0", count: 64)) })
        let package = TransferWordDefaults.Package(version: 1, defaults: defaults, referenceHashes: placeholderHashes)
        let decoded = try TransferWordDefaults.decode(JSONEncoder().encode(package))
        var prior = TransferLetterCustomizationStore()
        prior.applyToAll(.init(header: "ترويسة مخصصة"))
        prior.apply(.init(committeeFirst: "عضو مخصص"), to: "food_company")
        let merged = TransferWordDefaults.merging(decoded.defaults, preserving: prior)
        try check(merged.effective(for: "food_company").header == "ترويسة مخصصة" &&
                  merged.effective(for: "food_company").committeeFirst == "عضو مخصص" &&
                  merged.effective(for: "food_company").committeeSecond == "عضو ثان", "Word defaults preserve customizations")
        var incompleteDefaults = defaults
        incompleteDefaults.individual.removeValue(forKey: "water")
        do {
            _ = try TransferWordDefaults.decode(JSONEncoder().encode(TransferWordDefaults.Package(version: 1, defaults: incompleteDefaults, referenceHashes: placeholderHashes)))
            throw Failure(errorDescription: "incomplete Word settings accepted")
        } catch is Failure { throw Failure(errorDescription: "incomplete Word settings accepted") }
        catch { }
        let payer = "IQ06TEST123000000000001", beneficiary = "IQ76TEST123000000000002"
        let local = String(beneficiary.suffix(15)), month = "تشرين الاول 2026"
        try check(TransferRules.validIBAN(payer) && TransferRules.validIBAN(beneficiary), "synthetic accounts")
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("word-selftest-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("templates"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        let syntheticFont = try Data(contentsOf: documents.appendingPathComponent("synthetic-word-font.ttf"))
        let fontDocument = root.appendingPathComponent("synthetic-font.docx")
        try TransferWordDocument.write(blocks: [.paragraph(.init(text: "ترويسة وهمية", font: "Sakkal Majalla", halfPoints: 36, bold: true))],
                                       destination: fontDocument, sakkalBold: syntheticFont)
        let fontArchive = try Archive(url: fontDocument, accessMode: .read)
        guard let fontEntry = fontArchive["word/fonts/sakkal-bold.ttf"], fontArchive["word/fontTable.xml"] != nil,
              fontArchive["word/_rels/fontTable.xml.rels"] != nil else { throw Failure(errorDescription: "embedded Word font missing") }
        var recoveredFont = Data(); _ = try fontArchive.extract(fontEntry) { recoveredFont.append($0) }
        try check(recoveredFont == syntheticFont, "embedded Word font changed")
        guard let font = UIFont(name: "Arial-BoldMT", size: 10) else { throw Failure(errorDescription: "synthetic reference font") }
        func payerRect(_ id: String) -> CGRect {
            switch id {
            case "food_company": CGRect(x: 384, y: 350, width: 175, height: 19)
            case "food_tax", "ramadi", "cash_salary": CGRect(x: 385.99, y: 349.7, width: 170.586, height: 15.543)
            case "tax_social": CGRect(x: 97.584, y: 333.62, width: 170.6, height: 15.543)
            case "contracts": CGRect(x: 126.26, y: 333.62, width: 170.586, height: 15.543)
            case "housing": CGRect(x: 211.37, y: 299.06, width: 170.586, height: 15.543)
            case "water": CGRect(x: 385.99, y: 333.62, width: 170.586, height: 15.543)
            case "rafidain_anbar": CGRect(x: 161.06, y: 299.06, width: 170.6, height: 15.543)
            default: CGRect(x: 385.99, y: 337.58, width: 170.685, height: 15.543)
            }
        }
        func targetRect(_ id: String) -> CGRect {
            switch id {
            case "water": CGRect(x: 40.20, y: 349.70, width: 104.91, height: 15.54)
            case "housing": CGRect(x: 140.42, y: 315.14, width: 105.20, height: 15.54)
            case "ramadi": CGRect(x: 334.39, y: 365.78, width: 170.66, height: 15.54)
            default: CGRect(x: 220, y: 450, width: 190, height: 18)
            }
        }
        var records: [TransferReferenceManifest.Template] = []
        for (id, count) in TransferReferencePack.expected {
            phase = "synthetic-reference-\(id)"
            let pdf = autoreleasepool {
                let renderer = UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 595.3, height: 841.9))
                return renderer.pdfData { context in
                    context.beginPage()
                    PDFText.draw(payer, in: payerRect(id), font: font, align: .center)
                    if id != "rafidain_anbar" && id != "rafidain_group" && id != "other_banks_group" {
                        PDFText.draw(id == "water" || id == "housing" ? local : beneficiary, in: targetRect(id), font: font, align: .center)
                    }
                    if count == 2 { context.beginPage() }
                }
            }
            guard let document = PDFDocument(data: pdf) else { throw Failure(errorDescription: "synthetic PDF unreadable \(id) bytes=\(pdf.count)") }
            guard !document.isLocked else { throw Failure(errorDescription: "synthetic PDF locked \(id)") }
            guard document.pageCount == count else { throw Failure(errorDescription: "synthetic PDF pages \(id) actual=\(document.pageCount) expected=\(count)") }
            guard let firstPage = document.page(at: 0) else { throw Failure(errorDescription: "synthetic PDF missing page \(id)") }
            let box = firstPage.bounds(for: .mediaBox)
            guard abs(box.width - 595.3) < 2, abs(box.height - 841.9) < 2 else {
                throw Failure(errorDescription: "synthetic PDF size \(id) width=\(Int(box.width)) height=\(Int(box.height))")
            }
            for pageIndex in 0..<count {
                guard let page = document.page(at: pageIndex), abs(page.bounds(for: .mediaBox).width - 595.3) < 2,
                      abs(page.bounds(for: .mediaBox).height - 841.9) < 2 else { throw Failure(errorDescription: "synthetic PDF page \(id)") }
            }
            let path = "templates/\(id).pdf"
            try pdf.write(to: root.appendingPathComponent(path))
            records.append(.init(id: id, path: path, sha256: SHA256.hash(data: pdf).map { String(format: "%02x", $0) }.joined(), pages: count))
        }
        try JSONEncoder().encode(TransferReferenceManifest(version: 1, templates: records)).write(to: root.appendingPathComponent("manifest.json"))
        let packURL = root.appendingPathComponent("synthetic-references.zip")
        let archive = try Archive(url: packURL, accessMode: .create)
        try archive.addEntry(with: "manifest.json", relativeTo: root, compressionMethod: .deflate)
        for record in records { try archive.addEntry(with: record.path, relativeTo: root, compressionMethod: .deflate) }
        let references = try Data(contentsOf: packURL)
        phase = "reference-pack"
        let packed = try Archive(url: packURL, accessMode: .read)
        for record in records {
            guard let entry = packed[record.path] else { throw Failure(errorDescription: "synthetic archive entry \(record.id)") }
            var data = Data(); _ = try packed.extract(entry) { data.append($0) }
            let hash = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            guard hash == record.sha256, let document = PDFDocument(data: data), document.pageCount == record.pages else {
                throw Failure(errorDescription: "synthetic archive PDF \(record.id)")
            }
        }
        _ = try TransferReferencePack.validateData(references)
        let matchingHashes = Dictionary(uniqueKeysWithValues: records.map { ($0.id, $0.sha256) })
        let matchingPackage = TransferWordDefaults.Package(version: 1, defaults: defaults, referenceHashes: matchingHashes)
        try TransferWordDefaults.verifyReferences(matchingPackage, data: references)
        var boundStore = defaults
        boundStore.wordReferenceHashes = matchingHashes
        let savedStore = try JSONDecoder().decode(TransferLetterCustomizationStore.self, from: JSONEncoder().encode(boundStore))
        try check(savedStore.wordReferenceHashes == matchingHashes, "Word reference binding persistence")
        try TransferWordDefaults.verifyHashes(savedStore.wordReferenceHashes!, data: references)
        do {
            try TransferWordDefaults.verifyReferences(package, data: references)
            throw Failure(errorDescription: "wrong Word reference hashes accepted")
        } catch is Failure { throw Failure(errorDescription: "wrong Word reference hashes accepted") }
        catch { }
        for id in TransferReferencePack.expected.keys {
            guard let template = TransferLetterBody.template(for: id) else { throw Failure(errorDescription: "missing body template \(id)") }
            try TransferLetterBody.validate(template, for: id)
            let changed = template.replacingOccurrences(of: "نرجو تحويل", with: "يرجى تحويل")
            try TransferLetterBody.validate(changed, for: id)
            do {
                try TransferLetterBody.validate(changed.replacingOccurrences(of: "{{المبلغ}}", with: "0"), for: id)
                throw Failure(errorDescription: "missing body amount accepted")
            } catch is Failure { throw Failure(errorDescription: "missing body amount accepted") }
            catch { }
            do {
                try TransferLetterBody.validate(changed + " {{المبلغ}}", for: id)
                throw Failure(errorDescription: "duplicate body amount accepted")
            } catch is Failure { throw Failure(errorDescription: "duplicate body amount accepted") }
            catch { }
        }
        let bodyValues = ["المبلغ": "1200", "المبلغ كتابة": "ألف ومئتان", "حسابنا": "IQ00TEST",
                          "المستفيد": "جهة وهمية", "حساب المستفيد": "IQ11TEST", "الشهر": "تشرين الأول", "السنة": "2026"]
        let renderedBody = try TransferLetterBody.render(id: "tax_social", custom: nil, values: bodyValues)
        try check(renderedBody.contains("1200") && renderedBody.contains("IQ00TEST") && renderedBody.contains("IQ11TEST") && !renderedBody.contains("{{"), "Dynamic letter body fields")
        let changedTemplate = TransferLetterBody.template(for: "tax_social")!.replacingOccurrences(of: "نرجو تحويل", with: "يرجى تحويل")
        let changedBody = try TransferLetterBody.render(id: "tax_social", custom: changedTemplate, values: bodyValues)
        try check(changedBody.contains("يرجى تحويل") && changedBody.contains("1200"), "Customized body rendering")
        var bodyStore = TransferLetterCustomizationStore()
        bodyStore.apply(.init(bodyTemplate: "متن وهمي {{المبلغ}}"), to: "food_tax")
        let restoredBodyStore = try JSONDecoder().decode(TransferLetterCustomizationStore.self, from: JSONEncoder().encode(bodyStore))
        try check(restoredBodyStore.effective(for: "food_tax").bodyTemplate == "متن وهمي {{المبلغ}}", "Body customization persistence")
        let figures = TransferLetterFigures(month: month, foodFund: 3, foodTax: 5, foodCompany: 92,
            healthcare: 20, policeFundDonations: 30, sports: 40, incomeTax: 11, socialProtection: 13,
            contractNominalShare: 17, contractRetirement: 19, payrollCash: 23)
        let custom = TransferLetterCustomization(header: "ترويسة وهمية", committeeFirst: "عضو وهمي أول",
            committeeSecond: "عضو وهمي ثان", committeeThird: "عضو وهمي ثالث", footer: "هامش وهمي",
            fixedNumber: "رقم وهمي", foodContractReference: "عقد وهمي")
        func unifiedCustomization(_ id: String) -> TransferLetterCustomization {
            var value = custom
            var form = CustomTransferTemplate()
            form.title = "كتاب وهمي"; form.header = "ترويسة وهمية"; form.number = "عدد وهمي"
            form.recipient = "مصرف وهمي"; form.subject = "تحويل تجريبي"; form.footer = "هامش وهمي"
            form.body = TransferLetterBody.template(for: id) ?? ""
            form.firstSigner = "صفة وهمية\nاسم وهمي\nمنصب وهمي"; form.secondSigner = ""; form.thirdSigner = ""
            form.bodyAlignment = .center; form.bodySpacing = .compact; form.signatureHorizontalOffset = 10
            form.attachments = "قائمة وهمية إضافية"
            value.letterTemplate = form
            return value
        }
        let mergedAttachmentsText = try TransferWordGroupDraft.letterText(from: unifiedCustomization("rafidain_group"), adaptivePDF: true)
        var attachmentBlocks: [TransferWordDocument.Block] = [.paragraph(.init(text: "المرفقات:\nقائمة إلزامية", halfPoints: 24))]
        TransferWordGroupDraft.appendAttachments(mergedAttachmentsText, to: &attachmentBlocks)
        guard attachmentBlocks.count == 1, case let .paragraph(combinedAttachment) = attachmentBlocks[0] else { throw Failure(errorDescription: "attachments were not combined") }
        try check(combinedAttachment.text.contains("قائمة إلزامية") && combinedAttachment.text.contains("قائمة وهمية إضافية"), "mandatory and custom attachments are preserved")
        let letterText = try TransferWordGroupDraft.letterText(from: custom)
        let fixedRoles = TransferWordGroupDraft.signatureCells(letterText.committee, date: "2026 / 10 /", adaptivePDF: true)
        try check(fixedRoles.count == 3 && fixedRoles[0].contains("معاون مدير تدقيق") &&
                  fixedRoles[1].contains("مدير الحسابات") && fixedRoles[2].contains("قائد شرطة محافظة الأنبار"), "Adaptive PDF signature roles")
        var badPosition = custom
        badPosition.signatureOffset = 61
        do {
            _ = try TransferWordGroupDraft.letterText(from: badPosition)
            throw Failure(errorDescription: "out-of-range signature position accepted")
        } catch is Failure { throw Failure(errorDescription: "out-of-range signature position accepted") }
        catch { }
        let cash = TransferCashList(month: month, employees: [.init(id: 1, name: "موظف وهمي", rank: "رتبة وهمية", workplace: "قسم وهمي", amount: 23)])
        func content(_ url: URL) throws -> String {
            let zip = try Archive(url: url, accessMode: .read)
            guard let entry = zip["word/document.xml"] else { throw Failure(errorDescription: "Word content missing") }
            var data = Data(); _ = try zip.extract(entry) { data.append($0) }
            return String(decoding: data, as: UTF8.self)
        }
        var generated = 0
        for id in ["food_company", "food_tax", "tax_social", "contracts", "cash_salary"] {
            phase = "payroll-\(id)"
            let result = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: [], entity: nil,
                individualPlan: nil, cashList: id == "cash_salary" ? cash : nil, references: references,
                accountLinks: Data(), payerName: "دافع وهمي", payerIBAN: payer, customization: custom,
                sakkalBold: id == "food_tax" ? syntheticFont : nil)
            defer { try? FileManager.default.removeItem(at: result.letter.deletingLastPathComponent()) }
            let xml = try content(result.letter)
            try check(xml.contains(payer) && xml.contains(beneficiary) && xml.contains("عضو وهمي أول"), "Word payroll \(id)")
            if id == "cash_salary" { try check(!xml.contains("موظف وهمي") && xml.contains("المرفقات:") && xml.contains("قائمة أسماء عدد (١)"), "Word cash letter identifies separate list") }
            if id == "food_tax" {
                let embedded = try Archive(url: result.letter, accessMode: .read)
                try check(embedded["word/fonts/sakkal-bold.ttf"] == nil, "Word draft should not embed the old font")
            }
            if id == "food_company" {
                do {
                    try TransferWordVerification.verify(url: result.letter, month: month, amount: 93, payerIBAN: payer,
                        text: letterText, table: nil, beneficiaryAccount: beneficiary)
                    throw Failure(errorDescription: "wrong Word amount accepted")
                } catch is Failure { throw Failure(errorDescription: "wrong Word amount accepted") }
                catch { }
            }
            phase = "adaptive-payroll-\(id)"
            let pdf = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: [], entity: nil,
                individualPlan: nil, cashList: id == "cash_salary" ? cash : nil, references: references,
                accountLinks: Data(), payerName: "دافع وهمي", payerIBAN: payer, customization: custom,
                sakkalBold: syntheticFont, adaptivePDF: true)
            defer { try? FileManager.default.removeItem(at: pdf.letter.deletingLastPathComponent()) }
            try check(PDFDocument(url: pdf.letter)?.pageCount == 1, "Adaptive PDF payroll \(id)")
            phase = "unified-payroll-\(id)"
            let unifiedPDF = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: [], entity: nil,
                individualPlan: nil, cashList: id == "cash_salary" ? cash : nil, references: references,
                accountLinks: Data(), payerName: "دافع وهمي", payerIBAN: payer, customization: unifiedCustomization(id),
                sakkalBold: nil, adaptivePDF: true)
            defer { try? FileManager.default.removeItem(at: unifiedPDF.letter.deletingLastPathComponent()) }
            try check(PDFDocument(url: unifiedPDF.letter)?.pageCount == 1, "Unified template payroll \(id)")
            generated += 1
        }
        var reviewed = TransferLetterFigures(month: month, foodFund: 3, foodTax: 5, foodCompany: 92,
            healthcare: 20, policeFundDonations: 30, sports: 40, incomeTax: 11, socialProtection: 13,
            contractNominalShare: nil, contractRetirement: 19, payrollCash: 23, missingContractNominal: 1,
            availableContractNominalShare: 17)
        do {
            _ = try TransferWordDraftGenerator.make(bookID: "contracts", figures: reviewed, entities: [], entity: nil,
                individualPlan: nil, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: payer, customization: custom, sakkalBold: syntheticFont, adaptivePDF: true)
            throw Failure(errorDescription: "unreviewed missing nominal accepted")
        } catch is Failure { throw Failure(errorDescription: "unreviewed missing nominal accepted") }
        catch { }
        reviewed.contractNominalReviewed = true
        let reviewedPDF = try TransferWordDraftGenerator.make(bookID: "contracts", figures: reviewed, entities: [], entity: nil,
            individualPlan: nil, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
            payerIBAN: payer, customization: custom, sakkalBold: syntheticFont, adaptivePDF: true)
        defer { try? FileManager.default.removeItem(at: reviewedPDF.letter.deletingLastPathComponent()) }
        try check(PDFDocument(url: reviewedPDF.letter)?.pageCount == 1 && reviewed.contractTotal == 36, "reviewed contract PDF")
        do {
            let wrong = try TransferWordDraftGenerator.make(bookID: "food_company", figures: figures, entities: [], entity: nil,
                individualPlan: nil, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: beneficiary, customization: custom)
            try? FileManager.default.removeItem(at: wrong.letter.deletingLastPathComponent())
            throw Failure(errorDescription: "beneficiary accepted as payer")
        } catch is Failure { throw Failure(errorDescription: "beneficiary accepted as payer") }
        catch { }
        let groupA = ReportGroup(kind: .deductions, month: month, title: "جهة وهمية أولى", count: 1, total: 1200, byEntity: false)
        let groupB = ReportGroup(kind: .deductions, month: month, title: "جهة وهمية ثانية", count: 1, total: 1800, byEntity: false)
        let accountA = SavedExecutionAccount(entity: groupA.title, iban: beneficiary, branch: "مصرف الرافدين فرع تجريبي", swift: "TESTIQBAAAA")
        let accountB = SavedExecutionAccount(entity: groupB.title, iban: beneficiary, branch: "مصرف الرشيد فرع تجريبي", swift: "TESTIQBAAAA")
        let groupEntities = [TransferEntity(group: groupA, account: accountA, category: .rafidain, missingAmounts: 0),
                             TransferEntity(group: groupB, account: accountB, category: .otherBanks, missingAmounts: 0)]
        for id in ["rafidain_group", "other_banks_group"] {
            phase = "group-\(id)"
            let result = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: groupEntities, entity: nil,
                individualPlan: nil, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: payer, customization: custom)
            defer { try? FileManager.default.removeItem(at: result.letter.deletingLastPathComponent()) }
            let groupXML = try content(result.letter)
            try check(result.csv != nil && groupXML.contains("المجموع"), "Word group \(id)")
            let category: TransferCategory = id == "rafidain_group" ? .rafidain : .otherBanks
            let expectedPlan = try TransferWordGroupPlan(allEntities: groupEntities, category: category, month: month,
                payer: "دافع وهمي", payerIBAN: payer)
            var wrongTable = expectedPlan.table
            wrongTable.rows[0][4] = "999"
            do {
                try TransferWordVerification.verify(url: result.letter, month: month, amount: expectedPlan.total,
                    payerIBAN: payer, text: letterText, table: wrongTable)
                throw Failure(errorDescription: "wrong Word table amount accepted")
            } catch is Failure { throw Failure(errorDescription: "wrong Word table amount accepted") }
            catch { }
            phase = "adaptive-group-\(id)"
            let pdf = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: groupEntities, entity: nil,
                individualPlan: nil, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: payer, customization: custom, sakkalBold: syntheticFont, adaptivePDF: true)
            defer { try? FileManager.default.removeItem(at: pdf.letter.deletingLastPathComponent()) }
            try check(PDFDocument(url: pdf.letter)?.pageCount == 2 && pdf.csv != nil, "Adaptive PDF group \(id)")
            phase = "unified-group-\(id)"
            let unifiedPDF = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: groupEntities, entity: nil,
                individualPlan: nil, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: payer, customization: unifiedCustomization(id), sakkalBold: nil, adaptivePDF: true)
            defer { try? FileManager.default.removeItem(at: unifiedPDF.letter.deletingLastPathComponent()) }
            try check(PDFDocument(url: unifiedPDF.letter)?.pageCount == 2 && unifiedPDF.csv != nil, "Unified template group \(id)")
            generated += 1
        }
        for (id, category, count) in [("rafidain_group", TransferCategory.rafidain, 29), ("other_banks_group", TransferCategory.otherBanks, 39)] {
            phase = "adaptive-large-group-\(id)"
            let entries = (1...count).map { index -> TransferEntity in
                let title = "جهة تنفيذ وهمية رقم \(index)"
                let group = ReportGroup(kind: .deductions, month: month, title: title, count: 1, total: Double(1000 + index), byEntity: false)
                let bank = category == .rafidain ? "مصرف الرافدين فرع تجريبي" : "مصرف الرشيد فرع تجريبي"
                let account = SavedExecutionAccount(entity: title, iban: beneficiary, branch: bank, swift: "TESTIQBAAAA")
                return TransferEntity(group: group, account: account, category: category, missingAmounts: 0)
            }
            let pdf = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: entries, entity: nil,
                individualPlan: nil, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: payer, customization: custom, sakkalBold: syntheticFont, adaptivePDF: true)
            defer { try? FileManager.default.removeItem(at: pdf.letter.deletingLastPathComponent()) }
            try check(PDFDocument(url: pdf.letter)?.pageCount == 2 && pdf.csv != nil, "Large adaptive group \(id)")
        }
        let referenceLine = TransferWordGroupDraft.referenceText(number: "ح/١/٢٢", date: " / ١٠ / ٢٠٢٦", adaptivePDF: true)
        try check(referenceLine.contains("التاريخ:" + String(repeating: "\u{00A0}", count: 14) + " / ١٠ / ٢٠٢٦"), "Handwriting space after date label")
        let signatureExample = TransferWordGroupDraft.signatureCells(["عضو وهمي أول", "عضو وهمي ثان", "اســم وهمــي ثالث"], date: " / ١٠ / ٢٠٢٦", adaptivePDF: true)
        try check(signatureExample.count == 3 && signatureExample[2].contains("اسم وهمي ثالث") && !signatureExample[2].contains("ـ"), "Fixed signature names")
        try check(ReportSheet.easternDigits("ح/1/22/ / 10 / 2026") == "ح/١/٢٢/ / ١٠ / ٢٠٢٦", "Indic letter dates")
        let tooMany = (1...100).map { index -> TransferEntity in
            let title = "جهة وهمية \(index)"
            let group = ReportGroup(kind: .deductions, month: month, title: title, count: 1, total: 100, byEntity: false)
            let account = SavedExecutionAccount(entity: title, iban: beneficiary, branch: "مصرف الرافدين فرع وهمي", swift: "TESTIQBAAAA")
            return TransferEntity(group: group, account: account, category: .rafidain, missingAmounts: 0)
        }
        do {
            _ = try TransferWordGroupPlan(allEntities: tooMany, category: .rafidain, month: month, payer: "دافع وهمي", payerIBAN: payer)
            throw Failure(errorDescription: "overfull Word group accepted")
        } catch is Failure { throw Failure(errorDescription: "overfull Word group accepted") }
        catch { }
        for (id, kind) in [("ramadi", TransferIndividualPlan.Kind.ramadi), ("housing", .housing), ("water", .water), ("rafidain_anbar", .rafidainAnbar)] {
            phase = "entity-\(id)"
            let title = id == "rafidain_anbar" ? "مصرف الرافدين/فرع الانبار" : "جهة وهمية \(id)"
            let group = ReportGroup(kind: .deductions, month: month, title: title, count: 1, total: 1200, byEntity: false)
            let row = ReportRow(id: 1, name: "موظف وهمي", rank: "رتبة وهمية", workplace: "قسم وهمي", amount: 1200, note: "", entity: title)
            let plan = try TransferIndividualPlan(kind: kind, group: group, rows: [row], missingAmounts: 0)
            let account: SavedExecutionAccount? = id == "rafidain_anbar" ? nil : SavedExecutionAccount(entity: title, iban: beneficiary, branch: "مصرف وهمي / فرع وهمي", swift: "TESTIQBAAAA")
            let entity = TransferEntity(group: group, account: account, category: id == "rafidain_anbar" ? .anbar : .otherBanks, missingAmounts: 0)
            if id == "ramadi" || id == "rafidain_anbar" {
                let matched = try TransferLetterEntityLinks.uniqueReferenceMatch(bookID: id, candidates: [entity],
                    references: references, payerIBAN: payer, accountLinks: Data())
                try check(matched?.id == entity.id, "unique letter reference beneficiary \(id)")
                let ambiguous = try TransferLetterEntityLinks.uniqueReferenceMatch(bookID: id, candidates: [entity, entity],
                    references: references, payerIBAN: payer, accountLinks: Data())
                try check(ambiguous == nil, "ambiguous reference beneficiary \(id)")
            }
            let result = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: [entity], entity: entity,
                individualPlan: plan, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: payer, customization: custom)
            defer { try? FileManager.default.removeItem(at: result.letter.deletingLastPathComponent()) }
            let xml = try content(result.letter)
            try check(xml.contains(payer) && (id != "water" || xml.contains("موظف وهمي")), "Word entity \(id)")
            if id == "water" { try check(!xml.contains("المرفقات:"), "Water letter has no list attachment") }
            else { try check(xml.contains("المرفقات:") && xml.contains("قائمة أسماء عدد (١)"), "Individual letter identifies separate list \(id)") }
            phase = "adaptive-entity-\(id)"
            let pdf = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: [entity], entity: entity,
                individualPlan: plan, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: payer, customization: custom, sakkalBold: syntheticFont, adaptivePDF: true)
            defer { try? FileManager.default.removeItem(at: pdf.letter.deletingLastPathComponent()) }
            try check(PDFDocument(url: pdf.letter)?.pageCount == 1, "Adaptive PDF entity \(id)")
            phase = "unified-entity-\(id)"
            let unifiedPDF = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: [entity], entity: entity,
                individualPlan: plan, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: payer, customization: unifiedCustomization(id), sakkalBold: nil, adaptivePDF: true)
            defer { try? FileManager.default.removeItem(at: unifiedPDF.letter.deletingLastPathComponent()) }
            try check(PDFDocument(url: unifiedPDF.letter)?.pageCount == 1, "Unified template entity \(id)")
            generated += 1
        }
        var adjusted = custom
        adjusted.signatureOffset = 15
        let adjustedPDF = try TransferWordDraftGenerator.make(bookID: "tax_social", figures: figures, entities: [], entity: nil,
            individualPlan: nil, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
            payerIBAN: payer, customization: adjusted, sakkalBold: syntheticFont, adaptivePDF: true)
        defer { try? FileManager.default.removeItem(at: adjustedPDF.letter.deletingLastPathComponent()) }
        try check(PDFDocument(url: adjustedPDF.letter)?.pageCount == 1, "adjusted committee PDF")
        try check(generated == 11, "Word draft count")
        phase = "adaptive-long-text"
        let longPDF = documents.appendingPathComponent("adaptive-long-test-\(UUID().uuidString).pdf")
        defer { try? FileManager.default.removeItem(at: longPDF) }
        let longBody = Array(repeating: "نص تجريبي طويل للتأكد من التفاف الأسطر وانتقال المحتوى.", count: 160).joined(separator: " ")
        let fixedSignatures = TransferWordDocument.Table(headers: [], rows: [TransferWordGroupDraft.signatureCells(["عضو أول", "عضو ثان", "عضو ثالث"], date: " / ١٠ / ٢٠٢٦", adaptivePDF: true)], font: "Times New Roman", halfPoints: 32, columnWeights: [1, 1, 1], showBorders: false)
        try TransferAdaptivePDF.write(blocks: [
            .paragraph(.init(text: "ترويسة وهمية", font: "Times New Roman", halfPoints: 32, bold: true)),
            .paragraph(.init(text: "العدد: تجريبي\nالتاريخ: 2026 / 10 /", font: "Sakkal Majalla", halfPoints: 36, bold: true)),
            .paragraph(.init(text: "إلى / جهة وهمية")),
            .paragraph(.init(text: "م / اختبار", centered: true)),
            .paragraph(.init(text: longBody)), .pageBreak, .table(fixedSignatures),
            .paragraph(.init(text: "هامش وهمي", halfPoints: 24))
        ], destination: longPDF, sakkalBold: syntheticFont, group: false)
        try check((PDFDocument(url: longPDF)?.pageCount ?? 0) > 1, "Adaptive PDF long text pagination")
        phase = "custom-body-layouts"
        for alignment: TransferWordDocument.Paragraph.Alignment in [.right, .left, .center, .justified] {
            let url = documents.appendingPathComponent("custom-layout-\(UUID().uuidString).pdf")
            defer { try? FileManager.default.removeItem(at: url) }
            try TransferAdaptivePDF.write(blocks: [
                .paragraph(.init(text: "ترويسة وهمية", halfPoints: 32, bold: true)),
                .paragraph(.init(text: "العدد: تجريبي\nالتاريخ: ٢٠٢٦ / ١٠ /", halfPoints: 30, bold: true)),
                .paragraph(.init(text: "إلى / جهة وهمية", centered: true)),
                .paragraph(.init(text: "م / تجربة", centered: true)),
                .paragraph(.init(text: "المبلغ التجريبي ١,٢٣٤,٥٦٧ ديناراً بتاريخ ٢٠٢٦/١٠/٠٥.", halfPoints: 28, bold: true, alignment: alignment, lineSpacing: 8)),
                .table(fixedSignatures), .paragraph(.init(text: "هامش وهمي", halfPoints: 24))
            ], destination: url, sakkalBold: nil, group: false, signatureOffset: -10, signatureHorizontalOffset: 15)
            try check(PDFDocument(url: url)?.pageCount == 1, "custom body PDF alignment")
            try TransferAdaptivePDF.verify(url, required: ["١,٢٣٤,٥٦٧"])
        }
        phase = "kashida-pdf-review"
        let reviewBody = "نرجو تحويل مبلغ قدره (١,٢٣٤,٥٦٧) ديناراً من حسابنا إلى حساب الجهة المستفيدة وتزويدنا بالإشعار اللازم بعد تنفيذ عملية التحويل مع فائق الاحترام والتقدير. رقم الحساب IQ06TEST123000000000001 ونؤكد صحة جميع المبالغ الواردة في هذا الكتاب التجريبي."
        var reviewPages: [(String, UIImage)] = []
        for (label, mode): (String, CustomTransferTemplate.Kashida) in [("JUSTIFY", .none), ("SMALL", .low), ("MEDIUM", .medium), ("LARGE", .high)] {
            let url = documents.appendingPathComponent("kashida-review-\(label).pdf")
            try? FileManager.default.removeItem(at: url)
            defer { try? FileManager.default.removeItem(at: url) }
            try TransferAdaptivePDF.write(blocks: [
                .paragraph(.init(text: "ترويسة وهمية", halfPoints: 32, bold: true)),
                .paragraph(.init(text: "العدد: تجريبي\nالتاريخ: ٢٠٢٦ / ١٠ /", halfPoints: 30, bold: true)),
                .paragraph(.init(text: "إلى / جهة وهمية", centered: true)),
                .paragraph(.init(text: "م / تجربة", centered: true)),
                .paragraph(.init(text: reviewBody, halfPoints: 28, alignment: .justified, kashida: mode)),
                .table(fixedSignatures), .paragraph(.init(text: "هامش وهمي", halfPoints: 24))
            ], destination: url, sakkalBold: nil, group: false)
            try TransferAdaptivePDF.verify(url, required: ["١,٢٣٤,٥٦٧", "IQ06TEST123000000000001"])
            guard let pdf = PDFDocument(url: url), pdf.pageCount == 1, let page = pdf.page(at: 0) else {
                throw Failure(errorDescription: "kashida review PDF missing \(label)")
            }
            reviewPages.append((label, page.thumbnail(of: CGSize(width: 595, height: 842), for: .mediaBox)))
        }
        let reviewSize = CGSize(width: 595, height: 900)
        let reviewImage = UIGraphicsImageRenderer(size: reviewSize).image { context in
            UIColor.white.setFill(); context.fill(CGRect(origin: .zero, size: reviewSize))
            for (index, item) in reviewPages.enumerated() {
                let top = CGFloat(index * 225)
                (item.0 as NSString).draw(at: CGPoint(x: 12, y: top + 3), withAttributes: [.font: UIFont.boldSystemFont(ofSize: 15)])
                context.cgContext.saveGState()
                context.cgContext.clip(to: CGRect(x: 0, y: top + 25, width: 595, height: 195))
                item.1.draw(in: CGRect(x: 0, y: top + 25 - 220, width: 595, height: 842))
                context.cgContext.restoreGState()
            }
        }
        guard let reviewPNG = reviewImage.pngData() else { throw Failure(errorDescription: "kashida review image missing") }
        try reviewPNG.write(to: documents.appendingPathComponent("kashida-review.png"), options: .atomic)
        phase = "custom-single-signature"
        let oneSignatureCells = try CustomTransferTemplate.signatureCells(["وظيفة\nSOLO SIGNER\nمنصب", "", ""], date: " / ١٠ / ٢٠٢٦")
        let oneSignature = TransferWordDocument.Table(headers: [], rows: [oneSignatureCells], font: "Times New Roman", halfPoints: 32, columnWeights: [1, 1, 1], showBorders: false)
        let singlePDF = documents.appendingPathComponent("single-signature-\(UUID().uuidString).pdf")
        defer { try? FileManager.default.removeItem(at: singlePDF) }
        try TransferAdaptivePDF.write(blocks: [
            .paragraph(.init(text: "ترويسة وهمية", halfPoints: 32, bold: true)),
            .paragraph(.init(text: "العدد: تجريبي\nالتاريخ: ٢٠٢٦ / ١٠ /", halfPoints: 30, bold: true)),
            .paragraph(.init(text: "إلى / جهة وهمية", centered: true)),
            .paragraph(.init(text: "م / تجربة", centered: true)),
            .paragraph(.init(text: "متن كتاب وهمي BODYTOKEN", halfPoints: 28)),
            .paragraph(.init(text: "المرفقات: ATTACHMENTTOKEN", halfPoints: 22)),
            .table(oneSignature), .paragraph(.init(text: "هامش وهمي", halfPoints: 24))
        ], destination: singlePDF, sakkalBold: nil, group: false)
        guard let singleDocument = PDFDocument(url: singlePDF), let singlePage = singleDocument.page(at: 0),
              let signatureSelection = singleDocument.findString("SOLO SIGNER", withOptions: .caseInsensitive).first,
              let bodySelection = singleDocument.findString("BODYTOKEN", withOptions: .caseInsensitive).first,
              let attachmentSelection = singleDocument.findString("ATTACHMENTTOKEN", withOptions: .caseInsensitive).first else {
            throw Failure(errorDescription: "custom letter body, attachment or signature missing from PDF")
        }
        try check(signatureSelection.bounds(for: singlePage).midX < singlePage.bounds(for: .mediaBox).midX, "single signature is on the left of the PDF")
        try check(bodySelection.bounds(for: singlePage).midY > attachmentSelection.bounds(for: singlePage).midY &&
                  attachmentSelection.bounds(for: singlePage).midY > signatureSelection.bounds(for: singlePage).midY, "attachment appears after body and before signature")
        phase = "treasury-letter-drafts"
        let fakeRow = ReportRow(id: 42, name: "اسم وهمي", rank: "رتبة وهمية", workplace: "", amount: 1000, note: "")
        let schoolRow = ReportRow(id: 43, name: "اسم آخر وهمي", rank: "رتبة وهمية", workplace: "", amount: 600, note: "")
        for kind: TreasuryLetterView.Kind in [.guarantee, .schools] {
            let label = kind == .guarantee ? "guarantee" : "schools"
            phase = "treasury-\(label)-plan"
            let draft = try TreasuryLetterDraft.make(kind: kind, monthName: month, payerIBAN: payer, beneficiaryIBAN: beneficiary,
                header: "ترويسة وهمية", number: "عدد وهمي", firstSigner: "عضو أول", secondSigner: "عضو ثان", thirdSigner: "عضو ثالث",
                guaranteeReason: "تضمين وهمي", firstRows: [fakeRow], secondRows: kind == .schools ? [schoolRow] : [],
                notes: [42: "إضبارة وهمية"])
            guard case let .table(table) = draft.blocks[5] else { throw Failure(errorDescription: "missing treasury table") }
            if kind == .schools {
                try check(table.rows.count == 3 && table.rows[0][1] == "١,٠٠٠" && table.rows[1][1] == "٦٠٠" && table.rows[2][1] == "١,٦٠٠", "school amounts and total")
            } else {
                try check(table.rows.count == 2 && table.rows[0][2] == "اسم وهمي" && table.rows[0][5] == "إضبارة وهمية", "guarantee name and manual note")
            }
            let url = documents.appendingPathComponent("treasury-test-\(UUID().uuidString).pdf")
            defer { try? FileManager.default.removeItem(at: url) }
            phase = "treasury-\(label)-write"
            try TransferAdaptivePDF.write(blocks: draft.blocks, destination: url, sakkalBold: nil, group: false, verifyNumericTableCells: true)
            try check(PDFDocument(url: url)?.pageCount == 1, "treasury PDF fits one page")
            for index in draft.required.indices {
                phase = "treasury-\(label)-verify-\(index)"
                try TransferAdaptivePDF.verify(url, required: [draft.required[index]])
            }
            phase = "treasury-\(label)-verify-all"
            do { try TransferAdaptivePDF.verify(url, required: draft.required) }
            catch {
                let sample = PDFDocument(url: url)?.page(at: 0)?.string ?? ""
                throw Failure(errorDescription: "synthetic PDF extraction: \(sample)")
            }
            phase = "unified-treasury-\(label)"
            let id = kind == .guarantee ? "treasury_guarantee" : "treasury_schools"
            let unified = try TreasuryLetterDraft.make(kind: kind, monthName: month, payerIBAN: payer, beneficiaryIBAN: beneficiary,
                header: "", number: "", firstSigner: "", secondSigner: "", thirdSigner: "",
                guaranteeReason: "تضمين وهمي", firstRows: [fakeRow], secondRows: kind == .schools ? [schoolRow] : [],
                notes: [42: "إضبارة وهمية"], template: unifiedCustomization(id).letterTemplate)
            let unifiedURL = documents.appendingPathComponent("unified-treasury-\(UUID().uuidString).pdf")
            defer { try? FileManager.default.removeItem(at: unifiedURL) }
            try TransferAdaptivePDF.write(blocks: unified.blocks, destination: unifiedURL, sakkalBold: nil, group: false, verifyNumericTableCells: true, signatureHorizontalOffset: 10)
            try TransferAdaptivePDF.verify(unifiedURL, required: unified.required)
            try check(PDFDocument(url: unifiedURL)?.pageCount == 1, "Unified template treasury \(label)")
        }
        // معاينة منشأة بالكامل بأسماء وهمية، دون استخدام اسم جهة حقيقية في الصور.
        let fixture = documents.appendingPathComponent("word-preview-fixture.docx")
        try? FileManager.default.removeItem(at: fixture)
        let signatures = TransferWordDocument.Table(headers: [], rows: [["عضو وهمي أول\n2026 / 10 /", "عضو وهمي ثان\n2026 / 10 /", "عضو وهمي ثالث\n2026 / 10 /"]], font: "Times New Roman", halfPoints: 32, columnWeights: [1, 1, 1], showBorders: false)
        try TransferWordDocument.write(blocks: [
            .paragraph(.init(text: "ترويسة وهمية", font: "Sakkal Majalla", halfPoints: 36, bold: true)),
            .paragraph(.init(text: "العدد: رقم وهمي\nالتاريخ: 2026 / 10 /", halfPoints: 30, bold: true)),
            .paragraph(.init(text: "إلى / مصرف وهمي / فرع وهمي", font: "Arial", halfPoints: 32, bold: true)),
            .paragraph(.init(text: "م / تحويل مبلغ", font: "Arial", halfPoints: 32, bold: true, centered: true)),
            .paragraph(.init(text: "نرجو تحويل مبلغ تجريبي إلى حساب جهة وهمية عن استقطاعات الشهر، وتزويدنا بالإشعار اللازم بذلك.", halfPoints: 28)),
            .table(signatures), .paragraph(.init(text: "هامش وهمي", halfPoints: 24))
        ], destination: fixture)
        return "wordTransfers=ok drafts=11 synthetic=1"
    }
}
#endif

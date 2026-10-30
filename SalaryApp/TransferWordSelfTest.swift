#if targetEnvironment(simulator)
import Foundation
import UIKit
import PDFKit
import ZIPFoundation
import CryptoKit

/// اختبارات مستقلة لمسار Word، لا تستخدم قوالب أو حسابات حقيقية.
@MainActor enum TransferWordSelfTest {
    struct Failure: LocalizedError { let errorDescription: String? }
    static func run() throws -> String {
        func check(_ ok: Bool, _ message: String) throws { if !ok { throw Failure(errorDescription: message) } }
        let payer = "IQ06TEST123000000000001", beneficiary = "IQ76TEST123000000000002"
        let local = String(beneficiary.suffix(15)), month = "تشرين الاول 2026"
        try check(TransferRules.validIBAN(payer) && TransferRules.validIBAN(beneficiary), "synthetic accounts")
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("word-selftest-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root.appendingPathComponent("templates"), withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let renderer = UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 595.3, height: 841.9))
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
            let pdf = renderer.pdfData { context in
                context.beginPage()
                PDFText.draw(payer, in: payerRect(id), font: font, align: .center)
                if id != "rafidain_anbar" && id != "rafidain_group" && id != "other_banks_group" {
                    PDFText.draw(id == "water" || id == "housing" ? local : beneficiary, in: targetRect(id), font: font, align: .center)
                }
                if count == 2 { context.beginPage() }
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
        _ = try TransferReferencePack.validateData(references)
        let figures = TransferLetterFigures(month: month, foodFund: 3, foodTax: 5, foodCompany: 92,
            healthcare: 20, policeFundDonations: 30, sports: 40, incomeTax: 11, socialProtection: 13,
            contractNominalShare: 17, contractRetirement: 19, payrollCash: 23)
        let custom = TransferLetterCustomization(header: "ترويسة وهمية", committeeFirst: "عضو وهمي أول",
            committeeSecond: "عضو وهمي ثان", committeeThird: "عضو وهمي ثالث", footer: "هامش وهمي",
            fixedNumber: "رقم وهمي", foodContractReference: "عقد وهمي")
        let cash = TransferCashList(month: month, employees: [.init(id: 1, name: "موظف وهمي", rank: "رتبة وهمية", workplace: "قسم وهمي", amount: 23)])
        func content(_ url: URL) throws -> String {
            let zip = try Archive(url: url, accessMode: .read)
            guard let entry = zip["word/document.xml"] else { throw Failure(errorDescription: "Word content missing") }
            var data = Data(); _ = try zip.extract(entry) { data.append($0) }
            return String(decoding: data, as: UTF8.self)
        }
        var generated = 0
        for id in ["food_company", "food_tax", "tax_social", "contracts", "cash_salary"] {
            let result = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: [], entity: nil,
                individualPlan: nil, cashList: id == "cash_salary" ? cash : nil, references: references,
                accountLinks: Data(), payerName: "دافع وهمي", payerIBAN: payer, customization: custom)
            defer { try? FileManager.default.removeItem(at: result.letter.deletingLastPathComponent()) }
            let xml = try content(result.letter)
            try check(xml.contains(payer) && xml.contains(beneficiary) && xml.contains("عضو وهمي أول"), "Word payroll \(id)")
            if id == "cash_salary" { try check(xml.contains("موظف وهمي"), "Word cash list") }
            generated += 1
        }
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
            let result = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: groupEntities, entity: nil,
                individualPlan: nil, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: payer, customization: custom)
            defer { try? FileManager.default.removeItem(at: result.letter.deletingLastPathComponent()) }
            let groupXML = try content(result.letter)
            try check(result.csv != nil && groupXML.contains("المجموع"), "Word group \(id)")
            generated += 1
        }
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
            let title = id == "rafidain_anbar" ? "مصرف الرافدين/فرع الانبار" : "جهة وهمية \(id)"
            let group = ReportGroup(kind: .deductions, month: month, title: title, count: 1, total: 1200, byEntity: false)
            let row = ReportRow(id: 1, name: "موظف وهمي", rank: "رتبة وهمية", workplace: "قسم وهمي", amount: 1200, note: "", entity: title)
            let plan = try TransferIndividualPlan(kind: kind, group: group, rows: [row], missingAmounts: 0)
            let account: SavedExecutionAccount? = id == "rafidain_anbar" ? nil : SavedExecutionAccount(entity: title, iban: beneficiary, branch: "مصرف وهمي / فرع وهمي", swift: "TESTIQBAAAA")
            let entity = TransferEntity(group: group, account: account, category: id == "rafidain_anbar" ? .anbar : .otherBanks, missingAmounts: 0)
            let result = try TransferWordDraftGenerator.make(bookID: id, figures: figures, entities: [entity], entity: entity,
                individualPlan: plan, cashList: nil, references: references, accountLinks: Data(), payerName: "دافع وهمي",
                payerIBAN: payer, customization: custom)
            defer { try? FileManager.default.removeItem(at: result.letter.deletingLastPathComponent()) }
            let xml = try content(result.letter)
            try check(xml.contains(payer) && (id != "water" || xml.contains("موظف وهمي")), "Word entity \(id)")
            generated += 1
        }
        try check(generated == 11, "Word draft count")
        return "wordTransfers=ok drafts=11 synthetic=1"
    }
}
#endif

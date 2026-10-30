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
        do {
            _ = try TransferHeaderFont.make(data: Data("invalid".utf8), style: .bold, size: 18)
            throw Failure(errorDescription: "invalid header font accepted")
        } catch is Failure { throw Failure(errorDescription: "invalid header font accepted") }
        catch { }
        var customization = TransferLetterCustomizationStore()
        customization.applyToAll(.init(header: "ترويسة عامة", footer: "هامش عام"))
        customization.apply(.init(header: "ترويسة خاصة", committeeFirst: "عضو وهمي"), to: "food_company")
        try check(customization.effective(for: "food_company").header == "ترويسة خاصة" && customization.effective(for: "food_company").footer == "هامش عام", "individual customization inheritance")
        try check(customization.effective(for: "cash_salary").header == "ترويسة عامة", "common customization inheritance")
        customization.applyToAll(.init(header: "ترويسة جديدة"))
        try check(customization.effective(for: "food_company").header == "ترويسة جديدة" && customization.effective(for: "food_company").committeeFirst == "عضو وهمي", "apply to all preserves unrelated fields")
        let savedCustomization = try JSONEncoder().encode(customization)
        try check(try JSONDecoder().decode(TransferLetterCustomizationStore.self, from: savedCustomization) == customization, "customization roundtrip")
        let wordValues = try TransferWordValues.make(month: "تشرين الاول 2026", amount: 123_456,
            custom: .init(header: "ترويسة وهمية"), requested: ["MONTH_NUMBER", "YEAR", "SIGNATURE_DATE", "AMOUNT", "AMOUNT_WORDS", "HEADER"])
        try check(wordValues["MONTH_NUMBER"] == "10", "Word month number")
        try check(wordValues["YEAR"] == "2026", "Word year")
        try check(wordValues["AMOUNT"] == "123,456", "Word numeric amount grouping")
        try check(wordValues["SIGNATURE_DATE"] == " / 10 / 2026" && wordValues["HEADER"] == "ترويسة وهمية", "Word signature date and customization")
        try check(wordValues["AMOUNT_WORDS"] == ArabicAmountWords.dinars(123_456), "Word amount in words")
        let generatedWord = FileManager.default.temporaryDirectory.appendingPathComponent("generated-word-\(UUID().uuidString).docx")
        defer { try? FileManager.default.removeItem(at: generatedWord) }
        try TransferWordDocument.write(blocks: [
            .paragraph(.init(text: "كتاب وهمي\nسطر آخر", halfPoints: 28, bold: true)),
            .table(.init(headers: ["الجهة", "المبلغ"], rows: [["جهة وهمية", "123,456"]], columnWeights: [3, 1])),
            .table(.init(headers: [], rows: [["عضو أول", "عضو ثان", "عضو ثالث"]], showBorders: false))
        ], destination: generatedWord)
        let generatedArchive = try Archive(url: generatedWord, accessMode: .read)
        guard let generatedBody = generatedArchive["word/document.xml"], generatedArchive["[Content_Types].xml"] != nil,
              generatedArchive["_rels/.rels"] != nil else { throw Failure(errorDescription: "generated Word package incomplete") }
        var generatedXML = Data()
        _ = try generatedArchive.extract(generatedBody) { generatedXML.append($0) }
        let generatedText = String(decoding: generatedXML, as: UTF8.self)
        try check(generatedText.contains("جهة وهمية") && generatedText.contains("123,456") &&
                  generatedText.contains("<w:br/>") && generatedText.contains("<w:tblBorders>") &&
                  generatedText.contains("<w:gridCol w:w=\"7654\"/>") && generatedText.contains("w:val=\"nil\""), "generated Word contents and table")
        let wordFigures = TransferLetterFigures(month: "تشرين الاول 2026", foodFund: 3, foodTax: 5,
            foodCompany: 92, healthcare: 20, policeFundDonations: 30, sports: 40,
            incomeTax: 11, socialProtection: 13, contractNominalShare: 17,
            contractRetirement: 19, payrollCash: 23)
        try check(try TransferWordValues.payrollAmount(bookID: "food_company", figures: wordFigures) == 92, "Word company source")
        try check(try TransferWordValues.payrollAmount(bookID: "food_tax", figures: wordFigures) == 5, "Word food tax source")
        try check(try TransferWordValues.payrollAmount(bookID: "tax_social", figures: wordFigures) == 24, "Word treasury source")
        try check(try TransferWordValues.payrollAmount(bookID: "contracts", figures: wordFigures) == 36, "Word contract source")
        try check(try TransferWordValues.payrollAmount(bookID: "cash_salary", figures: wordFigures) == 23, "Word cash source")
        let treasuryRows = try TransferWordTables.treasury(wordFigures).rows
        try check(treasuryRows.count == 3 && treasuryRows[0][2] == "13" && treasuryRows[1][2] == "11" && treasuryRows[2][2] == "24", "Word tax and social protection table")
        let contractRows = try TransferWordTables.contracts(wordFigures).rows
        try check(contractRows.count == 1 && contractRows[0] == ["19", "17", "36"], "Word contract amounts")
        let wordCashList = TransferCashList(month: wordFigures.month, employees: [
            .init(id: 1, name: "موظف وهمي", rank: "رتبة وهمية", workplace: "قسم وهمي", amount: 23)
        ])
        let cashRows = try TransferWordTables.cash(wordCashList, figures: wordFigures).rows
        try check(cashRows.count == 2 && cashRows[0][3] == "23" && cashRows[1][3] == "23", "Word cash list and total")
        do {
            let wrongCash = TransferCashList(month: wordFigures.month, employees: [
                .init(id: 1, name: "موظف وهمي", rank: "رتبة وهمية", workplace: "قسم وهمي", amount: 22)
            ])
            _ = try TransferWordTables.cash(wrongCash, figures: wordFigures)
            throw Failure(errorDescription: "inconsistent Word cash list accepted")
        } catch is Failure { throw Failure(errorDescription: "inconsistent Word cash list accepted") }
        catch { }
        var incompleteContracts = wordFigures
        incompleteContracts.missingContractNominal = 1
        do {
            _ = try TransferWordValues.payrollAmount(bookID: "contracts", figures: incompleteContracts)
            throw Failure(errorDescription: "incomplete contract amount accepted")
        } catch is Failure { throw Failure(errorDescription: "incomplete contract amount accepted") }
        catch { }
        do {
            _ = try TransferWordValues.payrollAmount(bookID: "water", figures: wordFigures)
            throw Failure(errorDescription: "entity amount guessed from payroll")
        } catch is Failure { throw Failure(errorDescription: "entity amount guessed from payroll") }
        catch { }
        do {
            _ = try TransferWordValues.make(month: "تشرين الاول 2026", amount: -1, custom: .init(), requested: ["AMOUNT"])
            throw Failure(errorDescription: "invalid Word amount accepted")
        } catch is Failure { throw Failure(errorDescription: "invalid Word amount accepted") }
        catch { }
        do {
            _ = try TransferWordValues.make(month: "تشرين الاول 2026", amount: 100, custom: .init(), requested: ["HEADER"])
            throw Failure(errorDescription: "missing Word customization accepted")
        } catch is Failure { throw Failure(errorDescription: "missing Word customization accepted") }
        catch { }
        let iban = "IQ06TEST123000000000001"
        let fakeBeneficiary = "IQ76TEST123000000000002"
        try check(TransferRules.validIBAN(fakeBeneficiary), "synthetic beneficiary IBAN")
        let fakeReference = "حساب الدافع \(iban) حساب المستفيد \(fakeBeneficiary)"
        try check(try TransferWordReferenceAccounts.beneficiary(in: fakeReference, payerIBAN: iban) == fakeBeneficiary, "Word beneficiary reference account")
        do {
            _ = try TransferWordReferenceAccounts.beneficiary(in: "\(iban) \(fakeBeneficiary) \(fakeBeneficiary)", payerIBAN: iban)
            throw Failure(errorDescription: "ambiguous reference beneficiary accepted")
        } catch is Failure { throw Failure(errorDescription: "ambiguous reference beneficiary accepted") }
        catch { }
        let account = SavedExecutionAccount(entity: "جهة وهمية", iban: iban, branch: "مصرف الرافدين فرع تجريبي", swift: "TESTIQBAAAA")
        let anbar = SavedExecutionAccount(entity: "جهة ثانية", iban: iban, branch: "مصرف الرافدين فرع الانبار", swift: "TESTIQBAAAA")
        let other = SavedExecutionAccount(entity: "جهة ثالثة", iban: iban, branch: "مصرف الرشيد فرع تجريبي", swift: "TESTIQBAAAA")
        try check(TransferRules.classify(account) == .rafidain && TransferRules.classify(anbar) == .anbar && TransferRules.classify(other) == .otherBanks, "classification")
        let ambiguous = SavedExecutionAccount(entity: "جهة وهمية", iban: iban, branch: "مصرف الرافدين محافظة الانبار فرع آخر", swift: "TESTIQBAAAA")
        try check(TransferRules.classify(ambiguous) == .unresolved, "ambiguous Anbar branch")
        let newBank = SavedExecutionAccount(entity: "جهة وهمية", iban: iban, branch: "مصرف تجريبي فرع تجريبي", swift: "TESTIQBAAAA")
        let incompleteBank = SavedExecutionAccount(entity: "جهة وهمية", iban: iban, branch: "فرع تجريبي", swift: "TESTIQBAAAA")
        try check(TransferRules.classify(newBank) == .otherBanks && TransferRules.classify(incompleteBank) == .unresolved, "other explicit banks and unclear branches")
        let sampleRows = (0..<44).map { i in ["\(i + 1)", "جهة وهمية", "IQ00TEST000000000000000", "فرع تجريبي", "100000"] }
        let widths: [CGFloat] = [30, 145, 150, 105, 90]
        let singlePage = try TransferSinglePageFit.fit(rows: sampleRows, widths: widths, availableHeight: 700, fontName: ReportFont.regular(10).fontName)
        try check(singlePage.rowHeights.count == 44 && singlePage.rowHeights.reduce(0, +) <= 700, "single-page group layout")
        let compactPage = try TransferSinglePageFit.fit(rows: sampleRows, widths: widths, availableHeight: 680,
                                                       fontName: ReportFont.bold(10).fontName, maximumSize: 10.8, minimumSize: 7, verticalPadding: 2)
        try check(compactPage.rowHeights.count == 44 && compactPage.rowHeights.reduce(0, +) <= 680, "reference-style group layout")
        do {
            _ = try TransferSinglePageFit.fit(rows: Array(repeating: sampleRows[0], count: 500), widths: widths, availableHeight: 700, fontName: ReportFont.regular(10).fontName)
            throw Failure(errorDescription: "overflowing single-page table accepted")
        } catch is Failure { throw Failure(errorDescription: "overflowing single-page table accepted") }
        catch { }
        try check(TransferRules.classify(nil) == .unresolved && TransferRules.validIBAN(iban) && !TransferRules.validIBAN("IQ00"), "invalid accounts")
        try check(TransferRules.localAccountDigits(iban) == String(iban.suffix(15)) && TransferRules.localAccountDigits("IQ00") == nil, "local account from Iraqi IBAN")
        let localOnly = SavedExecutionAccount(entity: "صندوق الاسكان وهمي", iban: "", branch: "الرافدين / فرع الانبار", swift: "", localAccount: "123456789012345")
        let conflict = SavedExecutionAccount(entity: "صندوق الاسكان وهمي", iban: iban, branch: "الرافدين / فرع الانبار", swift: "", localAccount: "123456789012345")
        try check(TransferRules.individualLocalAccount(localOnly) == "123456789012345" && TransferRules.individualLocalAccount(conflict) == nil, "local account and conflicting IBAN")
        let accountRoundtrip = try ExecutionAccountStore.decode(ExecutionAccountStore.encode([localOnly]))
        try check(accountRoundtrip.first?.localAccount == localOnly.localAccount && accountRoundtrip.first?.iban == "", "local account storage roundtrip")
        let oldAccount = Data("[{\"entity\":\"جهة وهمية\",\"iban\":\"\",\"branch\":\"\",\"swift\":\"\"}]".utf8)
        try check(try ExecutionAccountStore.decode(oldAccount).first?.localAccount == nil, "older account without local field")
        let backupAccount = BackupFormat.AccountRecord(entity: localOnly.entity, iban: localOnly.iban, branch: localOnly.branch, swift: localOnly.swift, localAccount: localOnly.localAccount)
        try check(try JSONDecoder().decode(BackupFormat.AccountRecord.self, from: JSONEncoder().encode(backupAccount)).localAccount == localOnly.localAccount, "local account backup roundtrip")
        let group = ReportGroup(kind: .deductions, month: "تشرين الاول 2026", title: "جهة وهمية", count: 1, total: 1200, byEntity: false)
        let item = TransferEntity(group: group, account: account, category: .rafidain, missingAmounts: 0)
        let bankTitle = "مصرف الرافدين/فرع الانبار"
        let bankGroup = ReportGroup(kind: .deductions, month: group.month, title: bankTitle, count: 1, total: 1200, byEntity: false)
        let directBank = TransferEntity(group: bankGroup, account: nil, category: .anbar, missingAmounts: 0)
        try check(directBank.isDirectAnbarBank && TransferRules.issues(directBank).isEmpty, "Anbar bank source uses individual letter without beneficiary account")
        try check(TransferAccountLinks.allowsDirectAnbar(title: bankTitle, data: Data()), "direct Anbar source is permitted")
        try check(!TransferAccountLinks.allowsDirectAnbar(title: "مصرف الرافدين فرع آخر", data: Data()), "other branches are not direct Anbar")
        try check(!TransferAccountLinks.allowsDirectAnbar(title: bankTitle, data: Data("invalid".utf8)), "corrupt links do not bypass beneficiary selection")
        let explicitMissing = try JSONEncoder().encode([TransferAccountLinks.sourceKey(bankTitle): "حساب غير موجود"])
        try check(!TransferAccountLinks.allowsDirectAnbar(title: bankTitle, data: explicitMissing), "explicit missing link is not bypassed")
        do {
            _ = try TransferRules.csv([directBank], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
            throw Failure(errorDescription: "direct Anbar source accepted in grouped CSV")
        } catch is Failure { throw Failure(errorDescription: "direct Anbar source accepted in grouped CSV") }
        catch { }
        let aliasAccount = SavedExecutionAccount(entity: "صندوق وهمي تسليف", iban: iban, branch: "مصرف الرشيد فرع تجريبي", swift: "TESTIQBAAAA")
        let linkedGroup = ReportGroup(kind: .deductions, month: group.month, title: "وزارة وهمية / صندوق وهمي", count: 1, total: 1200, byEntity: false)
        let mapKey = TransferAccountLinks.sourceKey(linkedGroup.title)
        let mapping = [mapKey: aliasAccount.key, "health": "حساب قديم"]
        let linkBytes = try JSONEncoder().encode(mapping)
        let restoredLinks = try TransferAccountLinks.decode(linkBytes)
        try check(BackupFormat.settingsKeys.contains("transferPayrollAccountLinks") && restoredLinks["health"] == "حساب قديم", "account links remain in backup settings")
        let lookup = [aliasAccount.key: aliasAccount, account.key: account]
        let resolved = TransferAccountLinks.resolve(title: linkedGroup.title, accounts: lookup, links: try TransferAccountLinks.decode(linkBytes))
        try check(resolved?.key == aliasAccount.key && TransferAccountLinks.authorizes(title: linkedGroup.title, account: aliasAccount, data: linkBytes), "explicit account link")
        try check(!TransferAccountLinks.authorizes(title: linkedGroup.title, account: account, data: linkBytes), "wrong linked account rejected")
        try check(!TransferAccountLinks.authorizes(title: linkedGroup.title, account: aliasAccount, data: Data("invalid".utf8)), "corrupt links rejected")
        let directOverride = try JSONEncoder().encode([TransferAccountLinks.sourceKey(group.title): aliasAccount.key])
        try check(!TransferAccountLinks.authorizes(title: group.title, account: account, data: directOverride), "explicit link overrides same-name automatic account")
        try check(TransferAccountLinks.resolve(title: linkedGroup.title, accounts: [:], links: mapping) == nil, "missing linked account does not silently match")
        let linkedItem = TransferEntity(group: linkedGroup, account: aliasAccount, category: .otherBanks, missingAmounts: 0, beneficiaryName: aliasAccount.entity)
        let linkedCSV = try TransferRules.csv([linkedItem], category: .otherBanks, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        let linkedFields = String(decoding: linkedCSV, as: UTF8.self).components(separatedBy: "\r\n")[1].components(separatedBy: "|")
        try check(linkedFields[7] == aliasAccount.entity && linkedFields[8] == iban, "linked beneficiary name and account in CSV")
        try TransferGroupCSVCheck.validate(linkedCSV, entities: [linkedItem], category: .otherBanks, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        let localGrouped = TransferEntity(group: group, account: localOnly, category: .rafidain, missingAmounts: 0)
        do {
            _ = try TransferRules.csv([localGrouped], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
            throw Failure(errorDescription: "local account accepted for grouped CSV")
        } catch is Failure { throw Failure(errorDescription: "local account accepted for grouped CSV") }
        catch { }
        let issuedLine = TransferIssuedLine(item)
        let namesA = [["موظف وهمي أ", "رتبة", "قسم", "600"], ["موظف وهمي ب", "رتبة", "قسم", "600"]]
        let namesB = [["موظف وهمي أ", "رتبة", "قسم", "600"], ["موظف وهمي ج", "رتبة", "قسم", "600"]]
        let hashA = try TransferIssuedDetails.fingerprint(namesA)
        try check(hashA == TransferIssuedDetails.fingerprint(namesA.reversed().map { $0 }), "name fingerprint must ignore order")
        try check(hashA != TransferIssuedDetails.fingerprint(namesB), "name substitution must change fingerprint")
        var namedLine = issuedLine; namedLine.detailHash = hashA
        var changedNames = issuedLine; changedNames.detailHash = try TransferIssuedDetails.fingerprint(namesB)
        let namedRecord = TransferIssuedRecord(id: UUID(), month: group.month, book: "كتاب وهمي", confirmedAt: .now, lines: [namedLine])
        try check(TransferIssuance.audit(current: [changedNames], records: [namedRecord], month: group.month).changed.count == 1, "changed names after confirmation")
        try check(TransferIssuance.audit(current: [issuedLine], records: [namedRecord], month: group.month).changed.isEmpty, "unread name list must not be a false mismatch")
        let issuedRecord = TransferIssuedRecord(id: UUID(), month: group.month, book: "كتاب وهمي", confirmedAt: .now, lines: [issuedLine])
        let ledgerBytes = try JSONEncoder().encode(TransferIssuance.Ledger(records: [issuedRecord]))
        let restoredLedger = try TransferIssuance.decode(ledgerBytes)
        let confirmedAudit = TransferIssuance.audit(current: [issuedLine], records: restoredLedger.records, month: group.month)
        try check(confirmedAudit.missing.isEmpty && confirmedAudit.changed.isEmpty, "manual issuance survives backup encoding")
        let noConfirmation = TransferIssuance.audit(current: [issuedLine], records: [], month: group.month)
        try check(noConfirmation.missing.count == 1, "preview does not confirm issuance")
        let changedGroup = ReportGroup(kind: .deductions, month: group.month, title: group.title, count: 1, total: 1201, byEntity: false)
        let changedLine = TransferIssuedLine(TransferEntity(group: changedGroup, account: account, category: .rafidain, missingAmounts: 0))
        let changedAudit = TransferIssuance.audit(current: [changedLine], records: [issuedRecord, issuedRecord], month: group.month)
        try check(changedAudit.changed.count == 1 && changedAudit.repeated.count == 1, "changed and duplicate confirmations")
        try check(TransferIssuance.audit(current: [], records: [issuedRecord], month: group.month).absent.count == 1, "removed confirmed entity")
        try check(TransferIssuance.audit(current: [issuedLine], records: [issuedRecord], month: "شهر آخر").missing.count == 1, "confirmation month isolation")
        do { _ = try TransferIssuance.decode(Data("invalid".utf8)); throw Failure(errorDescription: "invalid issuance ledger accepted") }
        catch is Failure { throw Failure(errorDescription: "invalid issuance ledger accepted") }
        catch { }
        let coverage = TransferDraftCoverage()
        let revision = TransferDraftRevision(sourceKey: "revision-1", accounts: Data(), categories: Data(), payrollLinks: Data(), customization: Data(), references: Data(), payerName: "دافع وهمي", payerIBAN: iban)
        let otherRevision = TransferDraftRevision(sourceKey: "revision-2", accounts: Data(), categories: Data(), payrollLinks: Data(), customization: Data(), references: Data(), payerName: "دافع وهمي", payerIBAN: iban)
        coverage.record(revision: revision, entityIDs: [item.id], bookID: "group")
        coverage.record(revision: revision, entityIDs: [item.id], bookID: "group")
        try check(coverage.records(for: revision)[item.id]?.count == 1, "repeated preview is not duplicate")
        coverage.record(revision: revision, entityIDs: [item.id], bookID: "individual")
        try check(coverage.records(for: revision)[item.id]?.count == 2 && coverage.records(for: otherRevision).isEmpty, "coverage duplicate and revision isolation")
        coverage.discardIfDifferent(otherRevision)
        try check(coverage.records(for: revision).isEmpty, "coverage invalidated after source change")
        let individualEntity = TransferEntity(group: group, account: anbar, category: .anbar, missingAmounts: 0)
        try check(TransferArchiveCoverage.unlinked(entities: [item, individualEntity], linkedEntityKeys: []).count == 1, "unlinked individual entity")
        try check(TransferArchiveCoverage.unlinked(entities: [item, individualEntity], linkedEntityKeys: [ArabicText.normalize(group.title)]).isEmpty, "linked individual entity")
        let secondGroup = ReportGroup(kind: .deductions, month: group.month, title: "جهة وهمية ثانية", count: 1, total: 1800, byEntity: false)
        let secondItem = TransferEntity(group: secondGroup, account: account, category: .rafidain, missingAmounts: 0)
        let wordGroup = try TransferWordGroupPlan(allEntities: [item, secondItem], category: .rafidain,
            month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        try check(wordGroup.total == 3000 && wordGroup.table.rows.count == 3 &&
                  wordGroup.table.rows[0][1] == group.title && wordGroup.table.rows[1][4] == "1800" &&
                  wordGroup.table.rows[2][4] == "3000", "Word group rows and total")
        _ = try TransferGroupCSVCheck.validate(wordGroup.csv, entities: [item, secondItem], category: .rafidain,
            month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        let groupText = TransferWordGroupDraft.LetterText(header: "ترويسة وهمية", fixedNumber: "رقم وهمي",
            committee: ["عضو أول", "عضو ثان", "عضو ثالث"], footer: "هامش وهمي")
        let groupDraft = FileManager.default.temporaryDirectory.appendingPathComponent("group-word-\(UUID().uuidString).docx")
        defer { try? FileManager.default.removeItem(at: groupDraft) }
        try TransferWordGroupDraft.write(plan: wordGroup, month: group.month, payerIBAN: iban, text: groupText, destination: groupDraft)
        let draftArchive = try Archive(url: groupDraft, accessMode: .read)
        guard let draftBody = draftArchive["word/document.xml"] else { throw Failure(errorDescription: "Word group draft missing") }
        var draftData = Data()
        _ = try draftArchive.extract(draftBody) { draftData.append($0) }
        let draftText = String(decoding: draftData, as: UTF8.self)
        try check(draftText.contains("جهة وهمية ثانية") && draftText.contains("المجموع") &&
                  draftText.contains("3000") && draftText.contains("عضو أول"), "Word group draft and source")
        do {
            try TransferWordGroupDraft.write(plan: wordGroup, month: "أيلول 2026", payerIBAN: iban, text: groupText,
                destination: FileManager.default.temporaryDirectory.appendingPathComponent("wrong-group-\(UUID().uuidString).docx"))
            throw Failure(errorDescription: "Word group wrong month accepted")
        } catch is Failure { throw Failure(errorDescription: "Word group wrong month accepted") }
        catch { }
        do {
            let unknown = TransferEntity(group: secondGroup, account: nil, category: .unresolved, missingAmounts: 0)
            _ = try TransferWordGroupPlan(allEntities: [item, unknown], category: .rafidain,
                month: group.month, payer: "دافع وهمي", payerIBAN: iban)
            throw Failure(errorDescription: "unresolved Word group accepted")
        } catch is Failure { throw Failure(errorDescription: "unresolved Word group accepted") }
        catch { }
        let individualRows = [ReportRow(id: 1, name: "موظف وهمي", rank: "موظف", workplace: "قسم وهمي", amount: 1200, note: "", entity: group.title)]
        try check(TransferAnbarTablePDF.matchesExtractedCell("موظف وھمي", expected: "موظف وهمي"), "PDF heh extraction")
        try check(!TransferAnbarTablePDF.matchesExtractedCell("موظف وحمي", expected: "موظف وهمي"), "different name must not match")
        try check(!TransferAnbarTablePDF.matchesExtractedCell("موظف وهمي", expected: "موظف وھمي"), "source doachashmee remains distinct")
        _ = try TransferIndividualPlan(kind: .water, group: group, rows: individualRows, missingAmounts: 0)
        let blankAnbarURL = FileManager.default.temporaryDirectory.appendingPathComponent("anbar-test-\(UUID().uuidString).pdf")
        defer { try? FileManager.default.removeItem(at: blankAnbarURL) }
        try UIGraphicsPDFRenderer(bounds: CGRect(x: 0, y: 0, width: 595, height: 842)).pdfData { $0.beginPage() }.write(to: blankAnbarURL)
        let inlinePlan = try TransferIndividualPlan(kind: .rafidainAnbar, group: group, rows: individualRows, missingAmounts: 0)
        let wordAnbarRows = try TransferWordTables.anbar(inlinePlan).rows
        try check(wordAnbarRows.count == 2 && wordAnbarRows[0][2] == "موظف وهمي" &&
                  wordAnbarRows[0][3] == "1200" && wordAnbarRows[1][3] == "1200", "Word Anbar employees and total")
        let inlinePDF = try await TransferAnbarTablePDF.make(blankLetter: blankAnbarURL, plan: inlinePlan)
        defer { try? FileManager.default.removeItem(at: inlinePDF.deletingLastPathComponent()) }
        try check(PDFDocument(url: inlinePDF)?.pageCount == 1, "inline Anbar table")
        let manyRows = (1...25).map { i in ReportRow(id: i, name: "موظف وهمي \(i)", rank: "موظف", workplace: "قسم وهمي", amount: 100, note: "", entity: group.title) }
        let manyGroup = ReportGroup(kind: .deductions, month: group.month, title: group.title, count: 25, total: 2500, byEntity: false)
        let manyPlan = try TransferIndividualPlan(kind: .rafidainAnbar, group: manyGroup, rows: manyRows, missingAmounts: 0)
        let separatePDF = try await TransferAnbarTablePDF.make(blankLetter: blankAnbarURL, plan: manyPlan)
        defer { try? FileManager.default.removeItem(at: separatePDF.deletingLastPathComponent()) }
        try check(PDFDocument(url: separatePDF)?.pageCount == 2, "separate Anbar table")
        let wrongRows = [ReportRow(id: 1, name: "موظف وهمي", rank: "موظف", workplace: "قسم وهمي", amount: 1199, note: "", entity: group.title)]
        do {
            _ = try TransferIndividualPlan(kind: .water, group: group, rows: wrongRows, missingAmounts: 0)
            throw Failure(errorDescription: "individual total mismatch accepted")
        } catch is Failure { throw Failure(errorDescription: "individual total mismatch accepted") }
        catch { }
        let twoPeople = [individualRows[0], ReportRow(id: 2, name: "موظف آخر وهمي", rank: "موظف", workplace: "قسم وهمي", amount: 100, note: "", entity: group.title)]
        let twoGroup = ReportGroup(kind: .deductions, month: group.month, title: group.title, count: 2, total: 1300, byEntity: false)
        do {
            _ = try TransferIndividualPlan(kind: .water, group: twoGroup, rows: twoPeople, missingAmounts: 0)
            throw Failure(errorDescription: "multiple water employees accepted")
        } catch is Failure { throw Failure(errorDescription: "multiple water employees accepted") }
        catch { }
        let data = try TransferRules.csv([item, secondItem], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        try TransferGroupCSVCheck.validate(data, entities: [item, secondItem], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        let text = String(decoding: data, as: UTF8.self)
        let lines = text.components(separatedBy: "\r\n")
        var alteredLines = lines
        var alteredFields = alteredLines[1].components(separatedBy: "|")
        alteredFields[4] = "1199"
        alteredLines[1] = alteredFields.joined(separator: "|")
        do {
            try TransferGroupCSVCheck.validate(Data(alteredLines.joined(separator: "\r\n").utf8), entities: [item, secondItem], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
            throw Failure(errorDescription: "altered group CSV amount accepted")
        } catch is Failure { throw Failure(errorDescription: "altered group CSV amount accepted") }
        catch { }
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
        let tableProof = try await TransferGroupTablePDF.makeChecked(entities: [item, secondItem], category: .rafidain, month: group.month)
        let groupTable = tableProof.url
        defer { try? FileManager.default.removeItem(at: groupTable.deletingLastPathComponent()) }
        guard let groupTableDoc = PDFDocument(url: groupTable), let groupTableText = groupTableDoc.page(at: 0)?.string else { throw Failure(errorDescription: "group table PDF unreadable") }
        try check(groupTableDoc.pageCount == 1 && groupTableText.contains("1800") && groupTableText.contains("3000"), "group table rendered values")
        let fileProof = try TransferGroupCSVCheck.validate(data, entities: [item, secondItem], category: .rafidain, month: group.month, payer: "دافع وهمي", payerIBAN: iban)
        let expectedProof = [TransferReconcileLine(sourceID: item.id, title: item.group.title, amount: 1200),
                             TransferReconcileLine(sourceID: secondItem.id, title: secondItem.group.title, amount: 1800)]
        try check(TransferReconciliation.check(expected: expectedProof, book: tableProof.lines, file: fileProof, target: .grouped).isEmpty, "group PDF and CSV reconciliation")
        try check(!TransferReconciliation.check(expected: expectedProof, book: Array(tableProof.lines.dropLast()), file: fileProof, target: .grouped).isEmpty, "missing book row rejected")
        do {
            _ = try await TransferGroupTablePDF.make(entities: [item, item], category: .rafidain, month: group.month)
            throw Failure(errorDescription: "duplicate group table source accepted")
        } catch is Failure { throw Failure(errorDescription: "duplicate group table source accepted") }
        catch { }
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
        var missingNominal = summary
        missingNominal.missingContractNominal = 1
        let partialFigures = try TransferLetterFigureRules.make(summary: missingNominal, available: columns)
        try check(partialFigures.contractNominalShare == nil && partialFigures.contractTotal == nil && partialFigures.missingContractNominal == 1, "missing nominal must not become a partial contract total")
        try check(partialFigures.foodCompany == figures.foodCompany && partialFigures.treasuryTotal == figures.treasuryTotal, "missing contract nominal must not block unrelated figures")
        do {
            _ = try await TransferContractsLetter.make(figures: partialFigures, payerIBAN: iban, references: Data())
            throw Failure(errorDescription: "incomplete contract letter accepted")
        } catch is Failure { throw Failure(errorDescription: "incomplete contract letter accepted") }
        catch { }
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
        let reviewMonth = "كانون الثاني 2025"
        context.insert(Employee(month: reviewMonth, name: "اسم وهمي ناقص", rank: "عقود", workplace: "قسم وهمي", netSalary: 100))
        context.insert(Employee(month: reviewMonth, name: "اسم وهمي مكتمل", rank: "عقود", workplace: "قسم وهمي", netSalary: 100, details: ["الاسمي": "100"]))
        try context.save()
        let missingRows = try await ContractNominalReviewEngine(modelContainer: container).missing(month: reviewMonth)
        try check(missingRows.count == 1 && missingRows.first?.name == "اسم وهمي ناقص", "identify contract without nominal pay")
        try check(ContractNominalValue.read(details: ["الاسمي": "0"], items: [:]) == 0 && ContractNominalValue.read(details: [:], items: [:]) == nil, "distinguish zero nominal from missing nominal")
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
        let individualItems = TransferIssuance.payrollLines(actual)
        try check(Set(individualItems.map(\.id)).count == individualItems.count && individualItems.allSatisfy { $0.amount > 0 }, "individual payroll items are distinct")
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
        let invalidWord = root.appendingPathComponent("invalid-xml.docx")
        do {
            try TransferWordTemplate.render(template: input, destination: invalidWord,
                fields: ["AMOUNT": "\u{FFFF}", "MONTH": "تشرين الأول"], tableRows: wordRows)
            throw Failure(errorDescription: "invalid XML character accepted")
        } catch is Failure { throw Failure(errorDescription: "invalid XML character accepted") }
        catch { }
        try check(!FileManager.default.fileExists(atPath: invalidWord.path), "invalid Word file not published")
        // Word قد يقسم علامة الحقل بين مقطعين؛ لا يجوز تصديرها كنص ظاهر في الكتاب.
        let splitRoot = root.appendingPathComponent("split", isDirectory: true)
        try FileManager.default.createDirectory(at: splitRoot.appendingPathComponent("word"), withIntermediateDirectories: true)
        try Data("<types/>".utf8).write(to: splitRoot.appendingPathComponent("[Content_Types].xml"))
        let splitXML = "<document xmlns:w=\"urn:word-test\"><w:p><w:r><w:t>{{AMOUNT}}</w:t></w:r><w:r><w:t>{{MIS</w:t></w:r><w:r><w:t>SING}}</w:t></w:r></w:p></document>"
        try Data(splitXML.utf8).write(to: splitRoot.appendingPathComponent("word/document.xml"))
        let splitInput = root.appendingPathComponent("split.docx")
        let splitArchive = try Archive(url: splitInput, accessMode: .create)
        try splitArchive.addEntry(with: "[Content_Types].xml", relativeTo: splitRoot, compressionMethod: .deflate)
        try splitArchive.addEntry(with: "word/document.xml", relativeTo: splitRoot, compressionMethod: .deflate)
        let splitOutput = root.appendingPathComponent("split-output.docx")
        do {
            try TransferWordTemplate.render(template: splitInput, destination: splitOutput, fields: ["AMOUNT": "100"])
            throw Failure(errorDescription: "split Word placeholder accepted")
        } catch is Failure { throw Failure(errorDescription: "split Word placeholder accepted") }
        catch { }
        try check(!FileManager.default.fileExists(atPath: splitOutput.path), "split placeholder file not published")
        let linesRoot = root.appendingPathComponent("lines", isDirectory: true)
        try FileManager.default.createDirectory(at: linesRoot.appendingPathComponent("word"), withIntermediateDirectories: true)
        try Data("<types/>".utf8).write(to: linesRoot.appendingPathComponent("[Content_Types].xml"))
        let linesXML = "<document xmlns:w=\"urn:word-test\"><w:p><w:r><w:t>{{HEADER}}</w:t></w:r></w:p></document>"
        try Data(linesXML.utf8).write(to: linesRoot.appendingPathComponent("word/document.xml"))
        let linesInput = root.appendingPathComponent("lines.docx"), linesOutput = root.appendingPathComponent("lines-output.docx")
        let linesArchive = try Archive(url: linesInput, accessMode: .create)
        try linesArchive.addEntry(with: "[Content_Types].xml", relativeTo: linesRoot, compressionMethod: .deflate)
        try linesArchive.addEntry(with: "word/document.xml", relativeTo: linesRoot, compressionMethod: .deflate)
        try TransferWordTemplate.render(template: linesInput, destination: linesOutput, fields: ["HEADER": "سطر أول\nسطر ثانٍ"])
        let linesResult = try Archive(url: linesOutput, accessMode: .read)
        guard let linesDocument = linesResult["word/document.xml"] else { throw Failure(errorDescription: "Word multiline document missing") }
        var linesData = Data()
        _ = try linesResult.extract(linesDocument) { linesData.append($0) }
        try check(String(decoding: linesData, as: UTF8.self).contains("</w:t><w:br/><w:t xml:space=\"preserve\">"), "Word multiline field")
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
        let numberCases: [(String, CGRect)] = [
            ("food_company", CGRect(x: 92.5, y: 151.6, width: 51.6, height: 22.2)),
            ("tax_social", CGRect(x: 92.5, y: 151.6, width: 51.6, height: 22.2)),
            ("contracts", CGRect(x: 121.2, y: 151.6, width: 51.5, height: 22.2)),
            ("rafidain_anbar", CGRect(x: 87.6, y: 151.6, width: 53.4, height: 22.2)),
            ("water", CGRect(x: 92.5, y: 151.6, width: 51.6, height: 22.2)),
            ("housing", CGRect(x: 95.3, y: 151.6, width: 51.5, height: 22.2)),
            ("ramadi", CGRect(x: 121.2, y: 151.6, width: 51.5, height: 22.2)),
            ("rafidain_group", CGRect(x: 64.8, y: 160, width: 58.4, height: 20.4)),
            ("other_banks_group", CGRect(x: 64.8, y: 160, width: 58.4, height: 20.4)),
            ("cash_salary", CGRect(x: 92.5, y: 151.6, width: 51.6, height: 22.2)),
            ("food_tax", CGRect(x: 92.5, y: 151.6, width: 51.6, height: 22.2))
        ]
        for (id, rect) in numberCases {
            try TransferPDFNumberField.ensurePreserved(id: id, masks: [CGRect(x: 300, y: 300, width: 40, height: 20)])
            do {
                try TransferPDFNumberField.ensurePreserved(id: id, masks: [rect])
                throw Failure(errorDescription: "number field removal accepted")
            } catch is Failure { throw Failure(errorDescription: "number field removal accepted") }
            catch { }
        }
        do {
            try TransferPDFNumberField.ensurePreserved(id: "unknown", masks: [])
            throw Failure(errorDescription: "unknown number field accepted")
        } catch is Failure { throw Failure(errorDescription: "unknown number field accepted") }
        catch { }
        let fakeLetter = letterRenderer.pdfData { context in
            context.beginPage()
            let regular = UIFont(name: "TimesNewRomanPSMT", size: 14.04) ?? ReportFont.regular(14.04)
            let bold = UIFont(name: "TimesNewRomanPS-BoldMT", size: 16) ?? ReportFont.bold(16)
            PDFText.draw("1/22", in: CGRect(x: 92.5, y: 151.6, width: 51.6, height: 22.2), font: ReportFont.bold(12), align: .right)
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
        let signaturePDF = letterRenderer.pdfData { context in
            context.beginPage()
            let boxes = [CGRect(x: 452.260, y: 638.770, width: 94.289, height: 17.668), CGRect(x: 295.610, y: 638.770, width: 98.711, height: 17.668), CGRect(x: 86.304, y: 638.666, width: 131.669, height: 17.795)]
            for box in boxes { PDFText.draw("اسم وهمي", in: box, font: ReportFont.bold(14), align: .center) }
            PDFText.draw("ترويسة وهمية", in: CGRect(x: 28, y: 23.3, width: 192, height: 17.7), font: ReportFont.bold(14), align: .center)
            PDFText.draw("هامش وهمي", in: CGRect(x: 28, y: 773.5, width: 533, height: 13.3), font: ReportFont.bold(11), align: .right)
        }
        guard let signaturePage = PDFDocument(data: signaturePDF)?.page(at: 0) else { throw Failure(errorDescription: "signature reference unavailable") }
        let signatureChanges = TransferLetterCustomization(committeeFirst: "اسم تجريبي", committeeSecond: "اسم ثان", committeeThird: "")
        let preparedSignatures = try TransferPDFCustomization.prepare(id: "food_company", custom: signatureChanges, page: signaturePage, height: 841.9)
        try check(preparedSignatures.masks.count == 3 && preparedSignatures.flows.count == 2, "committee name masks and clear")
        let blockChanges = TransferLetterCustomization(header: "سطر تجريبي\nسطر ثان", footer: "هامش تجريبي")
        let blocks = try TransferPDFCustomization.prepare(id: "food_company", custom: blockChanges, page: signaturePage, height: 841.9)
        try check(blocks.masks.count == 2 && blocks.flows.count == 3, "header and footer customization")
        do {
            _ = try TransferPDFCustomization.prepare(id: "food_company", custom: TransferLetterCustomization(header: "1\n2\n3\n4\n5\n6"), page: signaturePage, height: 841.9)
            throw Failure(errorDescription: "oversized header accepted")
        } catch is Failure { throw Failure(errorDescription: "oversized header accepted") }
        catch { }
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
            PDFText.draw("1/22", in: CGRect(x: 92.5, y: 151.6, width: 51.6, height: 22.2), font: ReportFont.bold(12), align: .right)
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
        var invalidFontSlot = slot
        invalidFontSlot.privateFontData = Data("invalid font".utf8)
        invalidFontSlot.privateFontStyle = .bold
        do {
            _ = try await TransferPDFOverlay.make(blankReference: blankPDF, flows: [TransferPDFTextFlow(value: "ترويسة تجريبية", slots: [invalidFontSlot])])
            throw Failure(errorDescription: "invalid private font accepted by PDF overlay")
        } catch is Failure { throw Failure(errorDescription: "invalid private font accepted by PDF overlay") }
        catch { }
        let monthSlot = TransferPDFMonthSlot(rect: CGRect(x: 230, y: 100, width: 35, height: 30), month: 10, glyphs: digitGlyphs)
        let composed = try await TransferPDFOverlay.make(blankReference: blankPDF, flows: [TransferPDFTextFlow(value: "123456", slots: [slot], verifyNumericOutput: true)], months: [monthSlot])
        defer { try? FileManager.default.removeItem(at: composed.deletingLastPathComponent()) }
        guard let composedDoc = PDFDocument(url: composed), let composedText = composedDoc.page(at: 0)?.string else { throw Failure(errorDescription: "composed PDF missing") }
        try check(composedDoc.pageCount == 1 && composedText.contains("123456"), "PDF overlay")
        try TransferPDFPageProof.verify(sources: [composedDoc], result: composed)
        do {
            _ = try await TransferPDFOverlay.make(blankReference: blankPDF, flows: [TransferPDFTextFlow(value: "123ABC", slots: [slot], verifyNumericOutput: true)])
            throw Failure(errorDescription: "non-numeric amount accepted")
        } catch is Failure { throw Failure(errorDescription: "non-numeric amount accepted") }
        catch { }
        do {
            guard let other = PDFDocument(data: blankPDF) else { throw Failure(errorDescription: "blank PDF unreadable") }
            try TransferPDFPageProof.verify(sources: [other], result: composed)
            throw Failure(errorDescription: "changed merged page accepted")
        } catch is Failure { throw Failure(errorDescription: "changed merged page accepted") }
        catch { }
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
        do {
            _ = try await TransferPDFOverlay.make(blankReference: blankPDF, flows: [TransferPDFTextFlow(value: String(repeating: "1234567890", count: 100), slots: [slot], fieldName: "حقل تجريبي")])
            throw Failure(errorDescription: "labeled overflowing PDF field accepted")
        } catch let failure as TransferPDFOverlay.Failure {
            guard case .fieldDoesNotFit(let field) = failure, field == "حقل تجريبي" else { throw Failure(errorDescription: "wrong overflow diagnostic") }
        }
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

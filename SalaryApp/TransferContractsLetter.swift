import UIKit
import PDFKit
import CryptoKit

/// إعداد أولي لكتاب ضمان العقود؛ لا يُفعّل التصدير قبل التحقق على الجهاز.
nonisolated enum TransferContractsLetter {
    enum Failure: LocalizedError {
        case incomplete, accountMismatch, invalidReference, missingFields
        var errorDescription: String? {
            switch self {
            case .incomplete: tr("بيانات ضمان العقود أو الشهر غير مكتملة.", "Contract social security or month data is incomplete.")
            case .accountMismatch: tr("رقم حساب الجهة الدافعة لا يطابق الحساب الموجود في مرجع الكتاب.", "The payer IBAN does not match the letter reference.")
            case .invalidReference: tr("مرجع كتاب ضمان العقود لا يطابق تنسيقه المعتمد.", "The contract social security letter reference does not match the approved layout.")
            case .missingFields: tr("تعذّر التحقق من مواضع البيانات القديمة في مرجع كتاب ضمان العقود.", "The old fields in the contract social security reference could not be verified.")
            }
        }
    }
    private static let amount = CGRect(x: 362.23, y: 317.344, width: 79.986, height: 15.654)
    private static let wordsMasks = [
        CGRect(x: 136.872, y: 317.54, width: 211.526, height: 15.543),
        CGRect(x: 465.821, y: 333.62, width: 94.249, height: 15.543),
    ]
    private static let wordSlots = wordsMasks
    private static let years = [
        CGRect(x: 35.52, y: 180.605, width: 33.51, height: 16.725),
        CGRect(x: 436.27, y: 400.31, width: 35.97, height: 17.667),
        CGRect(x: 468.46, y: 695.92, width: 35.97, height: 17.667),
        CGRect(x: 312.05, y: 695.92, width: 35.97, height: 17.667),
        CGRect(x: 94.224, y: 695.816, width: 36.054, height: 17.796),
    ]
    private static let numericMonths = [
        CGRect(x: 82.824, y: 178.268, width: 15.156, height: 21.654),
        CGRect(x: 512.86, y: 695.92, width: 17.97, height: 17.667),
        CGRect(x: 356.59, y: 695.92, width: 17.97, height: 17.667),
    ]
    private static let monthNameMasks = [
        CGRect(x: 484.78, y: 400.31, width: 63.68, height: 17.667),
        CGRect(x: 143.308, y: 696.04, width: 63.563, height: 17.667),
    ]
    private static let nameSlots = monthNameMasks
    private static let tableAmounts = [
        CGRect(x: 459.46, y: 488.34, width: 61.50, height: 13.28),
        CGRect(x: 317.59, y: 488.34, width: 68.18, height: 13.28),
        CGRect(x: 177.62, y: 488.24, width: 79.76, height: 15.66)
    ]
    private static let yearFonts = ["Arial-BoldMT", "TimesNewRomanPS-BoldMT", "TimesNewRomanPS-BoldMT", "TimesNewRomanPS-BoldMT", "Arial-BoldMT"]
    private static let yearSizes: [CGFloat] = [15, 16, 16, 16, 16]
    private static let monthNames = ["كانون الثاني", "شباط", "آذار", "نيسان", "أيار", "حزيران", "تموز", "آب", "أيلول", "تشرين الأول", "تشرين الثاني", "كانون الأول"]

    private static func selected(_ rect: CGRect, page: PDFPage, height: CGFloat) -> String {
        let bounds = CGRect(x: rect.minX - 0.2, y: height - rect.maxY - 0.2, width: rect.width + 0.4, height: rect.height + 0.4)
        return page.selection(for: bounds)?.string?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }
    private static func ascii(_ input: String) -> String {
        ArabicText.latinDigits(input).uppercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) }
    }
    private static func digits(_ input: String) -> String {
        ArabicText.latinDigits(input).filter { $0.isASCII && $0.isNumber }
    }
    private static func slot(_ rect: CGRect, _ font: String, _ size: CGFloat) -> TransferPDFTextSlot {
        TransferPDFTextSlot(rect: rect, fontName: font, fontSize: size, alignment: .right)
    }
    static func make(figures: TransferLetterFigures, payerIBAN: String, references: Data, customization: TransferLetterCustomization = .init(), monthFontData: Data? = nil) async throws -> URL {
        let period = try TransferLetterPeriod(figures.month)
        let amountValue = figures.contractTotal
        guard TransferRules.validIBAN(payerIBAN), amountValue.isFinite, amountValue > 0,
              amountValue.rounded() == amountValue, amountValue <= 9_007_199_254_740_991 else { throw Failure.incomplete }
        let referenceWork = Task.detached(priority: .userInitiated) {
            try TransferReferencePack.load(["contracts", TransferReferencePack.digitTemplateID], from: references)
        }
        let source = try await withTaskCancellationHandler { try await referenceWork.value } onCancel: { referenceWork.cancel() }
        try Task.checkCancellation()
        guard let original = source["contracts"], let digitPDF = source[TransferReferencePack.digitTemplateID],
              let document = PDFDocument(data: original), !document.isLocked, document.pageCount == 1,
              let page = document.page(at: 0) else { throw Failure.invalidReference }
        let pageSize = page.bounds(for: .mediaBox)
        guard abs(pageSize.width - 595.3) < 2, abs(pageSize.height - 841.9) < 2 else { throw Failure.invalidReference }
        // نقرأ موضع حسابنا تحديداً؛ وجود الرقم في حساب المستفيد لا يعني تطابق حساب الدفع.
        let payerArea = CGRect(x: 126.26, y: 333.62, width: 170.586, height: 15.543)
        let referencePayer = ascii(selected(payerArea, page: page, height: pageSize.height))
        guard referencePayer == ascii(payerIBAN) else { throw Failure.accountMismatch }
        let rawAmount = selected(amount, page: page, height: pageSize.height).trimmingCharacters(in: CharacterSet.whitespacesAndNewlines.union(CharacterSet(charactersIn: "()")))
        let originalYears = years.map { digits(selected($0, page: page, height: pageSize.height)) }
        let originalMonths = numericMonths.compactMap { Int(digits(selected($0, page: page, height: pageSize.height))) }
        guard AnnualDeductionAmount.parse(rawAmount) != nil,
              wordsMasks.allSatisfy({ !selected($0, page: page, height: pageSize.height).isEmpty }),
              originalYears.allSatisfy({ $0.count == 4 }), Set(originalYears).count == 1,
              originalMonths.count == numericMonths.count, originalMonths.allSatisfy({ (1...12).contains($0) }), Set(originalMonths).count == 1,
              monthNameMasks.allSatisfy({ !selected($0, page: page, height: pageSize.height).isEmpty }) else { throw Failure.missingFields }
        let oldTable = tableAmounts.compactMap { AnnualDeductionAmount.parse(selected($0, page: page, height: pageSize.height)) }
        guard oldTable.count == 3, oldTable[0] + oldTable[1] == oldTable[2],
              AnnualDeductionAmount.parse(rawAmount) == oldTable[2],
              figures.contractNominalShare.isFinite, figures.contractRetirement.isFinite,
              figures.contractNominalShare >= 0, figures.contractRetirement >= 0,
              figures.contractNominalShare.rounded() == figures.contractNominalShare,
              figures.contractRetirement.rounded() == figures.contractRetirement else { throw Failure.missingFields }
        let customFields = try TransferPDFCustomization.prepare(id: "contracts", custom: customization, page: page, height: pageSize.height)
        let masks = [amount] + wordsMasks + years + numericMonths + monthNameMasks + tableAmounts + customFields.masks
        try TransferPDFNumberField.ensurePreserved(id: "contracts", masks: masks)
        let digest = SHA256.hash(data: original).map { String(format: "%02x", $0) }.joined()
        let blank = try await TransferPDFBackground.make(reference: original, sha256: digest, pageIndex: 0, masks: masks)
        try Task.checkCancellation()
        let formatter = NumberFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.numberStyle = .decimal
        formatter.maximumFractionDigits = 0
        guard let numeric = formatter.string(from: NSNumber(value: amountValue)) else { throw Failure.incomplete }
        let words = try ArabicAmountWords.dinars(Int64(amountValue))
        var flows = [
            TransferPDFTextFlow(value: numeric, slots: [slot(amount, "Arial-BoldMT", 14.04)]),
            TransferPDFTextFlow(value: words, slots: wordSlots.map { slot($0, "TimesNewRomanPSMT", 14.04) }, keepLastWordOnLastLine: true),
            TransferPDFTextFlow(value: monthNames[period.month - 1], slots: [slot(nameSlots[0], "TimesNewRomanPS-BoldMT", 16)]),
            TransferPDFTextFlow(value: monthNames[period.month - 1], slots: [slot(nameSlots[1], "TimesNewRomanPS-BoldMT", 16)])
        ]
        let tableValues = [figures.contractRetirement, figures.contractNominalShare, amountValue]
        for i in tableAmounts.indices {
            guard let value = formatter.string(from: NSNumber(value: tableValues[i])) else { throw Failure.incomplete }
            flows.append(TransferPDFTextFlow(value: value, slots: [slot(tableAmounts[i], i == 2 ? "Arial-BoldMT" : "TimesNewRomanPS-BoldMT", i == 2 ? 14.04 : 12)]))
        }
        flows.append(contentsOf: customFields.flows)
        for i in years.indices { flows.append(TransferPDFTextFlow(value: String(period.year), slots: [slot(years[i], yearFonts[i], yearSizes[i])])) }
        for rect in numericMonths.dropFirst() { flows.append(TransferPDFTextFlow(value: String(period.month), slots: [slot(rect, "TimesNewRomanPS-BoldMT", 16)])) }
        let glyphs = try TransferMonthDigits.extract(from: digitPDF)
        try Task.checkCancellation()
        return try await TransferPDFOverlay.make(blankReference: blank, flows: flows,
            months: [TransferPDFMonthSlot(rect: numericMonths[0], month: period.month, glyphs: glyphs, fontData: monthFontData)])
    }
}

import UIKit
import PDFKit
import CryptoKit

/// إعداد أولي لكتاب الضريبة والحماية الاجتماعية؛ لا يُفعّل التصدير قبل التحقق على الجهاز.
nonisolated enum TransferTaxSocialLetter {
    enum Failure: LocalizedError {
        case incomplete, accountMismatch, invalidReference, missingFields
        var errorDescription: String? {
            switch self {
            case .incomplete: tr("بيانات الضريبة والحماية الاجتماعية أو الشهر غير مكتملة.", "Tax and social protection or month data is incomplete.")
            case .accountMismatch: tr("رقم حساب الجهة الدافعة لا يطابق الحساب الموجود في مرجع الكتاب.", "The payer IBAN does not match the letter reference.")
            case .invalidReference: tr("مرجع كتاب الضريبة والحماية الاجتماعية لا يطابق تنسيقه المعتمد.", "The tax and social protection letter reference does not match the approved layout.")
            case .missingFields: tr("تعذّر التحقق من مواضع البيانات القديمة في مرجع كتاب الضريبة والحماية الاجتماعية.", "The old fields in the tax and social protection reference could not be verified.")
            }
        }
    }
    private static let amount = CGRect(x: 358.99, y: 317.344, width: 72.166, height: 15.654)
    private static let wordsMasks = [
        CGRect(x: 178.106, y: 317.54, width: 161.179, height: 15.543),
        CGRect(x: 488.528, y: 333.62, width: 72.762, height: 15.543),
    ]
    private static let wordSlots = wordsMasks
    private static let years = [
        CGRect(x: 35.52, y: 180.605, width: 33.63, height: 16.725),
        CGRect(x: 428.71, y: 397.94, width: 31.65, height: 15.543),
        CGRect(x: 468.46, y: 714.88, width: 35.97, height: 17.667),
        CGRect(x: 312.05, y: 714.88, width: 35.97, height: 17.667),
        CGRect(x: 94.224, y: 714.776, width: 36.054, height: 17.796),
    ]
    private static let numericMonths = [
        CGRect(x: 82.944, y: 178.268, width: 15.156, height: 21.654),
        CGRect(x: 512.86, y: 714.88, width: 17.97, height: 17.667),
        CGRect(x: 356.59, y: 714.88, width: 17.97, height: 17.667),
    ]
    private static let monthNameMasks = [
        CGRect(x: 474.82, y: 397.94, width: 76.534, height: 15.543),
        CGRect(x: 143.308, y: 715.0, width: 63.563, height: 17.667),
    ]
    private static let nameSlots = monthNameMasks
    private static let tableAmounts = [
        CGRect(x: 309.89, y: 477.01, width: 61.50, height: 13.38),
        CGRect(x: 309.89, y: 491.29, width: 61.50, height: 13.38),
        CGRect(x: 299.57, y: 505.64, width: 71.91, height: 15.66)
    ]
    private static let yearFonts = ["Arial-BoldMT", "TimesNewRomanPS-BoldMT", "TimesNewRomanPS-BoldMT", "TimesNewRomanPS-BoldMT", "Arial-BoldMT"]
    private static let yearSizes: [CGFloat] = [15, 14.04, 16, 16, 16]
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
    static func make(figures: TransferLetterFigures, payerIBAN: String, references: Data) async throws -> URL {
        let period = try TransferLetterPeriod(figures.month)
        let amountValue = figures.treasuryTotal
        guard TransferRules.validIBAN(payerIBAN), amountValue.isFinite, amountValue > 0,
              amountValue.rounded() == amountValue, amountValue <= 9_007_199_254_740_991 else { throw Failure.incomplete }
        let referenceWork = Task.detached(priority: .userInitiated) {
            try TransferReferencePack.load(["tax_social", TransferReferencePack.digitTemplateID], from: references)
        }
        let source = try await withTaskCancellationHandler { try await referenceWork.value } onCancel: { referenceWork.cancel() }
        try Task.checkCancellation()
        guard let original = source["tax_social"], let digitPDF = source[TransferReferencePack.digitTemplateID],
              let document = PDFDocument(data: original), !document.isLocked, document.pageCount == 1,
              let page = document.page(at: 0) else { throw Failure.invalidReference }
        let pageSize = page.bounds(for: .mediaBox)
        guard abs(pageSize.width - 595.3) < 2, abs(pageSize.height - 841.9) < 2 else { throw Failure.invalidReference }
        // نقرأ موضع حسابنا تحديداً؛ وجود الرقم في حساب المستفيد لا يعني تطابق حساب الدفع.
        let payerArea = CGRect(x: 97.584, y: 333.62, width: 170.6, height: 15.543)
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
              figures.incomeTax.isFinite, figures.socialProtection.isFinite,
              figures.incomeTax >= 0, figures.socialProtection >= 0,
              figures.incomeTax.rounded() == figures.incomeTax,
              figures.socialProtection.rounded() == figures.socialProtection else { throw Failure.missingFields }
        let masks = [amount] + wordsMasks + years + numericMonths + monthNameMasks + tableAmounts
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
            TransferPDFTextFlow(value: monthNames[period.month - 1], slots: [slot(nameSlots[0], "TimesNewRomanPS-BoldMT", 14.04)]),
            TransferPDFTextFlow(value: monthNames[period.month - 1], slots: [slot(nameSlots[1], "TimesNewRomanPS-BoldMT", 16)])
        ]
        let tableValues = [figures.socialProtection, figures.incomeTax, amountValue]
        for i in tableAmounts.indices {
            guard let value = formatter.string(from: NSNumber(value: tableValues[i])) else { throw Failure.incomplete }
            flows.append(TransferPDFTextFlow(value: value, slots: [slot(tableAmounts[i], "Arial-BoldMT", i == 2 ? 14.04 : 12)]))
        }
        for i in years.indices { flows.append(TransferPDFTextFlow(value: String(period.year), slots: [slot(years[i], yearFonts[i], yearSizes[i])])) }
        for rect in numericMonths.dropFirst() { flows.append(TransferPDFTextFlow(value: String(period.month), slots: [slot(rect, "TimesNewRomanPS-BoldMT", 16)])) }
        let glyphs = try TransferMonthDigits.extract(from: digitPDF)
        try Task.checkCancellation()
        return try await TransferPDFOverlay.make(blankReference: blank, flows: flows,
            months: [TransferPDFMonthSlot(rect: numericMonths[0], month: period.month, glyphs: glyphs)])
    }
}

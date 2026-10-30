import UIKit
import PDFKit
import CryptoKit

/// إعداد كتاب شركة عراق الجوال من مرجع المالك؛ لا تُحفَظ المبالغ القديمة في المخرَج.
nonisolated enum TransferCompanyLetter {
    enum Failure: LocalizedError {
        case incomplete, accountMismatch, invalidReference, missingFields
        var errorDescription: String? {
            switch self {
            case .incomplete: tr("بيانات الشركة أو الشهر غير مكتملة.", "Company or month data is incomplete.")
            case .accountMismatch: tr("رقم حساب الجهة الدافعة لا يطابق الحساب الموجود في مرجع الكتاب.", "The payer IBAN does not match the letter reference.")
            case .invalidReference: tr("مرجع كتاب الشركة لا يطابق تنسيقه المعتمد.", "The company letter reference does not match the approved layout.")
            case .missingFields: tr("تعذّر التحقق من مواضع البيانات القديمة في مرجع كتاب الشركة.", "The old fields in the company reference could not be verified.")
            }
        }
    }
    private static let amount = CGRect(x: 298.61, y: 317.41, width: 105.02, height: 17.79)
    private static let wordsMasks = [
        CGRect(x: 149.71, y: 319.34, width: 128.87, height: 15.54),
        CGRect(x: 266.05, y: 335.78, width: 294.99, height: 15.54)
    ]
    private static let wordSlots = [
        CGRect(x: 32.28, y: 319.34, width: 246.30, height: 15.54),
        CGRect(x: 266.05, y: 335.78, width: 294.99, height: 15.54)
    ]
    private static let years = [
        CGRect(x: 35.52, y: 180.60, width: 33.63, height: 16.73),
        CGRect(x: 428.47, y: 416.30, width: 31.65, height: 15.54),
        CGRect(x: 468.46, y: 675.64, width: 35.97, height: 17.67),
        CGRect(x: 312.05, y: 675.64, width: 35.97, height: 17.67),
        CGRect(x: 94.22, y: 675.42, width: 36.06, height: 17.79)
    ]
    private static let numericMonths = [
        CGRect(x: 82.94, y: 178.27, width: 15.16, height: 21.65),
        CGRect(x: 512.86, y: 675.64, width: 17.97, height: 17.67),
        CGRect(x: 356.59, y: 675.64, width: 17.97, height: 17.67)
    ]
    private static let monthNameMasks = [
        CGRect(x: 471.10, y: 416.30, width: 55.86, height: 15.54),
        CGRect(x: 172.94, y: 675.64, width: 33.93, height: 17.67),
        CGRect(x: 143.31, y: 675.64, width: 25.61, height: 17.67)
    ]
    private static let nameSlots = [
        CGRect(x: 464.20, y: 416.30, width: 62.76, height: 15.54),
        CGRect(x: 139.30, y: 675.64, width: 67.57, height: 17.67)
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
    static func make(figures: TransferLetterFigures, payerIBAN: String, references: Data, customization: TransferLetterCustomization = .init()) async throws -> URL {
        let period = try TransferLetterPeriod(figures.month)
        guard TransferRules.validIBAN(payerIBAN), figures.foodCompany.isFinite, figures.foodCompany > 0,
              figures.foodCompany.rounded() == figures.foodCompany, figures.foodCompany <= 9_007_199_254_740_991 else { throw Failure.incomplete }
        let referenceWork = Task.detached(priority: .userInitiated) {
            try TransferReferencePack.load(["food_company", TransferReferencePack.digitTemplateID], from: references)
        }
        let source = try await withTaskCancellationHandler { try await referenceWork.value } onCancel: { referenceWork.cancel() }
        try Task.checkCancellation()
        guard let original = source["food_company"], let digitPDF = source[TransferReferencePack.digitTemplateID],
              let document = PDFDocument(data: original), !document.isLocked, document.pageCount == 1,
              let page = document.page(at: 0) else { throw Failure.invalidReference }
        let pageSize = page.bounds(for: .mediaBox)
        guard abs(pageSize.width - 595.3) < 2, abs(pageSize.height - 841.9) < 2 else { throw Failure.invalidReference }
        // نقرأ موضع حسابنا تحديداً؛ وجود الرقم في حساب المستفيد لا يعني تطابق حساب الدفع.
        let payerArea = CGRect(x: 384, y: 350, width: 175, height: 19)
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
        let customFields = try TransferPDFCustomization.prepare(id: "food_company", custom: customization, page: page, height: pageSize.height)
        let masks = [amount] + wordsMasks + years + numericMonths + monthNameMasks + customFields.masks
        let digest = SHA256.hash(data: original).map { String(format: "%02x", $0) }.joined()
        let blank = try await TransferPDFBackground.make(reference: original, sha256: digest, pageIndex: 0, masks: masks)
        try Task.checkCancellation()
        let formatter = NumberFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.numberStyle = .decimal
        formatter.maximumFractionDigits = 0
        guard let numeric = formatter.string(from: NSNumber(value: figures.foodCompany)) else { throw Failure.incomplete }
        let words = try ArabicAmountWords.dinars(Int64(figures.foodCompany))
        var flows = [
            TransferPDFTextFlow(value: numeric, slots: [slot(amount, "Arial-BoldMT", 16)]),
            TransferPDFTextFlow(value: words, slots: wordSlots.map { slot($0, "TimesNewRomanPSMT", 14.04) }, keepLastWordOnLastLine: true),
            TransferPDFTextFlow(value: monthNames[period.month - 1], slots: [slot(nameSlots[0], "TimesNewRomanPS-BoldMT", 14.04)]),
            TransferPDFTextFlow(value: monthNames[period.month - 1], slots: [slot(nameSlots[1], "TimesNewRomanPS-BoldMT", 16)])
        ]
        flows.append(contentsOf: customFields.flows)
        for i in years.indices { flows.append(TransferPDFTextFlow(value: String(period.year), slots: [slot(years[i], yearFonts[i], yearSizes[i])])) }
        for rect in numericMonths.dropFirst() { flows.append(TransferPDFTextFlow(value: String(period.month), slots: [slot(rect, "TimesNewRomanPS-BoldMT", 16)])) }
        let glyphs = try TransferMonthDigits.extract(from: digitPDF)
        try Task.checkCancellation()
        return try await TransferPDFOverlay.make(blankReference: blank, flows: flows,
            months: [TransferPDFMonthSlot(rect: numericMonths[0], month: period.month, glyphs: glyphs)])
    }
}

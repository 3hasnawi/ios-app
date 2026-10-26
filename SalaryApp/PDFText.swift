import UIKit
import CoreText

/// رسم نصوص ملفات PDF بـ CoreText مباشرة بدل NSAttributedString.draw.
///
/// السبب: عند تفعيل إحدى ميزات تسهيلات الاستخدام (مثل الوصول الموجّه) يسجّل UIKit كل نص يُرسم
/// بطريقته، ثم يحذف التسجيلات واحداً واحداً على الخيط الرئيسي. الكشف الكبير فيه عشرات الآلاف من الخلايا،
/// فيتجمد التطبيق أكثر من ١٠ ثوانٍ ويغلقه النظام (0x8BADF00D)، وتفشل المشاركة والنسخ بعد التصدير.
/// CoreText لا يمر بهذا التسجيل، والشكل يبقى كما هو.
nonisolated enum PDFText {
    private static let rtl: CTParagraphStyle = {
        let direction = CTWritingDirection.rightToLeft
        return withUnsafeBytes(of: direction) { (raw: UnsafeRawBufferPointer) -> CTParagraphStyle in
            var setting = CTParagraphStyleSetting(spec: .baseWritingDirection,
                                                  valueSize: MemoryLayout<CTWritingDirection>.size,
                                                  value: raw.baseAddress!)
            return CTParagraphStyleCreate(&setting, 1)
        }
    }()

    /// يرسم النص في سياق الرسم الحالي (إحداثيات UIKit: الأصل أعلى اليسار)، في وسط المستطيل عمودياً.
    /// - wrap = false: سطر واحد، ويُقص آخره بـ "…" إن لم يتسع (مثل lineBreakMode = .byTruncatingTail).
    /// - wrap = true: يلتف على أسطر بحسب الكلمات (قد يتجاوز ارتفاع المستطيل كما كان سابقاً).
    static func draw(_ text: String, in rect: CGRect, font: UIFont, color: UIColor = .black,
                     align: NSTextAlignment = .center, wrap: Bool = false) {
        guard !text.isEmpty, rect.width > 1, let cg = UIGraphicsGetCurrentContext() else { return }
        let attributes: [NSAttributedString.Key: Any] = [
            .font: font,
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): color.cgColor,
            NSAttributedString.Key(kCTParagraphStyleAttributeName as String): rtl,
        ]
        let attributed = NSAttributedString(string: text, attributes: attributes)
        let width = Double(rect.width)
        let typesetter = CTTypesetterCreateWithAttributedString(attributed)
        let length = attributed.length

        var lines: [CTLine] = []
        if wrap {
            var start = 0
            while start < length {
                var count = CTTypesetterSuggestLineBreak(typesetter, start, width)
                if count <= 0 { count = length - start }
                lines.append(CTTypesetterCreateLine(typesetter, CFRange(location: start, length: count)))
                start += count
            }
        } else {
            let line = CTTypesetterCreateLine(typesetter, CFRange(location: 0, length: length))
            if CTLineGetTypographicBounds(line, nil, nil, nil) - CTLineGetTrailingWhitespaceWidth(line) > width {
                let token = CTLineCreateWithAttributedString(NSAttributedString(string: "…", attributes: attributes))
                lines.append(CTLineCreateTruncatedLine(line, width, .end, token) ?? line)
            } else {
                lines.append(line)
            }
        }

        let lineHeight = font.lineHeight
        let total = lineHeight * CGFloat(lines.count)
        let top = rect.midY - total / 2
        let flush: CGFloat = switch align {
        case .left: 0
        case .center: 0.5
        default: 1   // يمين / طبيعي (عربي)
        }

        cg.saveGState()
        cg.textMatrix = CGAffineTransform(scaleX: 1, y: -1)   // السياق مقلوب (UIKit)، فتُعدل الحروف.
        for (i, line) in lines.enumerated() {
            let x = rect.minX + CGFloat(CTLineGetPenOffsetForFlush(line, flush, width))
            let baseline = top + font.ascender + lineHeight * CGFloat(i)
            cg.textPosition = CGPoint(x: x, y: baseline)
            CTLineDraw(line, cg)
        }
        cg.restoreGState()
        cg.textMatrix = .identity
    }
}

/// تجهيز ملفات التصدير واحداً تلو الآخر في الخلفية (لا ملفّا PDF في الوقت نفسه)،
/// مع تحرير الذاكرة المؤقتة بعد كل ملف.
nonisolated enum PDFWork {
    private static let queue = DispatchQueue(label: "salaryapp.pdf-work", qos: .userInitiated)

    static func run<T>(_ body: @escaping () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                continuation.resume(with: Result { try autoreleasepool { try body() } })
            }
        }
    }
}

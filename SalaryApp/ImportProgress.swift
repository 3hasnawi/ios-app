import SwiftUI
import QuartzCore

/// تقدّم الاستيراد (رواتب أو ملاك)، يظهر كشريط في أسفل الشاشة أياً كان التبويب.
@MainActor @Observable
final class ImportProgress {
    static let shared = ImportProgress()

    var isActive = false
    var title = ""
    var detail = ""
    /// من 0 إلى 1.
    var fraction: Double = 0

    func begin(_ title: String) {
        self.title = title
        detail = ""
        fraction = 0
        withAnimation(.snappy) { isActive = true }
    }

    func end() {
        fraction = 1
        // بدون حركة: الحركة كانت تتأجل أحياناً خلف رسالة النتيجة فيبقى الشريط ظاهراً.
        isActive = false
    }

    /// مرحلة من العمل: [start, end] من الشريط كله، مع وصفها.
    func sink(_ start: Double, _ end: Double, _ detail: String) -> ProgressSink {
        self.detail = detail
        if fraction < start { fraction = start }
        return ProgressSink(start: start, end: end, detail: detail)
    }
}

/// يُمرَّر إلى أعمال الخلفية. يرسل التقدّم إلى الواجهة بحد أقصى نحو 15 مرة في الثانية،
/// حتى لا يُشغل خيط الواجهة أثناء الاستيراد.
nonisolated struct ProgressSink: Sendable {
    let start: Double
    let end: Double
    let detail: String
    private let throttle = Throttle()

    init(start: Double, end: Double, detail: String) {
        self.start = start
        self.end = end
        self.detail = detail
    }

    /// value من 0 إلى 1 داخل هذه المرحلة.
    func callAsFunction(_ value: Double) {
        let overall = start + (end - start) * min(max(value, 0), 1)
        guard throttle.allow(overall) else { return }
        let detail = detail
        Task { @MainActor in
            let p = ImportProgress.shared
            if overall > p.fraction { p.fraction = overall }
            p.detail = detail
        }
    }

    /// جزء من هذه المرحلة.
    func sub(_ from: Double, _ to: Double, _ detail: String? = nil) -> ProgressSink {
        ProgressSink(start: start + (end - start) * from, end: start + (end - start) * to, detail: detail ?? self.detail)
    }

    private final class Throttle: @unchecked Sendable {
        private let lock = NSLock()
        private var last: Double = -1
        private var lastTime: CFTimeInterval = 0

        func allow(_ value: Double) -> Bool {
            lock.lock()
            defer { lock.unlock() }
            let now = CACurrentMediaTime()
            guard value - last >= 0.004, now - lastTime >= 0.066 || value >= 1 else { return false }
            last = value
            lastTime = now
            return true
        }
    }
}

/// الشريط نفسه: عنوان، نسبة مئوية، شريط تقدّم، ووصف المرحلة.
struct ImportProgressBar: View {
    let progress: ImportProgress

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Image(systemName: "square.and.arrow.down.fill")
                    .foregroundStyle(Color.accentColor)
                Text(progress.title).font(.subheadline.weight(.semibold))
                Spacer()
                Text(Num.digits("\(Int((progress.fraction * 100).rounded()))%"))
                    .font(.subheadline.weight(.bold).monospacedDigit())
                    .contentTransition(.numericText())
            }
            ProgressView(value: progress.fraction)
                .tint(Color.accentColor)
                .animation(.linear(duration: 0.15), value: progress.fraction)
            if !progress.detail.isEmpty {
                Text(progress.detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
        .padding(14)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
        .shadow(color: .black.opacity(0.12), radius: 12, y: 4)
        .padding(.horizontal, 16)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("importProgress")
    }
}

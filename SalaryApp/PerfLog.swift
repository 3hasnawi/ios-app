import Foundation
import QuartzCore

/// قياس السلاسة أثناء اختبار المحاكي فقط (يُفعَّل بـ -perfLog YES، ولا يعمل عند المستخدم).
/// يكتب في Documents/perf.txt:
///   hang <ms>   : خيط الواجهة لم يستجب هذه المدة (تجمّد/لاك).
///   <name> <ms> : زمن عملية (مثل البحث).
/// كل سطر يبدأ بالوقت (ثوانٍ منذ 1970) لمطابقته مع خطوات الاختبار.
nonisolated enum PerfLog {
    static var isEnabled: Bool { UserDefaults.standard.bool(forKey: "perfLog") }

    private static let queue = DispatchQueue(label: "perf.log")
    private static let url: URL? = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first?
        .appendingPathComponent("perf.txt")

    /// وقت بدء العملية (قبل main) لقياس زمن الفتح كاملاً.
    static let processStart: Date = {
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid()]
        guard sysctl(&mib, 4, &info, &size, nil, 0) == 0 else { return Date() }
        let t = info.kp_proc.p_un.__p_starttime
        return Date(timeIntervalSince1970: Double(t.tv_sec) + Double(t.tv_usec) / 1_000_000)
    }()

    static func mark(_ name: String) {
        guard isEnabled else { return }
        write("mark 0 \(name)")
    }

    static func startIfEnabled() {
        guard isEnabled else { return }
        record("launch", since: processStart)
        let thread = Thread {
            while true {
                let ping = DispatchSemaphore(value: 0)
                let start = CACurrentMediaTime()
                DispatchQueue.main.async { ping.signal() }
                ping.wait()
                let ms = (CACurrentMediaTime() - start) * 1000
                if ms >= 100 { write("hang \(Int(ms))") }
                Thread.sleep(forTimeInterval: 0.02)
            }
        }
        thread.qualityOfService = .userInteractive
        thread.start()
    }

    static func record(_ name: String, since start: Date) {
        guard isEnabled else { return }
        write("\(name) \(Int(Date().timeIntervalSince(start) * 1000))")
    }

    private static func write(_ line: String) {
        let text = String(format: "%.3f ", Date().timeIntervalSince1970) + line + "\n"
        queue.async {
            guard let url, let data = text.data(using: .utf8) else { return }
            if let handle = try? FileHandle(forWritingTo: url) {
                handle.seekToEndOfFile()
                handle.write(data)
                try? handle.close()
            } else {
                try? data.write(to: url)
            }
        }
    }
}

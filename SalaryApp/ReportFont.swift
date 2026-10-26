import UIKit

/// خط الكشوف المعتمد؛ تستخدمه الطباعة وقياس الأعمدة حتى تطابق المساحة النص المرسوم.
nonisolated enum ReportFont {
    static func regular(_ size: CGFloat) -> UIFont {
        UIFont(name: "ArialMT", size: size) ?? UIFont.systemFont(ofSize: size)
    }

    static func bold(_ size: CGFloat) -> UIFont {
        UIFont(name: "Arial-BoldMT", size: size) ?? UIFont.boldSystemFont(ofSize: size)
    }
}

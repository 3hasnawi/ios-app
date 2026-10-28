import SwiftUI
import UIKit
import UniformTypeIdentifiers
import LinkPresentation

/// نافذة اختيار الملفات من UIKit مع asCopy = true.
///
/// بديل عن fileImporter في SwiftUI: تلك كانت تعلق أحياناً عند الضغط على "فتح"
/// (خصوصاً مع ملفات كبيرة أو مواقع تخزين خارجية) ولا تُغلق. هنا يُنسخ الملف
/// إلى مجلد التطبيق المؤقت أولاً، فلا حاجة لصلاحيات الوصول المؤقتة.
struct DocumentPicker: UIViewControllerRepresentable {
    let contentTypes: [UTType]
    let onPick: ([URL]) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(onPick: onPick) }

    func makeUIViewController(context: Context) -> UIDocumentPickerViewController {
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: contentTypes, asCopy: true)
        picker.allowsMultipleSelection = true
        picker.shouldShowFileExtensions = true
        picker.delegate = context.coordinator
        return picker
    }

    func updateUIViewController(_ controller: UIDocumentPickerViewController, context: Context) {}

    final class Coordinator: NSObject, UIDocumentPickerDelegate {
        let onPick: ([URL]) -> Void
        init(onPick: @escaping ([URL]) -> Void) { self.onPick = onPick }

        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
            onPick(urls)
        }

        func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
            onPick([])
        }
    }
}

/// يعرض نافذة الملفات مباشرة من UIKit (بدون sheet في SwiftUI)، ويستدعي onPick فور الاختيار.
/// السبب: نافذة الملفات تُغلق نفسها بنفسها، وداخل sheet قد يحدث الإغلاق قبل وصول الملفات،
/// فيضيع الاختيار ويعود المستخدم للشاشة الرئيسية دون أي شيء.
@MainActor
enum FilePicker {
    private static var delegate: Delegate?

    static func present(types: [UTType], multiple: Bool = true, onPick: @escaping ([URL]) -> Void) {
        guard let top = topController() else { return }
        // المجلدات تُفتح في مكانها (للنسخ التلقائي)، والملفات تُنسخ.
        let picker = UIDocumentPickerViewController(forOpeningContentTypes: types, asCopy: !types.contains(.folder))
        picker.allowsMultipleSelection = multiple
        picker.shouldShowFileExtensions = true
        let d = Delegate(onPick: onPick)
        delegate = d
        picker.delegate = d
        top.present(picker, animated: true)
    }

    static func topController() -> UIViewController? {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        let scene = scenes.first { $0.activationState == .foregroundActive } ?? scenes.first
        var top = scene?.windows.first(where: \.isKeyWindow)?.rootViewController ?? scene?.windows.first?.rootViewController
        // لا يُعرض فوق نافذة تُغلق الآن (مثل تنبيه ضُغط زره للتو).
        while let next = top?.presentedViewController, !next.isBeingDismissed { top = next }
        return top
    }

    /// ننتظر اكتمال الإغلاق فعلياً؛ تجاهل النافذة أثناء إغلاقها قد يفتح التالية قبل انتهاء الانتقال.
    static func waitUntilSettled() async {
        for _ in 0..<40 {
            let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            let scene = scenes.first { $0.activationState == .foregroundActive } ?? scenes.first
            var controller = scene?.windows.first(where: \.isKeyWindow)?.rootViewController ?? scene?.windows.first?.rootViewController
            var transitioning = false
            while let current = controller {
                if current.isBeingDismissed || current.isBeingPresented ||
                    current is UIDocumentPickerViewController || current is UIAlertController {
                    transitioning = true
                }
                controller = current.presentedViewController
            }
            if !transitioning { return }
            try? await Task.sleep(for: .milliseconds(100))
        }
    }

    private final class Delegate: NSObject, UIDocumentPickerDelegate {
        let onPick: ([URL]) -> Void
        init(onPick: @escaping ([URL]) -> Void) { self.onPick = onPick }

        func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
            onPick(urls)
            Task { @MainActor in FilePicker.delegate = nil }
        }

        func documentPickerWasCancelled(_ controller: UIDocumentPickerViewController) {
            Task { @MainActor in FilePicker.delegate = nil }
        }
    }
}

/// نافذة المشاركة من UIKit (واتساب، تيليجرام، البريد، حفظ في الملفات...).
///
/// الطريقة الموصى بها من Apple والمجرّبة مع واتساب و"حفظ في الملفات": رابط الملف نفسه (file URL) بلا أي تغليف.
/// (جُرّب سابقاً ShareLink، ثم UIActivityItemSource مع LPLinkMetadata، ثم NSItemProvider بتمثيل ملف؛
///  الأخيرة جعلت الملفات تصل تالفة ولا تُحفظ على iOS 27.)
/// الملف يُنسخ أولاً إلى مجلد مؤقت خاص بهذه المشاركة حتى لا يُستبدل أثناء إرساله، و"نسخ" يضع محتوى الملف في الحافظة.
@MainActor
enum ActivityShare {
    static func present(_ urls: [URL], onDone: ((Bool) -> Void)? = nil) {
        guard let top = FilePicker.topController() else { onDone?(false); return }
        let files = urls.map(stableCopy)
        let controller = UIActivityViewController(activityItems: files, applicationActivities: nil)
        controller.completionWithItemsHandler = { activity, completed, _, error in
            if completed, activity == .copyToPasteboard { copyToPasteboard(files) }
            #if targetEnvironment(simulator)
            if UserDefaults.standard.bool(forKey: "shareTest") {
                let types = UIPasteboard.general.types.joined(separator: ",")
                let bytes = UIPasteboard.general.items.first?.values.compactMap { ($0 as? Data)?.count }.first ?? 0
                LaunchOptions.writeReport("share activity=\(activity?.rawValue ?? "none") completed=\(completed) error=\(error?.localizedDescription ?? "none") pasteboard=\(types) bytes=\(bytes) file=\(files.first?.lastPathComponent ?? "")")
            }
            #endif
            onDone?(completed)
        }
        if let popover = controller.popoverPresentationController {
            popover.sourceView = top.view
            popover.sourceRect = CGRect(x: top.view.bounds.midX, y: top.view.bounds.maxY - 80, width: 1, height: 1)
        }
        top.present(controller, animated: true)
    }

    /// يضع محتوى الملف نفسه في الحافظة فوراً (وليس رابطاً يُقرأ لاحقاً من التطبيق)،
    /// فيبقى اللصق ممكناً في واتساب أو الملفات أو البريد حتى لو أُغلق التطبيق أو بقي في الخلفية.
    static func copyToPasteboard(_ files: [URL]) {
        let items: [[String: Any]] = files.compactMap { url in
            guard let data = try? Data(contentsOf: url) else { return nil }
            let type = UTType(filenameExtension: url.pathExtension) ?? .data
            return [type.identifier: data]
        }
        guard !items.isEmpty else { return }
        UIPasteboard.general.setItems(items, options: [.localOnly: false])
    }

    /// tmp/Sharing/<معرّف>/<اسم الملف>: نسخة مستقلة لكل مشاركة؛ النسخ القديمة تُحذف عند المشاركة التالية.
    private static func stableCopy(_ url: URL) -> URL {
        let fm = FileManager.default
        let root = fm.temporaryDirectory.appendingPathComponent("Sharing", isDirectory: true)
        // نسخ المشاركات السابقة (أقدم من ساعة) لم تعد تلزم.
        if let old = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: [.creationDateKey]) {
            for dir in old where ((try? dir.resourceValues(forKeys: [.creationDateKey]).creationDate) ?? .distantPast) < Date().addingTimeInterval(-3600) {
                try? fm.removeItem(at: dir)
            }
        }
        let folder = root.appendingPathComponent(UUID().uuidString, isDirectory: true)
        do {
            try fm.createDirectory(at: folder, withIntermediateDirectories: true)
            let target = folder.appendingPathComponent(url.lastPathComponent)
            try fm.copyItem(at: url, to: target)
            return target
        } catch {
            return url
        }
    }
}

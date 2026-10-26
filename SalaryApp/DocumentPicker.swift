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

    /// ينتظر انتهاء إغلاق نافذة الملفات قبل عرض نافذة أخرى (حد أقصى ثانيتان).
    static func waitUntilSettled() async {
        for _ in 0..<20 {
            guard let top = topController() else { return }
            if !(top is UIDocumentPickerViewController) && !(top is UIAlertController) && !top.isBeingDismissed && !top.isBeingPresented { return }
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
/// الملف يُقدَّم كـ"ملف" بنوعه (PDF / Excel / صورة) عبر NSItemProvider: عند الإرسال ينسخ النظام الملف نفسه
/// إلى التطبيق المستلم، بدل أن يقرأ المستلم رابطاً داخل مجلد هذا التطبيق (كان واتساب يفشل عند الإرسال).
/// و"نسخ" يضع محتوى الملف في الحافظة فوراً.
@MainActor
enum ActivityShare {
    static func present(_ urls: [URL], onDone: ((Bool) -> Void)? = nil) {
        guard let top = FilePicker.topController() else { onDone?(false); return }
        let files = urls.map(stableCopy)
        let configuration = UIActivityItemsConfiguration(itemProviders: files.map(provider))
        configuration.metadataProvider = { key in
            key == .title ? files.map(\.lastPathComponent).joined(separator: "، ") : nil
        }
        configuration.perItemMetadataProvider = { index, key in
            guard files.indices.contains(index) else { return nil }
            switch key {
            case .title: return files[index].lastPathComponent
            case .linkPresentationMetadata:
                let metadata = LPLinkMetadata()
                metadata.title = files[index].lastPathComponent
                metadata.iconProvider = NSItemProvider(contentsOf: files[index])
                return metadata
            default: return nil
            }
        }
        let controller = UIActivityViewController(activityItemsConfiguration: configuration)
        controller.completionWithItemsHandler = { activity, completed, _, _ in
            if completed, activity == .copyToPasteboard { copyToPasteboard(files) }
            onDone?(completed)
        }
        if let popover = controller.popoverPresentationController {
            popover.sourceView = top.view
            popover.sourceRect = CGRect(x: top.view.bounds.midX, y: top.view.bounds.maxY - 80, width: 1, height: 1)
        }
        top.present(controller, animated: true)
    }

    /// الملف بنوعه واسمه؛ المستلم يأخذ نسخة منه (لا يحتاج صلاحية على مجلد التطبيق).
    private static func provider(_ url: URL) -> NSItemProvider {
        let type = UTType(filenameExtension: url.pathExtension) ?? .data
        let provider = NSItemProvider()
        provider.suggestedName = url.deletingPathExtension().lastPathComponent
        provider.registerFileRepresentation(for: type, visibility: .all, openInPlace: false) { completion in
            completion(url, false, nil)
            return nil
        }
        return provider
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

    /// Library/Caches/مشاركة/<اسم الملف>: يبقى حتى تكتمل المشاركة، ويُستبدل في المرة القادمة.
    private static func stableCopy(_ url: URL) -> URL {
        let fm = FileManager.default
        guard let caches = fm.urls(for: .cachesDirectory, in: .userDomainMask).first else { return url }
        let folder = caches.appendingPathComponent("Shared", isDirectory: true)
        try? fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = folder.appendingPathComponent(url.lastPathComponent)
        guard target.standardizedFileURL != url.standardizedFileURL else { return url }
        try? fm.removeItem(at: target)
        do { try fm.copyItem(at: url, to: target); return target } catch { return url }
    }
}

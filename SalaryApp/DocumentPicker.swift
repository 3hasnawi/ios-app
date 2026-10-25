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
        while let next = top?.presentedViewController { top = next }
        return top
    }

    /// ينتظر انتهاء إغلاق نافذة الملفات قبل عرض نافذة أخرى (حد أقصى ثانيتان).
    static func waitUntilSettled() async {
        for _ in 0..<20 {
            guard let top = topController() else { return }
            if !(top is UIDocumentPickerViewController) && !top.isBeingDismissed && !top.isBeingPresented { return }
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

/// نافذة المشاركة من UIKit (Google Drive، OneDrive، واتساب، حفظ في الملفات...).
/// الملف يُنسخ أولاً إلى مجلد ثابت (لا يُحذف أثناء المشاركة)، ويُعرَّف بنوعه (PDF، Excel...) حتى تقبله
/// التطبيقات مثل واتساب، و"نسخ" يضع الملف نفسه في الحافظة ليُلصق في أي تطبيق.
@MainActor
enum ActivityShare {
    static func present(_ urls: [URL], onDone: ((Bool) -> Void)? = nil) {
        guard let top = FilePicker.topController() else { onDone?(false); return }
        let files = urls.map(stableCopy)
        let controller = UIActivityViewController(activityItems: files.map(FileShareItem.init), applicationActivities: nil)
        controller.completionWithItemsHandler = { activity, completed, _, _ in
            if completed, activity == .copyToPasteboard {
                UIPasteboard.general.setItemProviders(files.compactMap { NSItemProvider(contentsOf: $0) }, localOnly: false, expirationDate: nil)
            }
            onDone?(completed)
        }
        if let popover = controller.popoverPresentationController {
            popover.sourceView = top.view
            popover.sourceRect = CGRect(x: top.view.bounds.midX, y: top.view.bounds.maxY - 80, width: 1, height: 1)
        }
        top.present(controller, animated: true)
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

/// ملف للمشاركة بنوعه الصحيح واسمه ومعاينته.
final class FileShareItem: NSObject, UIActivityItemSource {
    let url: URL
    init(_ url: URL) { self.url = url }

    func activityViewControllerPlaceholderItem(_ controller: UIActivityViewController) -> Any { url }

    func activityViewController(_ controller: UIActivityViewController, itemForActivityType activityType: UIActivity.ActivityType?) -> Any? { url }

    func activityViewController(_ controller: UIActivityViewController, dataTypeIdentifierForActivityType activityType: UIActivity.ActivityType?) -> String {
        (UTType(filenameExtension: url.pathExtension) ?? .data).identifier
    }

    func activityViewController(_ controller: UIActivityViewController, subjectForActivityType activityType: UIActivity.ActivityType?) -> String {
        url.deletingPathExtension().lastPathComponent
    }

    func activityViewControllerLinkMetadata(_ controller: UIActivityViewController) -> LPLinkMetadata? {
        let metadata = LPLinkMetadata()
        metadata.title = url.lastPathComponent
        metadata.originalURL = url
        metadata.url = url
        metadata.iconProvider = NSItemProvider(contentsOf: url)
        return metadata
    }
}

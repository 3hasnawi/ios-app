import SwiftUI
import UIKit
import UniformTypeIdentifiers

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

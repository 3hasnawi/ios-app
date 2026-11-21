import SwiftUI
import UIKit

/// أثناء الكتابة يحتفظ حقل النظام بالنص والمؤشر، حتى عند تحديث صفوف الدفعة.
struct RefundAmountField: UIViewRepresentable {
    let title: String
    let identifier: String
    @Binding var text: String
    var editingChanged: (Bool) -> Void = { _ in }
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.layoutDirection) private var direction

    init(_ title: String, identifier: String, text: Binding<String>, editingChanged: @escaping (Bool) -> Void = { _ in }) {
        self.title = title
        self.identifier = identifier
        _text = text
        self.editingChanged = editingChanged
    }

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeUIView(context: Context) -> UITextField {
        let field = UITextField()
        field.keyboardType = .numberPad
        field.borderStyle = .roundedRect
        field.textColor = .label
        field.font = .preferredFont(forTextStyle: .body)
        field.adjustsFontForContentSizeCategory = true
        field.setContentHuggingPriority(.defaultLow, for: .horizontal)
        field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        field.delegate = context.coordinator
        field.addTarget(context.coordinator, action: #selector(Coordinator.changed(_:)), for: .editingChanged)
        let toolbar = UIToolbar()
        let done = UIBarButtonItem(title: tr("تم", "Done"), style: .done, target: field, action: #selector(UIResponder.resignFirstResponder))
        done.accessibilityIdentifier = "refundAmountDone"
        toolbar.items = [.flexibleSpace(), done]
        toolbar.sizeToFit()
        field.inputAccessoryView = toolbar
        return field
    }

    func updateUIView(_ field: UITextField, context: Context) {
        context.coordinator.parent = self
        field.isEnabled = isEnabled
        field.textAlignment = direction == .rightToLeft ? .right : .left
        field.placeholder = title
        field.accessibilityLabel = title
        field.accessibilityIdentifier = identifier
        if !field.isFirstResponder && field.text != text { field.text = text }
    }

    final class Coordinator: NSObject, UITextFieldDelegate {
        var parent: RefundAmountField
        init(_ parent: RefundAmountField) { self.parent = parent }

        @objc func changed(_ field: UITextField) { parent.text = field.text ?? "" }
        func textFieldDidBeginEditing(_ textField: UITextField) { parent.editingChanged(true) }
        func textFieldDidEndEditing(_ textField: UITextField) { parent.editingChanged(false) }
    }
}

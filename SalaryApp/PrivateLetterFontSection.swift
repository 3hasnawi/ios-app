import SwiftUI
import UniformTypeIdentifiers

/// نفس إعداد الخط الخاص متاح من محرر كل قالب، ويحفظ في النسخة الاحتياطية الحالية.
struct PrivateLetterFontSection: View {
    @AppStorage(TransferHeaderFont.boldSettingsKey) private var bold = Data()
    @AppStorage(TransferHeaderFont.regularSettingsKey) private var regular = Data()
    @State private var importing = false
    @State private var error: String?
    private var busy: Bool { importing || BackupManager.shared.isWorking || ImportCenter.shared.isBusy || StaffImportState.shared.isImporting }

    var body: some View {
        Section {
            Button { importFont(.bold) } label: {
                Label(bold.isEmpty ? tr("استيراد الخط العريض", "Import bold font") : tr("استبدال الخط العريض", "Replace bold font"), systemImage: "textformat")
            }.disabled(busy)
            Button { importFont(.regular) } label: {
                Label(regular.isEmpty ? tr("استيراد الخط العادي", "Import regular font") : tr("استبدال الخط العادي", "Replace regular font"), systemImage: "textformat")
            }.disabled(busy)
            if importing { ProgressView() }
        } header: { Text(tr("خط الترويسة الخاص", "Private header font")) } footer: {
            Text(tr("يُحفظ الخط ضمن النسخة الاحتياطية ويُستخدم في الكتب التي تحتاجه.", "The font is kept in the backup and used by letters that require it."))
        }
        .alert(tr("تعذّر استيراد الخط", "Could not import font"), isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) { error = nil }
        } message: { Text(error ?? "") }
    }
    private func importFont(_ style: TransferHeaderFont.Style) {
        guard !busy else { return }
        FilePicker.present(types: [UTType(filenameExtension: "ttf") ?? .data], multiple: false) { urls in
            guard let url = urls.first else { return }
            importing = true
            Task {
                defer { importing = false }
                do {
                    let bytes = try await Task.detached(priority: .userInitiated) {
                        let size = try url.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
                        guard size > 0, size <= 3_000_000 else { throw TransferHeaderFont.Failure.invalidFont }
                        let data = try Data(contentsOf: url)
                        _ = try TransferHeaderFont.make(data: data, style: style, size: 18)
                        return data
                    }.value
                    guard !BackupManager.shared.isWorking, !ImportCenter.shared.isBusy, !StaffImportState.shared.isImporting else {
                        throw CustomTransferTemplate.invalid(tr("انتظر انتهاء العملية الحالية ثم أعد اختيار الخط.", "Wait for the current operation, then select the font again."))
                    }
                    if style == .bold { bold = bytes } else { regular = bytes }
                    BackupManager.shared.markChanged()
                } catch { self.error = error.localizedDescription }
            }
        }
    }
}

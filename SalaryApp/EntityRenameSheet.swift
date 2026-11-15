import SwiftUI

struct EntityRenameSheet: View {
    let source: String
    var onSaved: (EntityRenameResult) async -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var destination = ""
    @State private var names: [DeductionEntityName] = []
    @State private var ready = false
    @State private var saving = false
    @State private var confirm = false
    @State private var error: String?

    private var proposed: String { destination.trimmingCharacters(in: .whitespacesAndNewlines) }
    private var matching: [DeductionEntityName] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(destination)).split(separator: " ").map(String.init)
        return names.filter { item in
            item.title != source && tokens.allSatisfy { ArabicText.normalize(item.title).contains($0) }
        }
    }
    private var existing: DeductionEntityName? { names.first { $0.title == proposed && $0.title != source } }
    private var sourceMonths: Int { names.first { $0.title == source }?.months ?? 0 }
    private var blocked: Bool {
        saving || BackupManager.shared.isWorking || ImportCenter.shared.isBusy || StaffImportState.shared.isImporting
    }

    var body: some View {
        NavigationStack {
            Form {
                Section(tr("التسمية الحالية", "Current name")) {
                    Text(source).fontWeight(.semibold)
                    Text(tr("موجودة في \(Num.format(sourceMonths)) شهر. يشمل التعديل كل الأشهر المحفوظة.",
                            "Present in \(sourceMonths) months. The edit applies to all saved months."))
                        .font(.footnote).foregroundStyle(.secondary)
                }
                Section {
                    TextField(tr("اكتب الاسم أو ابحث عن جهة محفوظة", "Enter a name or find a saved entity"), text: $destination)
                        .accessibilityIdentifier("entityRenameField")
                        .autocorrectionDisabled()
                        .submitLabel(.done)
                        .onSubmit { if ready && !blocked && !proposed.isEmpty && proposed != source { confirm = true } }
                } header: { Text(tr("التسمية الموحّدة", "Unified name")) }
                footer: {
                    Text(tr("اختر الاسم المعتمد في الأشهر السابقة لتجميع سجلات الجهة تحته. تبقى سجلات كل شهر ومبالغها دون حذف.",
                            "Choose the name used in earlier months to unify the entity. Each month's records and amounts are preserved."))
                }
                Section(tr("الجهات المحفوظة — اضغط للاختيار", "Saved entities — tap to choose")) {
                    if !ready { ProgressView() }
                    else if matching.isEmpty { Text(tr("لا يوجد اسم مطابق؛ يمكنك حفظ التسمية المكتوبة.", "No name matches. You can save the entered name.")).foregroundStyle(.secondary) }
                    ForEach(matching) { item in
                        Button { destination = item.title } label: {
                            HStack {
                                VStack(alignment: .leading, spacing: 4) {
                                    Text(item.title).foregroundStyle(.primary)
                                    Text(tr("\(Num.format(item.months)) شهر", "\(item.months) months")).font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                if proposed == item.title { Image(systemName: "checkmark.circle.fill").foregroundStyle(.tint) }
                            }
                        }
                        .accessibilityIdentifier("entityRenameChoice")
                    }
                }
                if let error { Section { Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.red) } }
                if saving { Section { ProgressView(tr("جارٍ توحيد التسمية وحفظ الروابط…", "Unifying the name and saving links…")) } }
            }
            .disabled(saving)
            .navigationTitle(tr("تعديل تسمية الجهة", "Rename entity"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(tr("إلغاء", "Cancel")) { dismiss() }.disabled(saving)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(tr("حفظ", "Save")) { confirm = true }
                        .disabled(!ready || blocked || proposed.isEmpty || proposed == source)
                        .accessibilityIdentifier("saveEntityRename")
                }
            }
            .interactiveDismissDisabled(saving)
            .confirmationDialog(tr("تأكيد توحيد التسمية", "Confirm unified name"), isPresented: $confirm, titleVisibility: .visible) {
                Button(tr("توحيد التسمية لكل الأشهر", "Unify the name for all months")) { save() }
                Button(tr("إلغاء", "Cancel"), role: .cancel) {}
            } message: {
                Text(existing != nil
                     ? tr("ستُنقل سجلات «\(source)» إلى «\(proposed)» في جميع الأشهر، وتُجمع مع سجلات الجهة الموجودة دون حذف أي قيد أو تغيير أي مبلغ.",
                          "All records named ‘\(source)’ will use ‘\(proposed)’ across all months, alongside the existing entity's records. No records or amounts will be removed or changed.")
                     : tr("ستتغير تسمية «\(source)» إلى «\(proposed)» في جميع الأشهر المحفوظة دون تغيير المبالغ.",
                          "Rename ‘\(source)’ to ‘\(proposed)’ across all saved months without changing amounts."))
            }
            .task {
                do {
                    names = try await DeductionEntityEditor(modelContainer: SharedModel.container).names()
                    ready = true
                } catch is CancellationError { }
                catch { self.error = error.localizedDescription }
            }
        }
    }

    private func save() {
        guard ready, !blocked else { return }
        let name: String
        do { name = try EntityRenamePlan.validatedName(destination) }
        catch { self.error = error.localizedDescription; return }
        saving = true
        error = nil
        // نفس قفل الاستيراد: يمنع النسخ والاسترجاع واستيراد ملف أثناء تغيير البيانات وروابطها.
        ImportCenter.shared.isBusy = true
        let container = SharedModel.container
        Task {
            defer { ImportCenter.shared.isBusy = false; saving = false }
            do {
                let result = try await DeductionEntityEditor(modelContainer: container).rename(source: source, destination: name)
                await EntityRenameEffects.refresh()
                await onSaved(result)
                dismiss()
            } catch { self.error = error.localizedDescription }
        }
    }
}

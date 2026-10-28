import SwiftUI
import SwiftData
import VisionKit

struct ScannedBookDraft: Identifiable {
    let id = UUID()
    let images: [Data]
}

/// أرشيف الكتب المرتبطة بالأشهر؛ البحث بالاسم أو الشهر أو الجهة المرتبطة.
struct OfficialBooksView: View {
    @Query(sort: [SortDescriptor(\PayrollMonth.sortKey, order: .reverse), SortDescriptor(\PayrollMonth.importedAt, order: .reverse)])
    private var months: [PayrollMonth]
    @State private var store = OfficialBookStore.shared
    @State private var selectedMonth = ""
    @State private var search = ""
    @State private var showingScanner = false
    @State private var draft: ScannedBookDraft?
    @State private var editing: OfficialBook?
    @State private var deleting: OfficialBook?
    @State private var error: String?

    private var monthNames: [String] { months.map(\.name) }
    private var visible: [OfficialBook] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        return store.books.filter { book in
            if tokens.isEmpty { return selectedMonth.isEmpty || book.month == selectedMonth }
            let text = ArabicText.normalize([book.title, book.month, book.entity ?? ""].joined(separator: " "))
            return tokens.allSatisfy { text.contains($0) }
        }
    }

    var body: some View {
        List {
            Section {
                HStack {
                    Text(tr("الشهر", "Month"))
                    Spacer()
                    Menu {
                        Button(tr("كل الأشهر", "All months")) { selectedMonth = "" }
                        ForEach(monthNames, id: \.self) { month in
                            Button(month) { selectedMonth = month }
                        }
                    } label: {
                        Text(selectedMonth.isEmpty ? tr("كل الأشهر", "All months") : selectedMonth)
                    }
                }
                Button {
                    if VNDocumentCameraViewController.isSupported { showingScanner = true }
                    else { error = OfficialBookError.unavailableCamera.localizedDescription }
                } label: {
                    Label(tr("مسح كتاب جديد", "Scan a document"), systemImage: "doc.viewfinder")
                }
                .disabled(monthNames.isEmpty)
                .accessibilityIdentifier("scanOfficialBook")
            } footer: {
                Text(tr("يحدد الماسح حواف الورقة ويلتقطها تلقائيًا. صوّر الكتاب وإشعاره ضمن ١–٣ صفحات، ثم راجع الصفحات وعدّلها قبل الحفظ.",
                        "The scanner detects edges and captures automatically. Scan the letter and its notice as 1–3 pages, then review and edit before saving."))
            }

            Section(tr("الكتب المحفوظة", "Saved documents")) {
                if let loadError = store.loadError {
                    Text(loadError).foregroundStyle(.red)
                } else if visible.isEmpty {
                    ContentUnavailableView(search.isEmpty ? tr("لا توجد كتب لهذا الشهر", "No documents for this month")
                                                          : tr("لا توجد نتائج", "No results"), systemImage: "doc.text.magnifyingglass")
                        .listRowBackground(Color.clear)
                }
                ForEach(visible) { book in
                    NavigationLink {
                        OfficialBookDetailView(id: book.id)
                    } label: {
                        HStack(spacing: 12) {
                            Image(systemName: "doc.richtext")
                                .font(.title3).foregroundStyle(.teal)
                                .frame(width: 38, height: 38)
                                .background(.teal.opacity(0.10), in: RoundedRectangle(cornerRadius: 10))
                            VStack(alignment: .leading, spacing: 4) {
                                Text(book.title).font(.body.weight(.semibold)).lineLimit(2)
                                Text("\(book.month) · \(tr("\(Num.format(book.pages)) صفحات", "\(book.pages) pages"))")
                                    .font(.caption).foregroundStyle(.secondary)
                                if let entity = book.entity { Text(entity).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
                            }
                        }
                    }
                    .contextMenu {
                        Button(tr("مشاركة PDF", "Share PDF"), systemImage: "square.and.arrow.up") {
                            do { try OfficialBookShare.present(book) } catch { self.error = error.localizedDescription }
                        }
                        Button(tr("تعديل الاسم والجهة", "Edit name and entity"), systemImage: "pencil") { editing = book }
                        Button(tr("حذف الكتاب", "Delete document"), systemImage: "trash", role: .destructive) { deleting = book }
                    }
                }
            }
        }
        .searchable(text: $search, prompt: tr("ابحث عن كتاب أو جهة أو شهر", "Search documents, entities or months"))
        .navigationTitle(tr("الكتب الرسمية", "Official documents"))
        .navigationBarTitleDisplayMode(.inline)
        .task {
            guard selectedMonth.isEmpty else { return }
            let current = UserDefaults.standard.string(forKey: "selectedMonth") ?? ""
            selectedMonth = monthNames.contains(current) ? current : monthNames.first ?? ""
        }
        .sheet(isPresented: $showingScanner) {
            OfficialBookScanner(onComplete: { images in
                showingScanner = false
                Task { @MainActor in
                    try? await Task.sleep(for: .milliseconds(250))
                    draft = ScannedBookDraft(images: images)
                }
            }, onFailure: { message in
                showingScanner = false
                error = message
            }, onCancel: { showingScanner = false })
        }
        .sheet(item: $draft) { draft in
            OfficialBookEditor(months: monthNames, initialMonth: selectedMonth.isEmpty ? monthNames.first ?? "" : selectedMonth,
                               images: draft.images, existing: nil)
        }
        .sheet(item: $editing) { book in
            OfficialBookEditor(months: monthNames, initialMonth: book.month, images: nil, existing: book)
        }
        .confirmationDialog(tr("حذف الكتاب؟", "Delete document?"), isPresented: Binding(
            get: { deleting != nil }, set: { if !$0 { deleting = nil } }), titleVisibility: .visible) {
            Button(tr("حذف نهائي", "Delete permanently"), role: .destructive) {
                if let deleting {
                    do { try store.delete(deleting) } catch { self.error = error.localizedDescription }
                    self.deleting = nil
                }
            }
        } message: {
            Text(tr("سيُحذف ملف PDF من هذا الجهاز ومن النسخة الاحتياطية التالية.",
                    "The PDF will be removed from this device and the next backup."))
        }
        .alert(tr("الكتب الرسمية", "Official documents"), isPresented: Binding(
            get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
    }
}

/// تأكيد اسم الكتاب وشهره بعد المسح، أو تعديل الاسم والجهة دون تغيير الملف.
struct OfficialBookEditor: View {
    let months: [String]
    let existing: OfficialBook?
    @State private var images: [Data]?
    @State private var title: String
    @State private var month: String
    @State private var entity: String
    @State private var saving = false
    @State private var rotatingPage: Int?
    @State private var retakePage: Int?
    @State private var showingRetake = false
    @State private var error: String?
    @Environment(\.dismiss) private var dismiss
    @State private var store = OfficialBookStore.shared

    init(months: [String], initialMonth: String, images: [Data]?, existing: OfficialBook?) {
        self.months = months
        self.existing = existing
        _images = State(initialValue: images)
        _title = State(initialValue: existing?.title ?? "")
        _month = State(initialValue: initialMonth)
        _entity = State(initialValue: existing?.entity ?? "")
    }

    var body: some View {
        NavigationStack {
            Form {
                Section(tr("بيانات الكتاب", "Document details")) {
                    TextField(tr("اسم الكتاب", "Document name"), text: $title)
                        .textInputAutocapitalization(.sentences)
                    if existing == nil {
                        Picker(tr("الشهر", "Month"), selection: $month) {
                            ForEach(months, id: \.self) { Text($0).tag($0) }
                        }
                        LabeledContent(tr("عدد الصفحات", "Pages"), value: Num.format(images?.count ?? 0))
                    } else {
                        LabeledContent(tr("الشهر", "Month"), value: month)
                    }
                    TextField(tr("جهة التنفيذ المرتبطة (اختياري)", "Related entity (optional)"), text: $entity)
                }
                if let images {
                    Section {
                        ForEach(images.indices, id: \.self) { index in
                            HStack(spacing: 12) {
                                if let image = UIImage(data: images[index]) {
                                    Image(uiImage: image)
                                        .resizable().scaledToFit()
                                        .frame(width: 72, height: 92)
                                        .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 8))
                                        .accessibilityHidden(true)
                                }
                                VStack(alignment: .leading, spacing: 10) {
                                    Text(tr("الصفحة \(Num.format(index + 1))", "Page \(index + 1)"))
                                        .font(.subheadline.weight(.semibold))
                                    HStack(spacing: 18) {
                                        Button(tr("تدوير", "Rotate"), systemImage: "rotate.right") { rotate(index) }
                                            .disabled(rotatingPage != nil || saving)
                                        Button(tr("إعادة التصوير", "Retake"), systemImage: "camera") {
                                            retakePage = index
                                            showingRetake = true
                                        }
                                        .disabled(rotatingPage != nil || saving)
                                    }
                                    .font(.caption)
                                    .buttonStyle(.borderless)
                                }
                                Spacer(minLength: 0)
                            }
                            .padding(.vertical, 5)
                        }
                    } header: {
                        Text(tr("مراجعة الصفحات", "Review pages"))
                    } footer: {
                        Text(tr("يمكن تعديل القصّ من معاينة الماسح، أو تدوير أي صفحة وإعادة تصويرها هنا. الإشعار يبقى صفحة من الكتاب نفسه.",
                                "Adjust cropping in the scanner preview, or rotate and retake a page here. The notice stays a page of the same document."))
                    }
                }
                Section {
                    Text(tr("الاسم والشهر يظهران في البحث. الجهة المرتبطة تُحفظ لربط الكتاب بكشفها لاحقًا.",
                            "The name and month appear in search. The related entity is saved for future report links."))
                        .font(.footnote).foregroundStyle(.secondary)
                }
            }
            .navigationTitle(existing == nil ? tr("حفظ الكتاب", "Save document") : tr("تعديل الكتاب", "Edit document"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(tr("إلغاء", "Cancel")) { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(tr("حفظ", "Save")) { save() }
                        .disabled(saving || rotatingPage != nil || title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || month.isEmpty)
                }
            }
            .sheet(isPresented: $showingRetake) {
                OfficialBookScanner(maxPages: 1, onComplete: { replacement in
                    showingRetake = false
                    if let index = retakePage, let page = replacement.first, images?.indices.contains(index) == true {
                        images?[index] = page
                    }
                    retakePage = nil
                }, onFailure: { message in
                    showingRetake = false
                    retakePage = nil
                    error = message
                }, onCancel: {
                    showingRetake = false
                    retakePage = nil
                })
            }
            .alert(tr("تعذّر الحفظ", "Could not save"), isPresented: Binding(
                get: { error != nil }, set: { if !$0 { error = nil } })) {
                Button(tr("حسناً", "OK"), role: .cancel) {}
            } message: { Text(error ?? "") }
        }
    }

    private func rotate(_ index: Int) {
        guard let data = images?[index] else { return }
        rotatingPage = index
        Task {
            do {
                let rotated = try await Task.detached(priority: .userInitiated) {
                    try OfficialBookImage.rotateClockwise(data)
                }.value
                images?[index] = rotated
            } catch {
                self.error = error.localizedDescription
            }
            rotatingPage = nil
        }
    }

    private func save() {
        saving = true
        Task {
            do {
                if let existing { try store.rename(existing, to: title, entity: entity) }
                else if let images { try await store.save(title: title, month: month, entity: entity, images: images) }
                saving = false
                dismiss()
            } catch {
                saving = false
                self.error = error.localizedDescription
            }
        }
    }
}

/// معاينة PDF ومشاركته من ملفه الأصلي عبر طريقة المشاركة المعتمدة في التطبيق.
struct OfficialBookDetailView: View {
    let id: UUID
    @State private var store = OfficialBookStore.shared
    @State private var error: String?

    private var book: OfficialBook? { store.books.first { $0.id == id } }
    private var url: URL { OfficialBookFiles.file(id) }

    var body: some View {
        Group {
            if let book, FileManager.default.fileExists(atPath: url.path) {
                PDFPreview(url: url)
                    .navigationTitle(book.title)
                    .navigationBarTitleDisplayMode(.inline)
                    .toolbar {
                        ToolbarItem(placement: .primaryAction) {
                            Button(tr("مشاركة PDF", "Share PDF"), systemImage: "square.and.arrow.up") {
                                do { try OfficialBookShare.present(book) } catch { self.error = error.localizedDescription }
                            }
                        }
                    }
            } else {
                ContentUnavailableView(tr("ملف الكتاب غير موجود", "Document unavailable"), systemImage: "doc.badge.questionmark")
            }
        }
        .alert(tr("تعذّرت المشاركة", "Could not share"), isPresented: Binding(
            get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
    }
}

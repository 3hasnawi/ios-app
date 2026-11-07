import SwiftUI
import SwiftData
import VisionKit
import UniformTypeIdentifiers

struct ScannedBookDraft: Identifiable {
    let id = UUID()
    let images: [Data]
    var replacing: OfficialBook? = nil
}

/// ملف PDF مختار من «الملفات» قبل تسميته وربطه بشهر.
struct ImportedBookDraft: Identifiable {
    let id = UUID()
    let pdf: Data
    let suggestedTitle: String
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
    @State private var showingImporter = false
    @State private var imported: ImportedBookDraft?
    /// الكتاب الذي يُستبدل ملفه (بالمسح أو من «الملفات») مع إبقاء معرّفه وارتباطاته.
    @State private var replacing: OfficialBook?

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
                Button {
                    replacing = nil
                    showingImporter = true
                } label: {
                    Label(tr("استيراد PDF من الملفات", "Import PDF from Files"), systemImage: "doc.badge.plus")
                }
                .disabled(monthNames.isEmpty)
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
                        Button(tr("تعديل الاسم والشهر والجهة", "Edit name, month and entity"), systemImage: "pencil") { editing = book }
                        Menu(tr("استبدال الملف", "Replace file")) {
                            Button(tr("مسح صفحات جديدة", "Scan new pages"), systemImage: "doc.viewfinder") {
                                if VNDocumentCameraViewController.isSupported { replacing = book; showingScanner = true }
                                else { error = OfficialBookError.unavailableCamera.localizedDescription }
                            }
                            Button(tr("ملف PDF من الملفات", "PDF from Files"), systemImage: "doc.badge.plus") { replacing = book; showingImporter = true }
                        }
                        Button(tr("حذف الكتاب", "Delete document"), systemImage: "trash", role: .destructive) { deleting = book }
                    }
                }
            }
        }
        .searchable(text: $search, prompt: tr("ابحث عن كتاب أو جهة أو شهر", "Search documents, entities or months"))
        .navigationTitle(tr("الأرشيف", "Archive"))
        .navigationBarTitleDisplayMode(.inline)
        .task {
            guard selectedMonth.isEmpty else { return }
            let current = UserDefaults.standard.string(forKey: "selectedMonth") ?? ""
            selectedMonth = monthNames.contains(current) ? current : monthNames.first ?? ""
        }
        .sheet(isPresented: $showingScanner) {
            OfficialBookScanner(onComplete: { images in
                showingScanner = false
                let target = replacing
                replacing = nil
                Task { @MainActor in
                    try? await Task.sleep(for: .milliseconds(250))
                    draft = ScannedBookDraft(images: images, replacing: target)
                }
            }, onFailure: { message in
                showingScanner = false
                replacing = nil
                error = message
            }, onCancel: { showingScanner = false; replacing = nil })
        }
        .fileImporter(isPresented: $showingImporter, allowedContentTypes: [.pdf]) { result in
            let target = replacing
            replacing = nil
            do {
                let url = try result.get()
                let access = url.startAccessingSecurityScopedResource()
                defer { if access { url.stopAccessingSecurityScopedResource() } }
                let data = try Data(contentsOf: url)
                _ = try OfficialBookPDF.pageCount(data)
                if let target { try store.replaceFile(target, pdf: data) }
                else { imported = ImportedBookDraft(pdf: data, suggestedTitle: Self.meaningfulName(url.deletingPathExtension().lastPathComponent)) }
            } catch { self.error = error.localizedDescription }
        }
        .sheet(item: $imported) { file in
            OfficialBookEditor(months: monthNames, initialMonth: selectedMonth.isEmpty ? monthNames.first ?? "" : selectedMonth,
                               images: nil, existing: nil, pdf: file.pdf, suggestedTitle: file.suggestedTitle)
        }
        .sheet(item: $draft) { draft in
            OfficialBookEditor(months: draft.replacing.map { book in monthNames.contains(book.month) ? monthNames : [book.month] + monthNames } ?? monthNames,
                               initialMonth: draft.replacing?.month ?? (selectedMonth.isEmpty ? monthNames.first ?? "" : selectedMonth),
                               images: draft.images, existing: draft.replacing)
        }
        .sheet(item: $editing) { book in
            OfficialBookEditor(months: monthNames.contains(book.month) ? monthNames : [book.month] + monthNames, initialMonth: book.month, images: nil, existing: book)
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
        .alert(tr("الأرشيف", "Archive"), isPresented: Binding(
            get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button(tr("حسناً", "OK"), role: .cancel) {}
        } message: { Text(error ?? "") }
    }

    /// أسماء ملفات الماسح والكاميرا (Scan_0001، IMG_1234…) ليست اسماً للكتاب؛ يُطلب من المستخدم كتابة اسم.
    static func meaningfulName(_ name: String) -> String {
        let value = name.replacingOccurrences(of: "_", with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        let lower = value.lowercased()
        let generic = ["scan", "scanned", "img", "image", "doc", "document", "page", "pdf", "untitled", "photo", "dsc", "whatsapp", "screenshot", "مستند", "صورة", "مسح"]
        if generic.contains(where: { lower.hasPrefix($0) }) && value.filter(\.isLetter).count <= 12 { return "" }
        return value.filter(\.isLetter).count >= 3 ? value : ""
    }
}

/// تأكيد اسم الكتاب وشهره بعد المسح، أو تعديل الاسم والجهة دون تغيير الملف.
struct OfficialBookEditor: View {
    let months: [String]
    let existing: OfficialBook?
    private let onSaved: ((OfficialBook) -> Void)?
    @State private var images: [Data]?
    @State private var title: String
    @State private var month: String
    @State private var entity: String
    @State private var saving = false
    @State private var rotatingPage: Int?
    @State private var retakePage: Int?
    @State private var showingRetake = false
    @State private var scanMode: OfficialBookScanEnhancement.Mode = .gentle
    @State private var scanQuality: OfficialBookScanEnhancement.Quality = .high
    @State private var reviewPage: Int?
    @State private var error: String?
    @Environment(\.dismiss) private var dismiss
    @State private var store = OfficialBookStore.shared

    /// ملف PDF من «الملفات» بدل صور الماسح.
    private let pdf: Data?
    init(months: [String], initialMonth: String, images: [Data]?, existing: OfficialBook?, pdf: Data? = nil, suggestedTitle: String = "", onSaved: ((OfficialBook) -> Void)? = nil) {
        self.months = months
        self.existing = existing
        self.onSaved = onSaved
        self.pdf = pdf
        _images = State(initialValue: images)
        _title = State(initialValue: existing?.title ?? suggestedTitle)
        _month = State(initialValue: initialMonth)
        _entity = State(initialValue: existing?.entity ?? "")
    }

    var body: some View {
        NavigationStack {
            Form {
                Section(tr("بيانات الكتاب", "Document details")) {
                    TextField(tr("اسم الكتاب", "Document name"), text: $title)
                        .textInputAutocapitalization(.sentences)
                    Picker(tr("الشهر", "Month"), selection: $month) {
                        ForEach(months, id: \.self) { Text($0).tag($0) }
                    }
                    if existing == nil {
                        LabeledContent(tr("عدد الصفحات", "Pages"), value: Num.format(pdf.flatMap { try? OfficialBookPDF.pageCount($0) } ?? images?.count ?? 0))
                    }
                    TextField(tr("جهة التنفيذ المرتبطة (اختياري)", "Related entity (optional)"), text: $entity)
                }
                .disabled(existing != nil && images != nil)
                if let images {
                    Section {
                        Picker(tr("التحسين", "Enhancement"), selection: $scanMode) {
                            ForEach(OfficialBookScanEnhancement.Mode.allCases) { mode in Text(mode.title).tag(mode) }
                        }
                        Picker(tr("جودة الملف", "File quality"), selection: $scanQuality) {
                            ForEach(OfficialBookScanEnhancement.Quality.allCases) { quality in Text(quality.title).tag(quality) }
                        }
                        Text(tr("يُطبّق الاختيار على جميع الصفحات. التحسين الخفيف يحافظ على الألوان؛ راجع الأختام والتواقيع ويمكنك اختيار الأصل قبل الحفظ.", "The selected option applies to all pages. Gentle enhancement keeps colors; review stamps and signatures. You can choose the original before saving."))
                            .font(.footnote).foregroundStyle(.secondary)
                    } header: { Text(tr("تحسين المسح", "Scan enhancement")) }
                    .disabled(saving || rotatingPage != nil)
                    Section {
                        ForEach(images.indices, id: \.self) { index in
                            HStack(spacing: 12) {
                                OfficialBookScanThumbnail(data: images[index])
                                    .frame(width: 72, height: 92)
                                    .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 8))
                                    .accessibilityHidden(true)
                                VStack(alignment: .leading, spacing: 10) {
                                    Text(tr("الصفحة \(Num.format(index + 1))", "Page \(index + 1)"))
                                        .font(.subheadline.weight(.semibold))
                                    VStack(alignment: .leading, spacing: 8) {
                                        Button(tr("معاينة", "Preview"), systemImage: "doc.text.magnifyingglass") { reviewPage = index }
                                            .disabled(rotatingPage != nil || saving)
                                        HStack(spacing: 14) {
                                            Button(tr("تدوير", "Rotate"), systemImage: "rotate.right") { rotate(index) }
                                                .disabled(rotatingPage != nil || saving)
                                            Button(tr("إعادة التصوير", "Retake"), systemImage: "camera") {
                                                retakePage = index
                                                showingRetake = true
                                            }
                                            .disabled(rotatingPage != nil || saving)
                                        }
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
            .disabled(saving)
            .navigationTitle(existing == nil ? tr("حفظ الكتاب", "Save document") : (images == nil ? tr("تعديل الكتاب", "Edit document") : tr("استبدال صفحات الكتاب", "Replace document pages")))
            // الكتاب بلا اسم (مسح أو ملف باسم عام): يُكتب اسم يسهل البحث عنه.
            .navigationBarTitleDisplayMode(.inline)
            .interactiveDismissDisabled(saving)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(tr("إلغاء", "Cancel")) { dismiss() }.disabled(saving)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(tr("حفظ", "Save")) { save() }
                        .disabled(saving || rotatingPage != nil || title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || month.isEmpty)
                }
            }
            .sheet(isPresented: Binding(get: { reviewPage != nil }, set: { if !$0 { reviewPage = nil } })) {
                if let index = reviewPage, let pages = images, pages.indices.contains(index) {
                    OfficialBookScanPreview(data: pages[index], page: index + 1, mode: $scanMode)
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
                if let images {
                    let mode = scanMode, quality = scanQuality
                    let processed = try await Task.detached(priority: .userInitiated) {
                        try OfficialBookScanEnhancement.prepare(images, mode: mode, quality: quality)
                    }.value
                    if let existing {
                        try await store.replaceFile(existing, images: processed)
                    } else {
                        let book = try await store.save(title: title, month: month, entity: entity, images: processed, scheduleBackup: onSaved == nil)
                        onSaved?(book)
                        if onSaved != nil { Task { _ = await BackupManager.shared.autoBackup() } }
                    }
                } else if let existing {
                    try store.rename(existing, to: title, month: month, entity: entity)
                } else if let pdf {
                    let book = try await store.save(title: title, month: month, entity: entity, pdf: pdf)
                    onSaved?(book)
                }
                saving = false
                dismiss()
            } catch {
                saving = false
                self.error = error.localizedDescription
            }
        }
    }
}

/// تصغير الصفحات في الخلفية لتجنب فك صور المستند كاملة في القائمة.
private struct OfficialBookScanThumbnail: View {
    let data: Data
    @State private var thumbnail: Data?
    var body: some View {
        Group {
            if let thumbnail, let image = UIImage(data: thumbnail) {
                Image(uiImage: image).resizable().scaledToFit()
            } else {
                ProgressView()
            }
        }
        .task(id: data) {
            let source = data
            let result = await Task.detached(priority: .utility) {
                try? OfficialBookScanEnhancement.render(source, mode: .original, maxPixels: 220, jpegQuality: 0.8)
            }.value
            guard !Task.isCancelled else { return }
            thumbnail = result
        }
    }
}

/// معاينة صفحة واحدة مع مقارنة الصورة الملتقطة والمعالجة قبل حفظ أي ملف.
private struct OfficialBookScanPreview: View {
    let data: Data
    let page: Int
    @Binding var mode: OfficialBookScanEnhancement.Mode
    @Environment(\.dismiss) private var dismiss
    @State private var showOriginal = false
    @State private var originalPreview: Data?
    @State private var enhancedPreview: Data?
    @State private var loading = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            VStack(spacing: 10) {
                Picker(tr("التحسين", "Enhancement"), selection: $mode) {
                    ForEach(OfficialBookScanEnhancement.Mode.allCases) { option in Text(option.title).tag(option) }
                }
                .pickerStyle(.segmented).disabled(loading).padding(.horizontal)
                if mode != .original {
                    Picker(tr("المقارنة", "Compare"), selection: $showOriginal) {
                        Text(tr("قبل", "Before")).tag(true)
                        Text(tr("بعد", "After")).tag(false)
                    }
                    .pickerStyle(.segmented).disabled(loading).padding(.horizontal)
                }
                Group {
                    if loading {
                        ProgressView(tr("إعداد المعاينة…", "Preparing preview…"))
                    } else if let error {
                        ContentUnavailableView(tr("تعذّرت المعاينة", "Preview unavailable"), systemImage: "exclamationmark.triangle", description: Text(error))
                    } else if let data = (showOriginal || mode == .original ? originalPreview : enhancedPreview),
                              let image = UIImage(data: data) {
                        GeometryReader { geometry in
                            ScrollView([.horizontal, .vertical]) {
                                Image(uiImage: image).resizable().scaledToFit()
                                    .frame(width: max(1, geometry.size.width - 16)).padding(8)
                                    .accessibilityLabel(tr("معاينة الصفحة", "Page preview"))
                            }
                        }
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .navigationTitle(tr("الصفحة \(Num.format(page))", "Page \(page)"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button(tr("تم", "Done")) { dismiss() } } }
            .task(id: mode) {
                let selected = mode, source = data
                loading = true; error = nil
                do {
                    let result = try await Task.detached(priority: .userInitiated) {
                        let original = try OfficialBookScanEnhancement.render(source, mode: .original, maxPixels: 1400, jpegQuality: 0.9)
                        let enhanced: Data
                        if selected == .original {
                            enhanced = original
                        } else {
                            enhanced = try OfficialBookScanEnhancement.render(source, mode: selected, maxPixels: 1400, jpegQuality: 0.9)
                        }
                        return (original, enhanced)
                    }.value
                    guard !Task.isCancelled else { return }
                    originalPreview = result.0; enhancedPreview = result.1
                    showOriginal = false
                    loading = false
                } catch {
                    guard !Task.isCancelled else { return }
                    self.error = error.localizedDescription
                    loading = false
                }
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

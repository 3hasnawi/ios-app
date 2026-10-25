import SwiftUI

/// نافذة عائمة لتصدير الاستقطاعات: جهة واحدة (بحث ثم Excel أو PDF)،
/// أو مجموعة جهات (مديريات التنفيذ، المصارف، الأخرى، الكل) في ملف Excel بورقة لكل جهة أو PDF مجمّع.
struct DeductionExportSheet: View {
    let month: String
    let byEntity: Bool
    let groups: [ReportGroup]

    enum Mode: String { case single, group }

    @Environment(\.dismiss) private var dismiss
    @State private var mode: Mode = UserDefaults.standard.string(forKey: "exportSheet") == "group" ? .group : .single
    @State private var search = UserDefaults.standard.string(forKey: "exportSearch") ?? ""
    @State private var selected: ReportGroup?
    @State private var category: EntityCategory = .directorates
    @AppStorage("bulkExportSummary") private var summary = true
    @State private var working: ExportFormat?
    @State private var error: String?
    @FocusState private var searchFocused: Bool
    @State private var detent: PresentationDetent = Self.floating
    static let floating = PresentationDetent.height(640)

    private var matches: [ReportGroup] {
        let tokens = ArabicText.normalize(ArabicText.cleanQuery(search)).split(separator: " ").map(String.init)
        let list = groups.sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
        guard !tokens.isEmpty else { return list }
        return list.filter { g in tokens.allSatisfy { ArabicText.normalize(g.title).contains($0) } }
    }

    private func members(_ c: EntityCategory) -> [ReportGroup] { groups.filter { c.contains($0.title) } }

    var body: some View {
        VStack(spacing: 0) {
            header
            Picker("", selection: $mode) {
                Text(tr("جهة واحدة", "One entity")).tag(Mode.single)
                Text(tr("مجموعة جهات", "Several entities")).tag(Mode.group)
            }
            .pickerStyle(.segmented)
            .padding(.horizontal, 16)
            .padding(.bottom, 10)
            .accessibilityIdentifier("exportModePicker")

            if mode == .single { singleView } else { groupView }

            if let error {
                Text(error).font(.footnote).foregroundStyle(.red).padding(.horizontal, 16).padding(.top, 6)
            }
            buttons
        }
        .background(Color(.systemGroupedBackground))
        // أقل من الارتفاع الكامل حتى تظهر النافذة عائمة (iOS 26)، ويمكن سحبها للأعلى.
        .presentationDetents([Self.floating, .large], selection: $detent)
        .presentationDragIndicator(.visible)
        .presentationCornerRadius(34)
        .onAppear {
            if !search.isEmpty { selected = matches.first }
        }
    }

    private var header: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 2) {
                Text(tr("تصدير الاستقطاعات", "Export deductions")).font(.title3.weight(.bold))
                Text(month).font(.subheadline).foregroundStyle(.secondary)
            }
            Spacer()
            Button {
                dismiss()
            } label: {
                Image(systemName: "xmark")
                    .font(.footnote.weight(.bold))
                    .foregroundStyle(.secondary)
                    .frame(width: 30, height: 30)
                    .background(Color(.tertiarySystemFill), in: Circle())
            }
            .accessibilityLabel(tr("إغلاق", "Close"))
        }
        .padding(.horizontal, 20)
        .padding(.top, 20)
        .padding(.bottom, 12)
    }

    // MARK: جهة واحدة

    private var singleView: some View {
        VStack(spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField(tr("ابحث عن جهة تنفيذ", "Search an execution entity"), text: $search)
                    .focused($searchFocused)
                    .submitLabel(.search)
                    .accessibilityIdentifier("exportSearchField")
                if !search.isEmpty {
                    Button { search = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.tertiary) }
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
            .padding(.horizontal, 16)

            List(matches) { group in
                Button {
                    selected = group
                    searchFocused = false
                } label: {
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(group.title).font(.subheadline.weight(.semibold)).foregroundStyle(.primary)
                            Text(tr("\(Num.format(group.count)) موظف · \(Money.format(group.total))", "\(group.count) employees · \(Money.format(group.total))"))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if selected == group {
                            Image(systemName: "checkmark").font(.body.weight(.bold)).foregroundStyle(Color.accentColor)
                        }
                    }
                }
                .listRowBackground(selected == group ? Color.accentColor.opacity(0.1) : Color(.secondarySystemGroupedBackground))
            }
            .listStyle(.insetGrouped)
            .contentMargins(.top, 0, for: .scrollContent)
            .scrollDismissesKeyboard(.immediately)
            .overlay {
                if matches.isEmpty {
                    Text(tr("لا توجد جهة بهذا الاسم.", "No entity matches.")).foregroundStyle(.secondary)
                }
            }
        }
    }

    // MARK: مجموعة جهات

    private var groupView: some View {
        List {
            Section {
                ForEach(EntityCategory.allCases) { c in
                    let list = members(c)
                    Button {
                        category = c
                    } label: {
                        HStack(spacing: 12) {
                            Image(systemName: c.systemImage)
                                .font(.footnote.weight(.semibold))
                                .foregroundStyle(.white)
                                .frame(width: 30, height: 30)
                                .background(c.color.gradient, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                            VStack(alignment: .leading, spacing: 2) {
                                Text(c.title).font(.subheadline.weight(.semibold)).foregroundStyle(.primary)
                                Text(tr("\(Num.format(list.count)) جهة · \(Money.format(list.reduce(0) { $0 + $1.total }))",
                                        "\(list.count) entities · \(Money.format(list.reduce(0) { $0 + $1.total }))"))
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer()
                            if category == c {
                                Image(systemName: "checkmark").font(.body.weight(.bold)).foregroundStyle(Color.accentColor)
                            }
                        }
                    }
                    .disabled(list.isEmpty)
                    .listRowBackground(category == c ? Color.accentColor.opacity(0.1) : Color(.secondarySystemGroupedBackground))
                    .accessibilityIdentifier("exportCategory-\(c.rawValue)")
                }
            }
            Section {
                Toggle(tr("ورقة خلاصة في البداية", "Summary sheet first"), isOn: $summary)
            } footer: {
                Text(tr("ملف Excel واحد: كل جهة في ورقة باسمها وبالجدول المعتمد. الخلاصة: اسم كل جهة وعدد موظفيها ومجموعها. وفي PDF المجمّع تبدأ كل جهة بصفحة جديدة.",
                        "One Excel file: each entity on its own sheet in the approved table. The combined PDF starts each entity on a new page."))
            }
        }
        .listStyle(.insetGrouped)
        .contentMargins(.top, 0, for: .scrollContent)
    }

    // MARK: الأزرار

    private var buttons: some View {
        let count = members(category).count
        return HStack(spacing: 10) {
            exportButton(.excel,
                         title: mode == .single ? "Excel" : tr("Excel · \(Num.format(count + (summary ? 1 : 0))) ورقة", "Excel · \(count + (summary ? 1 : 0)) sheets"),
                         systemImage: "tablecells.fill", color: Color(red: 0.12, green: 0.48, blue: 0.27), filled: true)
            exportButton(.pdf,
                         title: mode == .single ? "PDF" : tr("PDF مجمّع", "Combined PDF"),
                         systemImage: "doc.richtext.fill", color: Color(red: 0.78, green: 0.16, blue: 0.16), filled: mode == .single)
        }
        .padding(.horizontal, 16)
        .padding(.top, 10)
        .padding(.bottom, 16)
        .disabled(working != nil || (mode == .single ? selected == nil : count == 0))
    }

    private func exportButton(_ format: ExportFormat, title: String, systemImage: String, color: Color, filled: Bool) -> some View {
        Button {
            export(format)
        } label: {
            HStack(spacing: 6) {
                if working == format { ProgressView().tint(filled ? .white : color) } else { Image(systemName: systemImage) }
                Text(title).lineLimit(1).minimumScaleFactor(0.8)
            }
            .font(.body.weight(.bold))
            .frame(maxWidth: .infinity)
            .padding(.vertical, 14)
            .foregroundStyle(filled ? .white : color)
            .background(filled ? color : Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(color, lineWidth: filled ? 0 : 1.5))
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier(format == .excel ? "exportExcelButton" : "exportPDFButton")
    }

    private func export(_ format: ExportFormat) {
        error = nil
        working = format
        let mode = self.mode, selected = self.selected, category = self.category, summary = self.summary
        let month = self.month, byEntity = self.byEntity, groups = self.groups
        Task {
            do {
                let url: URL
                if mode == .single, let group = selected {
                    let container = SharedModel.container
                    let rows = try await Task.detached(priority: .userInitiated) {
                        try await ReportEngine(modelContainer: container).rows(for: group)
                    }.value
                    let files = try await ReportFiles.make(group: group, rows: rows)
                    url = format == .excel ? files.excel : files.pdf
                } else {
                    let list = try await QuickExporter.entities(category: category, month: month, byEntity: byEntity, groups: groups)
                    url = format == .excel
                        ? try await BulkExport.excel(list, category: category, month: month, summary: summary)
                        : try await BulkExport.pdf(list, category: category, month: month, summary: summary)
                }
                working = nil
                ActivityShare.present([url])
            } catch {
                working = nil
                self.error = tr("تعذّر التصدير: \(error.localizedDescription)", "Export failed: \(error.localizedDescription)")
            }
        }
    }
}

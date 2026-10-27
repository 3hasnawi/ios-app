import SwiftUI
import SwiftData
import UIKit

/// نافذة المشاركة: تقرير راتب PDF لشهر القسيمة المعروضة،
/// أو القسيمة المختصرة كصورة/PDF.
struct ShareOptionsSheet: View {
    let employee: Employee
    @Query private var deductions: [Deduction]
    @Environment(\.dismiss) private var dismiss
    @State private var files: PayslipExporter.Files?
    @State private var errorText: String?

    enum Mode: String { case report, image }
    @State private var mode: Mode
    @AppStorage("salaryReportHideZeros") private var hideZeros = false
    @AppStorage("salaryReportHidePersonal") private var hidePersonal = false
    @State private var reportURL: URL?
    @State private var reportPreview: UIImage?
    @State private var reports: [SalaryReport] = []
    @State private var reportError: String?

    init(employee: Employee, initialMode: Mode? = nil) {
        self.employee = employee
        _mode = State(initialValue: initialMode ?? (UserDefaults.standard.string(forKey: "shareMode") == "image" ? .image : .report))
        let key = employee.matchKey
        let month = employee.month
        _deductions = Query(filter: #Predicate { $0.month == month && $0.matchKey == key }, sort: \Deduction.title)
    }

    private var reportKey: String {
        "\(employee.month)-\(hideZeros)-\(hidePersonal)"
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 18) {
                    Picker("", selection: $mode) {
                        Text(tr("تقرير راتب PDF", "Salary report PDF")).tag(Mode.report)
                        Text(tr("قسيمة صورة", "Payslip image")).tag(Mode.image)
                    }
                    .pickerStyle(.segmented)
                    .accessibilityIdentifier("shareModePicker")

                    if mode == .report { reportContent } else { imageContent }
                }
                .padding(20)
            }
            .background(Color(.systemGroupedBackground))
            .navigationTitle(tr("مشاركة", "Share"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(tr("إغلاق", "Close"), systemImage: "xmark") { dismiss() }
                }
            }
        }
        .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
        .presentationDetents([.large])
        .presentationDragIndicator(.visible)
        .task(id: mode) {
            guard mode == .image, files == nil else { return }
            do {
                files = try PayslipExporter.export(employee: employee, deductions: deductions)
            } catch {
                errorText = tr("تعذّر تجهيز الصورة: \(error.localizedDescription)", "Couldn't prepare the image: \(error.localizedDescription)")
            }
        }
        .task(id: "\(reportKey)|\(mode.rawValue)") {
            guard mode == .report else { return }
            await makeReport()
        }
    }

    // MARK: تقرير الراتب

    @ViewBuilder
    private var reportContent: some View {
        Group {
            if let reportPreview {
                Image(uiImage: reportPreview)
                    .resizable()
                    .scaledToFit()
                    .background(Color.white)
                    .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                    .shadow(color: .black.opacity(0.15), radius: 12, y: 4)
                    .frame(maxHeight: 330)
                    .accessibilityIdentifier("salaryReportPreview")
            } else if let reportError {
                Text(reportError).foregroundStyle(.red)
            } else {
                ProgressView().frame(height: 330)
            }
        }

        let warnings = reports.flatMap { r in r.mismatches.map { "\(r.month): \($0)" } }
            + reports.filter(\.missingDetails).map { tr("\($0.month): المخصصات وبيانات الموظف غير محفوظة لهذا الشهر. أعد استيراد ملف رواتبه.",
                                                         "\($0.month): allowances and details aren't saved for this month. Re-import its payroll file.") }
        if !warnings.isEmpty {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(warnings, id: \.self) { Label($0, systemImage: "exclamationmark.triangle.fill").font(.caption) }
            }
            .foregroundStyle(.orange)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background(Color.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        }

        VStack(spacing: 0) {
            Toggle(tr("إخفاء البنود الصفرية", "Hide zero items"), isOn: $hideZeros)
                .padding(.vertical, 10)
            Divider()
            Toggle(tr("إخفاء IBAN والأرقام الشخصية", "Hide IBAN and personal numbers"), isOn: $hidePersonal)
                .padding(.vertical, 10)
        }
        .padding(.horizontal, 14)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14, style: .continuous))

        HStack(spacing: 10) {
            Button {
                if let reportURL { ActivityShare.present([reportURL]) }
            } label: {
                Label(tr("مشاركة PDF", "Share PDF"), systemImage: "square.and.arrow.up")
                    .font(.body.weight(.bold))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 14)
                    .foregroundStyle(.white)
                    .background(Color(red: 0.78, green: 0.16, blue: 0.16), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            }
            .accessibilityIdentifier("shareSalaryReport")
            Button {
                if let reportURL { printReport(reportURL) }
            } label: {
                Label(tr("طباعة", "Print"), systemImage: "printer")
                    .font(.body.weight(.bold))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 13)
                    .foregroundStyle(Color(red: 0.78, green: 0.16, blue: 0.16))
                    .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).stroke(Color(red: 0.78, green: 0.16, blue: 0.16), lineWidth: 1.5))
            }
        }
        .buttonStyle(.plain)
        .disabled(reportURL == nil)
    }

    private func printReport(_ url: URL) {
        let controller = UIPrintInteractionController.shared
        let info = UIPrintInfo.printInfo()
        info.outputType = .general
        info.jobName = url.deletingPathExtension().lastPathComponent
        controller.printInfo = info
        controller.printingItem = url
        controller.present(animated: true)
    }

    private func makeReport() async {
        reportURL = nil
        reportPreview = nil
        reportError = nil
        let hideZeros = self.hideZeros, hidePersonal = self.hidePersonal
        let inputs = [SalaryReport.Input(
            month: employee.month, name: employee.name, rank: employee.rank,
            workplace: employee.workplace, net: employee.netSalary,
            items: employee.items, details: employee.details,
            bankDeductions: deductions.map { ($0.title, $0.amount ?? 0) }
        )]
        let name = employee.name
        do {
            let result = try await Task.detached(priority: .userInitiated) { () throws -> ([SalaryReport], URL, UIImage?) in
                let built = inputs.map { SalaryReport.make($0, hideZeros: hideZeros, hidePersonal: hidePersonal) }
                let folder = FileManager.default.temporaryDirectory.appendingPathComponent("share", isDirectory: true)
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                let url = folder.appendingPathComponent(SalaryReport.fileName(name: name, months: inputs.map(\.month)) + ".pdf")
                try SalaryReportPDF.write(built, to: url)
                return (built, url, SalaryReportPDF.preview(url))
            }.value
            guard !Task.isCancelled else { return }
            reports = result.0
            reportURL = result.1
            reportPreview = result.2
        } catch {
            reportError = tr("تعذّر تجهيز التقرير: \(error.localizedDescription)", "Couldn't prepare the report: \(error.localizedDescription)")
        }
    }

    // MARK: القسيمة المختصرة

    @ViewBuilder
    private var imageContent: some View {
        preview
        VStack(spacing: 12) {
            if let files {
                Button { ActivityShare.present([files.imageURL]) } label: {
                    OptionLabel(title: tr("مشاركة كصورة", "Share as image"), subtitle: tr("واتساب، الرسائل، الصور، أو نسخ", "WhatsApp, Messages, Photos, or Copy"), systemImage: "photo.fill")
                }
                .glassButtonStyle(prominent: true)

                Button { ActivityShare.present([files.pdfURL]) } label: {
                    OptionLabel(title: tr("تصدير PDF", "Export PDF"), subtitle: tr("حفظ في الملفات أو الطباعة أو الإرسال", "Save to Files, print, or send"), systemImage: ReportSymbols.pdf)
                }
                .glassButtonStyle()
            }
        }
        .disabled(files == nil)
    }

    @ViewBuilder
    private var preview: some View {
        if let files {
            Image(uiImage: files.image)
                .resizable()
                .scaledToFit()
                .clipShape(RoundedRectangle(cornerRadius: 20, style: .continuous))
                .shadow(color: .black.opacity(0.15), radius: 16, y: 6)
                .frame(maxHeight: 360)
        } else if let errorText {
            ContentUnavailableView(errorText, systemImage: "exclamationmark.triangle")
        } else {
            ProgressView().frame(height: 360)
        }
    }
}

private struct OptionLabel: View {
    let title: String
    let subtitle: String
    let systemImage: String

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: systemImage)
                .font(.title3)
                .frame(width: 30)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.body.weight(.semibold))
                Text(subtitle).font(.caption).opacity(0.75)
            }
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, 8)
        .padding(.horizontal, 4)
    }
}

@MainActor
enum PayslipExporter {
    struct Files {
        let title: String
        let image: UIImage
        let imageURL: URL
        let pdfURL: URL
    }

    enum ExportError: LocalizedError {
        case renderFailed
        var errorDescription: String? { tr("فشل إنشاء الملف", "Couldn't create the file") }
    }

    /// يرسم البطاقة إلى PNG (للنسخ والمشاركة) وإلى PDF، ويحفظهما في tmp/share.
    static func export(employee: Employee, deductions: [Deduction]) throws -> Files {
        // صورة النسخ والمشاركة: القسيمة المحايدة (بلا شعار ولا اسم وزارة).
        let card = PayslipDocument(style: .export, employee: employee, deductions: deductions)
            .frame(width: 390)
            .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
            .environment(\.colorScheme, .light)
        let renderer = ImageRenderer(content: card)
        renderer.scale = 3

        guard let image = renderer.uiImage, let png = image.pngData() else { throw ExportError.renderFailed }

        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("share", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        let title = "\(employee.name) - \(employee.month)"
        let fileName = title.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
        let imageURL = folder.appendingPathComponent(fileName + ".png")
        let pdfURL = folder.appendingPathComponent(fileName + ".pdf")
        try png.write(to: imageURL)

        var rendered = false
        renderer.render { size, draw in
            var box = CGRect(origin: .zero, size: size)
            guard let pdf = CGContext(pdfURL as CFURL, mediaBox: &box, nil) else { return }
            pdf.beginPDFPage(nil)
            draw(pdf)
            pdf.endPDFPage()
            pdf.closePDF()
            rendered = true
        }
        guard rendered else { throw ExportError.renderFailed }

        return Files(title: title, image: image, imageURL: imageURL, pdfURL: pdfURL)
    }
}

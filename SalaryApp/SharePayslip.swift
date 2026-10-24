import SwiftUI
import SwiftData
import UIKit

/// نافذة المشاركة: معاينة النتيجة ثم نسخها كصورة أو تصديرها PDF أو مشاركتها كصورة.
struct ShareOptionsSheet: View {
    let employee: Employee
    @Query private var deductions: [Deduction]
    @Environment(\.dismiss) private var dismiss
    @State private var files: PayslipExporter.Files?
    @State private var errorText: String?
    @State private var copied = false

    init(employee: Employee) {
        self.employee = employee
        let key = employee.matchKey
        let month = employee.month
        _deductions = Query(filter: #Predicate { $0.month == month && $0.matchKey == key }, sort: \Deduction.title)
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 24) {
                    preview

                    VStack(spacing: 12) {
                        Button {
                            copyImage()
                        } label: {
                            OptionLabel(
                                title: copied ? tr("تم نسخ الصورة", "Image copied") : tr("نسخ كصورة", "Copy as image"),
                                subtitle: tr("للصقها في واتساب أو الملاحظات", "Paste it into WhatsApp or Notes"),
                                systemImage: copied ? "checkmark.circle.fill" : "doc.on.doc.fill"
                            )
                        }
                        .glassButtonStyle(prominent: true)

                        if let files {
                            ShareLink(item: files.pdfURL, preview: SharePreview(files.title, icon: Image(systemName: "doc.richtext"))) {
                                OptionLabel(title: tr("تصدير PDF", "Export PDF"), subtitle: tr("حفظ في الملفات أو الطباعة أو الإرسال", "Save to Files, print, or send"), systemImage: "doc.richtext.fill")
                            }
                            .glassButtonStyle()

                            ShareLink(item: files.imageURL, preview: SharePreview(files.title, image: Image(uiImage: files.image))) {
                                OptionLabel(title: tr("مشاركة كصورة", "Share as image"), subtitle: tr("إرسال الصورة إلى أي تطبيق", "Send the image to any app"), systemImage: "photo.fill")
                            }
                            .glassButtonStyle()
                        }
                    }
                    .disabled(files == nil)
                }
                .padding(20)
            }
            .navigationTitle(tr("مشاركة النتيجة", "Share result"))
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
        .task {
            do {
                files = try PayslipExporter.export(employee: employee, deductions: deductions)
            } catch {
                errorText = tr("تعذّر تجهيز الصورة: \(error.localizedDescription)", "Couldn't prepare the image: \(error.localizedDescription)")
            }
        }
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

    private func copyImage() {
        guard let files else { return }
        UIPasteboard.general.image = files.image
        withAnimation { copied = true }
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

/// صورة النتيجة كما تُرسل: بطاقة الراتب ثم الاستقطاعات، بخلفية فاتحة ثابتة.
struct PayslipCard: View {
    let employee: Employee
    let deductions: [Deduction]

    var body: some View {
        VStack(spacing: 16) {
            SalaryHeader(employee: employee)

            VStack(spacing: 0) {
                HStack {
                    Text(tr("الاستقطاعات", "Deductions")).font(.headline)
                    Spacer()
                    if !deductions.isEmpty {
                        Text(Num.format(deductions.count)).foregroundStyle(.secondary)
                    }
                }
                .padding(.bottom, 8)

                if deductions.isEmpty {
                    Text(tr("لا توجد استقطاعات", "No deductions"))
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.vertical, 6)
                } else {
                    ForEach(deductions) { deduction in
                        Divider()
                        HStack(alignment: .top) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(deduction.title.orDash).font(.subheadline.weight(.medium))
                                if !deduction.note.isEmpty {
                                    Text(deduction.note).font(.caption).foregroundStyle(.secondary)
                                }
                            }
                            Spacer()
                            Text(Money.format(deduction.amount)).font(.subheadline.monospacedDigit())
                        }
                        .padding(.vertical, 8)
                    }
                    Divider()
                    HStack {
                        Text(tr("المجموع", "Total")).font(.subheadline.bold())
                        Spacer()
                        Text(Money.format(deductions.total))
                            .font(.subheadline.bold().monospacedDigit())
                            .foregroundStyle(.red)
                    }
                    .padding(.top, 10)
                }
            }
            .padding(18)
            .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 22, style: .continuous))

            Text(tr("تطبيق الرواتب", "Payroll app"))
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
        .padding(20)
        .frame(width: 390)
        .background(Color(.systemBackground))
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
        let card = PayslipCard(employee: employee, deductions: deductions)
            .environment(\.layoutDirection, AppSettings.isEnglish ? .leftToRight : .rightToLeft)
            .environment(\.colorScheme, .light)
        let renderer = ImageRenderer(content: card)
        renderer.scale = 3

        guard let image = renderer.uiImage, let png = image.pngData() else { throw ExportError.renderFailed }

        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("share", isDirectory: true)
        try? FileManager.default.removeItem(at: folder)
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

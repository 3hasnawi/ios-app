import SwiftUI

/// قسيمة الراتب بشكل المستند الرسمي (جداول بحدود على ورق فاتح).
/// نسختان:
///  - `.screen` داخل التطبيق: ترويسة الوزارة وشعارها، محل العمل، وختم الحالة من الملاك.
///  - `.export` صورة النسخ والمشاركة: محايدة ("قسيمة راتب" فقط، بلا شعار ولا اسم وزارة)، ومحل العمل بدل الرقم الإحصائي.
/// الحقول الفارغة لا تظهر.
struct PayslipDocument: View {
    enum Style { case screen, export }

    struct Line: Hashable {
        let title: String
        let note: String
        let amount: Double?
    }

    let style: Style
    let name: String
    let rank: String
    let workplace: String
    let status: String
    let month: String
    let net: Double?
    let lines: [Line]

    static let green = Color(red: 0.06, green: 0.24, blue: 0.18)
    static let paper = Color(red: 1.0, green: 0.992, blue: 0.969)
    static let rule = Color(red: 0.89, green: 0.875, blue: 0.82)
    static let band = Color(red: 0.953, green: 0.937, blue: 0.886)
    private static let labelWidth: CGFloat = 112
    private static let amountWidth: CGFloat = 112

    private var total: Double { lines.reduce(0) { $0 + ($1.amount ?? 0) } }

    private var infoRows: [(String, String)] {
        [(tr("الاسم", "Name"), name), (tr("الرتبة", "Rank"), rank),
         (tr("محل العمل", "Workplace"), workplace)]
            .filter { !$0.1.trimmingCharacters(in: .whitespaces).isEmpty }
    }

    var body: some View {
        VStack(spacing: 0) {
            header
            VStack(alignment: .leading, spacing: 14) {
                VStack(spacing: 0) {
                    ForEach(Array(infoRows.enumerated()), id: \.offset) { _, row in
                        tableRow(narrowFirst: true) {
                            cell(row.0, color: .secondary)
                        } second: {
                            cell(row.1, weight: row.0 == tr("الاسم", "Name") ? .bold : .regular)
                        }
                    }
                }
                VStack(alignment: .leading, spacing: 6) {
                    if style == .screen {
                        Text(tr("الاستقطاعات", "Deductions"))
                            .font(.footnote.bold())
                            .foregroundStyle(Self.green)
                    }
                    deductionsTable
                }
                netRow
            }
            .padding(16)
            .background {
                if style == .screen {
                    Image("HomeLogo")
                        .resizable()
                        .scaledToFit()
                        .frame(maxWidth: 230)
                        .opacity(0.05)
                        .accessibilityHidden(true)
                }
            }
        }
        .foregroundStyle(Color.black)
        .background(Self.paper)
        .clipShape(RoundedRectangle(cornerRadius: style == .screen ? 10 : 0, style: .continuous))
        .environment(\.colorScheme, .light)
    }

    // MARK: - الأجزاء

    @ViewBuilder
    private var header: some View {
        switch style {
        case .screen:
            HStack(spacing: 10) {
                Image("HomeLogo")
                    .resizable()
                    .scaledToFit()
                    .padding(2)
                    .frame(width: 40, height: 40)
                    .background(.white, in: Circle())
                VStack(alignment: .leading, spacing: 1) {
                    Text(tr("وزارة الداخلية", "Ministry of Interior")).font(.subheadline.bold())
                    Text(tr("قيادة شرطة محافظة الأنبار", "Anbar Governorate Police Command"))
                        .font(.caption.weight(.semibold))
                        .fixedSize(horizontal: false, vertical: true)
                    Text(tr("قسيمة راتب · \(month)", "Payslip · \(month)")).font(.caption).opacity(0.85)
                }
                Spacer()
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .background(Self.green)
        case .export:
            HStack {
                Text(tr("قسيمة راتب", "Payslip")).font(.headline)
                Spacer()
                Text(month).font(.subheadline).opacity(0.85)
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 18)
            .padding(.vertical, 14)
            .background(Self.green)
        }
    }

    private var deductionsTable: some View {
        VStack(spacing: 0) {
            tableRow(narrowFirst: false, fill: Self.band) {
                cell(tr("الاستقطاع", "Deduction"), weight: .bold)
            } second: {
                cell(tr("المبلغ", "Amount"), weight: .bold)
            }
            if lines.isEmpty {
                tableRow(narrowFirst: false) {
                    cell(tr("لا توجد استقطاعات", "No deductions"), color: .secondary)
                } second: {
                    cell("—", color: .secondary)
                }
            } else {
                ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                    tableRow(narrowFirst: false) {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(line.title.orDash).font(.subheadline)
                            if !line.note.isEmpty {
                                Text(line.note).font(.caption2).foregroundStyle(.secondary)
                            }
                        }
                    } second: {
                        cell(Money.format(line.amount), monospaced: true)
                    }
                }
                tableRow(narrowFirst: false, fill: Self.band) {
                    cell(tr("المجموع", "Total"), weight: .bold)
                } second: {
                    cell(Money.format(total), weight: .bold, color: .red, monospaced: true)
                }
            }
        }
    }

    @ViewBuilder
    private var netRow: some View {
        switch style {
        case .screen:
            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 0) {
                    Text(tr("صافي الراتب", "Net salary")).font(.caption).foregroundStyle(.secondary)
                    Text(Money.format(net))
                        .font(.system(size: 28, weight: .bold, design: .rounded))
                        .monospacedDigit()
                        .foregroundStyle(Self.green)
                        .lineLimit(1)
                        .minimumScaleFactor(0.6)
                }
                Spacer()
                if !status.isEmpty {
                    Text(status)
                        .font(.caption.weight(.heavy))
                        .multilineTextAlignment(.center)
                        .lineLimit(2)
                        .minimumScaleFactor(0.7)
                        .foregroundStyle(Color.blue)
                        .padding(8)
                        .frame(width: 76, height: 76)
                        .overlay(Circle().stroke(Color.blue, lineWidth: 2.5))
                        .rotationEffect(.degrees(-14))
                        .opacity(0.8)
                }
            }
        case .export:
            HStack {
                Text(tr("صافي الراتب", "Net salary")).font(.subheadline.bold())
                Spacer()
                Text(Money.format(net))
                    .font(.system(size: 26, weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
            }
            .foregroundStyle(Self.green)
            .padding(.horizontal, 14)
            .padding(.vertical, 12)
            .background(Self.green.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
        }
    }

    // MARK: - الجدول

    /// صف بخليتين متساويتي الارتفاع: الأولى إلى اليمين (في العربية).
    private func tableRow<A: View, B: View>(narrowFirst: Bool, fill: Color? = nil,
                                             @ViewBuilder first: () -> A, @ViewBuilder second: () -> B) -> some View {
        HStack(spacing: 0) {
            first()
                .modifier(Cell(fill: fill))
                .frame(width: narrowFirst ? Self.labelWidth : nil)
                .frame(maxWidth: narrowFirst ? nil : .infinity)
            second()
                .modifier(Cell(fill: fill))
                .frame(width: narrowFirst ? nil : Self.amountWidth)
                .frame(maxWidth: narrowFirst ? .infinity : nil)
        }
        .fixedSize(horizontal: false, vertical: true)
    }

    private func cell(_ text: String, weight: Font.Weight = .regular, color: Color = .black, monospaced: Bool = false) -> some View {
        Text(Num.digits(text))
            .font(.subheadline.weight(weight))
            .monospacedDigit()
            .foregroundStyle(color)
            .fixedSize(horizontal: false, vertical: true)
    }

    private struct Cell: ViewModifier {
        let fill: Color?
        func body(content: Content) -> some View {
            content
                .padding(.horizontal, 10)
                .padding(.vertical, 8)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
                .background(fill ?? .clear)
                .overlay(Rectangle().stroke(PayslipDocument.rule, lineWidth: 0.5))
        }
    }
}

extension PayslipDocument {
    /// من سجل الرواتب (واختيارياً سجل الملاك).
    init(style: Style, employee: Employee, staff: StaffMember? = nil, deductions: [Deduction]) {
        self.init(
            style: style,
            name: employee.name,
            rank: employee.rank.isEmpty ? (staff?.rank ?? "") : employee.rank,
            workplace: employee.workplace.isEmpty ? (staff?.workplace ?? "") : employee.workplace,
            status: staff?.status ?? "",
            month: employee.month,
            net: employee.netSalary,
            lines: deductions.map { Line(title: $0.title, note: $0.note, amount: $0.amount) }
        )
    }
}

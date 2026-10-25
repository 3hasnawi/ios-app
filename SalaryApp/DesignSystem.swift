import SwiftUI

/// عناصر التصميم المشتركة. على iOS 26 فما بعد (Liquid Glass) تُستخدم أزرار الزجاج
/// وشريط البحث السفلي، وعلى iOS 18 يُستخدم ما يقابلها.
enum Theme {
    static let salaryGradient = LinearGradient(
        colors: [Color(red: 0.16, green: 0.42, blue: 0.95), Color(red: 0.38, green: 0.27, blue: 0.86)],
        startPoint: .topLeading,
        endPoint: .bottomTrailing
    )

    private static let avatarColors: [Color] = [.blue, .indigo, .teal, .orange, .pink, .purple, .green, .cyan]

    /// لون ثابت لكل اسم (hashValue يتغير بين التشغيلات فلا يصلح هنا).
    static func avatarColor(for name: String) -> Color {
        let sum = name.unicodeScalars.reduce(0) { $0 &+ Int($1.value) }
        return avatarColors[sum % avatarColors.count]
    }
}

extension View {
    @ViewBuilder
    func glassButtonStyle(prominent: Bool = false) -> some View {
        if #available(iOS 26, *) {
            if prominent { buttonStyle(.glassProminent) } else { buttonStyle(.glass) }
        } else {
            if prominent { buttonStyle(.borderedProminent) } else { buttonStyle(.bordered) }
        }
    }

    /// شريط البحث ثابت تحت العنوان دائماً.
    /// (مع التبويبات في iOS 26 كان شريط البحث السفلي يختفي أحياناً بعد البحث والرجوع من صفحة التفاصيل.)
    func adaptiveSearchable(text: Binding<String>, prompt: String) -> some View {
        searchable(text: text, placement: .navigationBarDrawer(displayMode: .always), prompt: Text(prompt))
    }
}

/// دائرة بالحرف الأول من الاسم.
struct NameAvatar: View {
    let name: String
    var size: CGFloat = 42

    var body: some View {
        Text(String(name.prefix(1)))
            .font(.system(size: size * 0.42, weight: .semibold, design: .rounded))
            .foregroundStyle(.white)
            .frame(width: size, height: size)
            .background(Theme.avatarColor(for: name).gradient, in: Circle())
    }
}

/// بطاقة صافي الراتب: تظهر في صفحة التفاصيل وفي الصورة/PDF المُشارَك.
struct SalaryHeader: View {
    let employee: Employee

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Label(employee.month, systemImage: "calendar")
                    .font(.subheadline.weight(.semibold))
                Spacer()
                Image(systemName: "banknote.fill")
                    .font(.title3)
            }
            .foregroundStyle(.white.opacity(0.9))

            VStack(alignment: .leading, spacing: 4) {
                Text(employee.name)
                    .font(.title3.bold())
                Text([employee.rank, employee.workplace].filter { !$0.isEmpty }.joined(separator: " · "))
                    .font(.subheadline)
                    .opacity(0.85)
            }

            VStack(alignment: .leading, spacing: 2) {
                Text(tr("صافي الراتب", "Net salary"))
                    .font(.caption.weight(.medium))
                    .opacity(0.8)
                Text(Money.format(employee.netSalary))
                    .font(.system(size: 40, weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.6)
            }
        }
        .foregroundStyle(.white)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(22)
        .background(Theme.salaryGradient, in: RoundedRectangle(cornerRadius: 28, style: .continuous))
    }
}

extension Array where Element == Deduction {
    var total: Double { reduce(0) { $0 + ($1.amount ?? 0) } }
}

/// رمز واحد لكل فعل في صفحات التقارير ونوافذها.
nonisolated enum ReportSymbols {
    static let preview = "doc.text.magnifyingglass"
    static let export = "square.and.arrow.up"
    static let excel = "tablecells"
    static let pdf = "doc.richtext"
}

struct ReportActionButton: View {
    let title: String
    let systemImage: String
    var prominent = false
    var tint: Color = .accentColor
    var isWorking = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                if isWorking {
                    ProgressView().frame(width: 22, height: 22)
                } else {
                    Image(systemName: systemImage)
                        .font(.system(size: 20, weight: .medium))
                        .frame(width: 22, height: 22)
                }
                Text(title).font(.subheadline.weight(.semibold)).lineLimit(2)
            }
            .frame(maxWidth: .infinity, minHeight: 44)
            .padding(.horizontal, 8)
        }
        .glassButtonStyle(prominent: prominent)
        .buttonBorderShape(.capsule)
        .tint(tint)
    }
}

struct ReportToolbarLabel: View {
    var isWorking = false

    var body: some View {
        Group {
            if isWorking {
                ProgressView().accessibilityLabel(tr("جارٍ التصدير", "Exporting"))
            } else {
                Label(tr("تصدير", "Export"), systemImage: ReportSymbols.export)
                    .labelStyle(.iconOnly)
            }
        }
    }
}

import SwiftUI

/// مصادر البطاقات المعروضة في الرئيسية؛ كلها من الرواتب والاستقطاعات.
enum HomeTileKind: String, CaseIterable, Identifiable {
    case net, employees, deductionCount, deductionTotal, attention, reports, recents
    case changes, entities, categories, special, fileStatus, hidden

    var id: String { rawValue }

    var title: String {
        switch self {
        case .net: tr("صافي الرواتب", "Net payroll")
        case .employees: tr("الموظفون", "Employees")
        case .deductionCount: tr("الاستقطاعات", "Deductions")
        case .deductionTotal: tr("مبلغ الاستقطاعات", "Deduction amount")
        case .attention: tr("يحتاج انتباهك", "Needs attention")
        case .reports: tr("التقارير والكشوف", "Reports and statements")
        case .recents: tr("آخر من بحثت عنهم", "Recent payroll searches")
        case .changes: tr("متغيرات الشهر", "Monthly changes")
        case .entities: tr("جهات الاستقطاع", "Deduction entities")
        case .categories: tr("فئات الرواتب", "Payroll categories")
        case .special: tr("الكشوف الخاصة", "Special reports")
        case .fileStatus: tr("حالة ملفات الشهر", "Monthly file status")
        case .hidden: tr("إخفاء البطاقة", "Hide card")
        }
    }

    static let payroll: [Self] = [.net, .employees, .changes, .categories, .fileStatus]
    static let deductions: [Self] = [.deductionCount, .deductionTotal, .attention, .entities]
    static let navigation: [Self] = [.reports, .special, .recents]
}

enum HomeTileLayout {
    static let key = "homeTileLayout"
    static let defaults: [HomeTileKind] = [.net, .employees, .deductionCount, .attention, .reports, .recents]
    static var positions: [String] { [
        tr("البطاقة الرئيسية", "Main card"), tr("البطاقة الصغيرة اليمنى", "Right small card"),
        tr("البطاقة الصغيرة اليسرى", "Left small card"), tr("البطاقة الأولى", "First card"),
        tr("البطاقة الثانية", "Second card"), tr("البطاقة الثالثة", "Third card")
    ] }

    static func decode(_ raw: String) -> [HomeTileKind] {
        let kinds = raw.split(separator: "|").compactMap { HomeTileKind(rawValue: String($0)) }
        return kinds.count == defaults.count ? kinds : defaults
    }

    static func select(_ kind: HomeTileKind, at index: Int, in raw: String) -> String {
        var kinds = decode(raw)
        guard kinds.indices.contains(index) else { return raw }
        if kind != .hidden, let other = kinds.firstIndex(of: kind), other != index {
            kinds[other] = kinds[index]
        }
        kinds[index] = kind
        return kinds.map(\.rawValue).joined(separator: "|")
    }
}

struct HomeTileSpec {
    let title: String
    let value: String
    let subtitle: String
    var rows: [String] = []
}

struct HomeTileCard: View {
    let kind: HomeTileKind
    let spec: HomeTileSpec
    let compact: Bool
    let action: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: compact ? 6 : 10) {
            Text(spec.title)
                .font(compact ? .subheadline : .headline)
                .foregroundStyle(kind == .net ? Color.white.opacity(0.85) : Color.secondary)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
            if !spec.value.isEmpty {
                Text(spec.value)
                    .font(.system(size: kind == .net && !compact ? 32 : compact ? 27 : 25,
                                  weight: .bold, design: .rounded))
                    .monospacedDigit()
                    .lineLimit(1)
                    .minimumScaleFactor(0.55)
                    .foregroundStyle(kind == .net ? Color.white : Color.primary)
            }
            if !spec.subtitle.isEmpty {
                Text(spec.subtitle)
                    .font(.caption)
                    .foregroundStyle(kind == .net ? Color.white.opacity(0.85) : Color.secondary)
                    .lineLimit(compact ? 1 : 2)
            }
            if !compact {
                ForEach(Array(spec.rows.prefix(3).enumerated()), id: \.offset) { item in
                    Divider().overlay(kind == .net ? Color.white.opacity(0.2) : Color.clear)
                    Text(item.element).font(.subheadline).lineLimit(2)
                        .foregroundStyle(kind == .net ? Color.white : Color.primary)
                }
            }
        }
        .frame(maxWidth: .infinity, minHeight: compact ? 100 : 82, alignment: .leading)
        .padding(16)
        .background {
            RoundedRectangle(cornerRadius: 22, style: .continuous)
                .fill(kind == .net ? AnyShapeStyle(Theme.salaryGradient) : AnyShapeStyle(Color(.secondarySystemGroupedBackground)))
        }
        .contentShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
        .onTapGesture(perform: action)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }
}

/// تعديل جميع المواضع والعودة السريعة إلى ترتيب الواجهة المعتمد.
struct HomeLayoutSettingsView: View {
    @AppStorage(HomeTileLayout.key) private var layoutRaw = ""

    var body: some View {
        Form {
            Section {
                ForEach(HomeTileLayout.defaults.indices, id: \.self) { index in
                    Menu {
                        ForEach(HomeTileKind.payroll + HomeTileKind.deductions + HomeTileKind.navigation) { kind in
                            Button(kind.title) { layoutRaw = HomeTileLayout.select(kind, at: index, in: layoutRaw) }
                        }
                        Button(HomeTileKind.hidden.title, systemImage: "eye.slash") {
                            layoutRaw = HomeTileLayout.select(.hidden, at: index, in: layoutRaw)
                        }
                    } label: {
                        HStack {
                            Text(HomeTileLayout.positions[index]).foregroundStyle(.primary)
                            Spacer()
                            Text(HomeTileLayout.decode(layoutRaw)[index].title).foregroundStyle(.secondary)
                        }
                    }
                }
            } footer: {
                Text(tr("اختر بيانات كل موضع. إذا اخترت بطاقة موجودة في موضع آخر، تتبادل البطاقتان مكانهما.",
                        "Choose each position's data. Selecting a card already on the dashboard swaps the two cards."))
            }
            Section {
                Button(tr("إعادة التصميم الافتراضي", "Reset default layout")) { layoutRaw = "" }
                    .disabled(layoutRaw.isEmpty)
            }
        }
        .navigationTitle(tr("تخصيص بطاقات الرئيسية", "Customize home cards"))
        .navigationBarTitleDisplayMode(.inline)
    }
}

import SwiftUI

/// شريط التنقل المعتمد: كبسولة صغيرة وعدسة مستقلة، بلا عناوين مرئية.
struct CompactBottomBar: View {
    @Binding var selection: AppRouter.Tab
    let showsShare: Bool

    private let height: CGFloat = 60

    var body: some View {
        GlassEffectContainer(spacing: 12) {
            HStack(spacing: 12) {
                HStack(spacing: 0) {
                    tabButton(.payroll, title: tr("الرئيسية", "Home"), icon: "house", id: "bottomHome")
                    tabButton(.reports, title: tr("التقارير", "Reports"), icon: "chart.bar.doc.horizontal", id: "bottomReports")
                    tabButton(.settings, title: tr("الإعدادات", "Settings"), icon: "gearshape", id: "bottomSettings")
                    if showsShare {
                        tabButton(.share, title: tr("مشاركة", "Share"), icon: "square.and.arrow.up", id: "bottomShare")
                    }
                }
                .padding(6)
                .frame(maxWidth: showsShare ? 258 : 198)
                .frame(height: height)
                .glassEffect(.regular, in: Capsule())

                Spacer(minLength: 0)

                tabButton(.search, title: tr("بحث", "Search"), icon: "magnifyingglass", id: "bottomSearch")
                    .frame(width: height, height: height)
                    .glassEffect(.regular.interactive(), in: Circle())
            }
            .frame(height: height)
        }
        .padding(.horizontal, 27)
        .padding(.top, 8)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("compactBottomBar")
    }

    private func tabButton(_ tab: AppRouter.Tab, title: String, icon: String, id: String) -> some View {
        let isSelected = selection == tab
        return Button {
            selection = tab
        } label: {
            Image(systemName: icon)
                .font(.system(size: 24, weight: .regular))
                .symbolVariant(.none)
                .foregroundStyle(isSelected ? Color.accentColor : Color.primary)
                .frame(maxWidth: .infinity)
                .frame(height: 48)
                .background {
                    if isSelected {
                        Capsule().fill(Color.primary.opacity(0.07))
                    }
                }
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        // تبقى أسماء الأزرار متاحة لقارئ الشاشة واختبارات التنقل.
        .accessibilityLabel(Text(title))
        .accessibilityAddTraits(isSelected ? .isSelected : [])
        .accessibilityIdentifier(id)
    }
}

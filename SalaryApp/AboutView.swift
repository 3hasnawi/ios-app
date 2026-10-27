import SwiftUI

/// معلومات التطبيق والمطوّر؛ رقم النسخة يُقرأ من الحزمة حتى يواكب كل بناء.
struct AboutView: View {
    var body: some View {
        GeometryReader { geometry in
            ScrollView {
                VStack(spacing: 20) {
                    VStack(spacing: 12) {
                        Image(systemName: "book.closed.fill")
                            .font(.system(size: 32, weight: .semibold))
                            .foregroundStyle(.white)
                            .frame(width: 72, height: 72)
                            .background(Theme.salaryGradient, in: RoundedRectangle(cornerRadius: 20))
                        Text(tr("الرواتب", "Salary App"))
                            .font(.title2.bold())
                        Text(AppVersion.label)
                            .font(.subheadline.monospacedDigit())
                            .foregroundStyle(.secondary)
                            .accessibilityIdentifier("appVersion")
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.top, 28)

                    VStack(alignment: .leading, spacing: 12) {
                        Text(tr("عن البرنامج", "About the app"))
                            .font(.headline)
                        Text(tr("إدارة بيانات الرواتب والاستقطاعات، تدقيق الأشهر ومقارنتها، البحث عن الموظفين، وإعداد الكشوف وكشف حساب البنك والنسخ الاحتياطي.",
                                "Manage payroll and deductions, audit and compare months, search employees, and prepare reports, bank statements and backups."))
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(20)
                    .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 22))

                    Spacer(minLength: 24)

                    VStack(spacing: 10) {
                        Image("DeveloperPortrait")
                            .resizable()
                            .scaledToFill()
                            .frame(width: 88, height: 88)
                            .clipShape(Circle())
                            .overlay(Circle().strokeBorder(.white.opacity(0.8), lineWidth: 2))
                            .accessibilityLabel(tr("صورة المطوّر", "Developer photo"))
                            .accessibilityIdentifier("developerPortrait")
                        Text(tr("تطوير : Emad Hasnawi", "Developed by: Emad Hasnawi"))
                            .font(.subheadline.weight(.semibold))
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.bottom, 24)
                }
                .padding(.horizontal, 20)
                .frame(minHeight: geometry.size.height)
            }
        }
        .background(Color(.systemGroupedBackground))
        .navigationTitle(tr("حول", "About"))
        .navigationBarTitleDisplayMode(.inline)
    }
}

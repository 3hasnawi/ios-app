import XCTest

/// اختبار تنقّل حقيقي: يضغط الأزرار، يكتب في البحث، يفتح النتائج ويرجع، ويمرّر القوائم.
/// كل خطوة تُطبع بسطر STEP (وقت البداية والنهاية والمدة)، وكل مشكلة بسطر ISSUE،
/// والتطبيق نفسه يسجّل تجمّد الواجهة في Documents/perf.txt (-perfLog YES).
final class NavigationTests: XCTestCase {
    private let app = XCUIApplication()
    private var issues: [String] = []

    override func setUp() {
        continueAfterFailure = true
        app.launchArguments = ["-AppleLanguages", "(ar)", "-AppleLocale", "ar_IQ", "-perfLog", "YES"]
    }

    // MARK: - أدوات

    private func step(_ name: String, _ action: () -> Void) {
        let start = Date()
        XCTContext.runActivity(named: name) { _ in action() }
        let end = Date()
        print("STEP \(String(format: "%.3f %.3f", start.timeIntervalSince1970, end.timeIntervalSince1970)) \(Int(end.timeIntervalSince(start) * 1000)) \(name)")
    }

    private func issue(_ text: String) {
        issues.append(text)
        print("ISSUE \(String(format: "%.3f", Date().timeIntervalSince1970)) \(text)")
    }

    private func element(_ id: String) -> XCUIElement {
        app.descendants(matching: .any).matching(identifier: id).firstMatch
    }

    @discardableResult
    private func wait(_ e: XCUIElement, _ what: String, timeout: TimeInterval = 8) -> Bool {
        let start = Date()
        if e.waitForExistence(timeout: timeout) {
            let ms = Int(Date().timeIntervalSince(start) * 1000)
            print("WAIT \(ms) \(what)")
            if ms > 1500 { issue("بطء: \(what) ظهر بعد \(ms)ms") }
            return true
        }
        issue("لم يظهر: \(what)")
        return false
    }

    /// يغلق القائمة المنبثقة بالضغط أعلى الشاشة (منطقة شريط الحالة).
    private func dismissMenu() {
        app.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.04)).tap()
    }

    private var searchTab: XCUIElement {
        let inBar = app.tabBars.buttons["بحث"]
        return inBar.exists ? inBar : app.buttons["بحث"].firstMatch
    }

    private var homeTab: XCUIElement {
        let inBar = app.tabBars.buttons["الرئيسية"]
        return inBar.exists ? inBar : app.buttons["الرئيسية"].firstMatch
    }

    private var searchField: XCUIElement { app.searchFields.firstMatch }

    private func type(_ text: String) {
        let field = searchField
        guard wait(field, "حقل البحث") else { return }
        field.tap()
        field.typeText(text)
        let value = field.value as? String ?? ""
        if !value.contains(text.trimmingCharacters(in: .whitespaces)) {
            issue("الكتابة في حقل البحث لم تنجح (القيمة: \(value))")
        }
    }

    private func openAndBack(_ index: Int) {
        let cards = app.descendants(matching: .any).matching(identifier: "resultCard")
        guard cards.count > index else { issue("لا توجد نتيجة رقم \(index + 1)"); return }
        step("فتح النتيجة \(index + 1)") {
            cards.element(boundBy: index).tap()
            wait(element("personPartPicker"), "صفحة الشخص")
        }
        step("أقسام صفحة الشخص") {
            let picker = element("personPartPicker")
            guard picker.exists else { return }
            for title in ["الأشهر", "الراتب"] where picker.buttons[title].exists {
                picker.buttons[title].tap()
            }
            app.swipeUp()
            app.swipeDown()
        }
        step("رجوع للنتائج") {
            app.navigationBars.buttons.element(boundBy: 0).tap()
            wait(element("resultCard"), "النتائج بعد الرجوع")
        }
    }

    // MARK: - الاختبار

    func testNavigationFlow() {
        step("تشغيل التطبيق") {
            app.launch()
            wait(app.navigationBars["الرئيسية"], "الشاشة الرئيسية", timeout: 30)
        }
        step("تمرير الرئيسية") {
            app.swipeUp()
            app.swipeDown()
        }
        step("قائمة الأشهر في الرئيسية") {
            let menu = element("homeMonthMenu")
            guard wait(menu, "زر الأشهر") else { return }
            menu.tap()
            if wait(app.buttons["كل الأشهر"], "قائمة الأشهر") { app.buttons["كل الأشهر"].tap() }
            menu.tap()
            if wait(app.buttons["آخر شهر"], "قائمة الأشهر مرة ثانية") { app.buttons["آخر شهر"].tap() }
        }
        step("فتح الإعدادات") {
            element("settingsButton").tap()
            wait(app.navigationBars["الإعدادات"], "الإعدادات")
        }
        step("تمرير الإعدادات وإغلاقها") {
            app.swipeUp()
            app.swipeDown()
            app.buttons["تم"].firstMatch.tap()
            wait(app.navigationBars["الرئيسية"], "الرئيسية بعد الإعدادات")
        }

        step("تبويب البحث") {
            searchTab.tap()
            wait(searchField, "شريط البحث")
        }
        step("كتابة اسم") {
            type("احمد علي")
            wait(element("resultCard"), "نتائج البحث")
        }
        step("تمرير النتائج") {
            let list = element("searchList")
            for _ in 0..<3 { list.swipeUp(velocity: .fast) }
            for _ in 0..<3 { list.swipeDown(velocity: .fast) }
        }
        for i in 0..<3 { openAndBack(i) }

        step("فلتر النطاق") {
            let menu = element("scopeMenu")
            guard wait(menu, "زر النطاق") else { return }
            menu.tap()
            if wait(app.buttons["الرواتب"], "خيارات النطاق") { app.buttons["الرواتب"].tap() }
            wait(element("resultCard"), "النتائج بعد تغيير النطاق")
            menu.tap()
            if wait(app.buttons["الكل"], "خيارات النطاق مرة ثانية") { app.buttons["الكل"].tap() }
        }
        step("فلتر الأشهر في البحث") {
            let menu = element("searchMonthMenu")
            guard wait(menu, "زر الأشهر في البحث") else { return }
            menu.tap()
            let second = app.buttons["أيلول 2026"]
            if wait(second, "قائمة أشهر البحث") { second.tap(); second.tap() }
            dismissMenu()
        }
        step("تضييق البحث") {
            searchField.tap()
            searchField.typeText(" حسين")
            wait(element("resultCard"), "النتائج بعد تضييق البحث")
        }

        step("الرجوع للرئيسية") {
            homeTab.tap()
            wait(app.navigationBars["الرئيسية"], "الرئيسية")
        }
        step("العودة للبحث") {
            searchTab.tap()
            // المشكلة السابقة: شريط البحث يختفي بعد البحث والخروج.
            if !searchField.waitForExistence(timeout: 3) { issue("شريط البحث اختفى بعد العودة لتبويب البحث") }
        }
        step("فتح من آخر عمليات البحث") {
            homeTab.tap()
            let recent = app.buttons.matching(NSPredicate(format: "label CONTAINS 'احمد' OR label CONTAINS 'أحمد'")).firstMatch
            if wait(recent, "آخر من بحثت عنهم") {
                recent.tap()
                wait(element("resultCard"), "النتائج من آخر عمليات البحث")
            }
        }
        step("تنقّل سريع بين التبويبين") {
            for _ in 0..<5 {
                homeTab.tap()
                searchTab.tap()
            }
            if !searchField.waitForExistence(timeout: 3) { issue("شريط البحث اختفى بعد التنقل السريع") }
        }

        print("RESULT issues=\(issues.count)")
    }
}

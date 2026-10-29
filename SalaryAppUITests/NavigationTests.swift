import XCTest

/// اختبار تنقّل حقيقي: يضغط الأزرار، يكتب في البحث، يفتح النتائج ويرجع، ويمرّر القوائم.
/// كل خطوة تُطبع بسطر STEP (وقت البداية والنهاية والمدة)، وكل مشكلة بسطر ISSUE،
/// والتطبيق نفسه يسجّل تجمّد الواجهة في Documents/perf.txt (-perfLog YES).
final class NavigationTests: XCTestCase {
    private let app = XCUIApplication()
    private var issues: [String] = []

    override func setUp() {
        continueAfterFailure = true
        // نوافذ النظام (مثل تفعيل الإملاء الصوتي) لا علاقة لها بالتطبيق: تُغلق تلقائياً.
        addUIInterruptionMonitor(withDescription: "System alert") { alert in
            for title in ["Not Now", "ليس الآن", "Cancel", "إلغاء", "OK"] where alert.buttons[title].exists {
                alert.buttons[title].tap()
                return true
            }
            return false
        }
        app.launchArguments = ["-AppleLanguages", "(ar)", "-AppleLocale", "ar_IQ", "-perfLog", "YES"]
    }

    func testDeductionComparison() {
        app.launchArguments += ["-tab", "reports", "-compareDeductions", "YES"]
        app.launch()
        guard wait(element("deductionAuditSummary"), "خلاصة مقارنة الاستقطاعات", timeout: 30) else { return }
        let changed = element("deductionTile-changed")
        guard wait(changed, "بطاقة تغيّر المبلغ") else { return }
        changed.tap()
        guard wait(element("deductionChangeList"), "قائمة تغيّر المبلغ") else { return }
        XCTAssertTrue(app.navigationBars["تغيّر المبلغ"].exists || app.staticTexts["تغيّر المبلغ"].exists)
    }

    func testStaffSearchModes() {
        for (option, query) in [("اسم الأم", "فاطمة محمد الدليمي"), ("الرقم الإحصائي", "١٠٠٠٠٠")] {
            app.launchArguments += ["-skipLock", "YES", "-tab", "search", "-fakeStaff", "100"]
            app.launch()
            guard wait(element("searchModeMenu"), "زر نوع البحث") else { return }
            element("searchModeMenu").tap()
            guard wait(app.buttons[option], "خيار \(option)") else { return }
            app.buttons[option].tap()
            type(query)
            wait(element("resultCard"), "نتيجة \(option)", timeout: 20)
            app.terminate()
            app.launchArguments = ["-AppleLanguages", "(ar)", "-AppleLocale", "ar_IQ", "-perfLog", "YES"]
        }
    }

    func testExecutionAccountsMerge() {
        app.launchArguments += ["-skipLock", "YES", "-tab", "settings", "-openAccounts", "YES"]
        app.launch()
        XCTAssertTrue(app.navigationBars["حسابات جهات التنفيذ"].waitForExistence(timeout: 12))
        XCTAssertTrue(app.staticTexts["جهة تجريبية أ"].exists)
        XCTAssertTrue(app.staticTexts["جهة تجريبية ب"].exists) // غابت من الملف الثاني وبقيت محفوظة.
        XCTAssertTrue(app.staticTexts["جهة تجريبية ج"].exists)
        XCTAssertFalse(app.staticTexts["جهة خاطئة"].exists)
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
        if !text.hasPrefix("بطء:") { XCTFail(text) }
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
            if ms > 2500 { issue("بطء: \(what) ظهر بعد \(ms)ms") }
            return true
        }
        issue("لم يظهر: \(what)")
        return false
    }

    private var searchTab: XCUIElement {
        let inBar = app.tabBars.buttons["بحث"]
        return inBar.exists ? inBar : app.buttons["بحث"].firstMatch
    }

    private var settingsTab: XCUIElement {
        hideKeyboard()
        let inBar = app.tabBars.buttons["الإعدادات"]
        return inBar.exists ? inBar : app.buttons["الإعدادات"].firstMatch
    }

    private var homeTab: XCUIElement {
        hideKeyboard()
        let inBar = app.tabBars.buttons["الرئيسية"]
        return inBar.exists ? inBar : app.buttons["الرئيسية"].firstMatch
    }

    /// يخفي لوحة المفاتيح قبل الضغط على أزرار الشريط السفلي (حتى لا يقع الضغط على مفاتيحها).
    private func hideKeyboard() {
        guard app.keyboards.firstMatch.exists else { return }
        let list = element("searchList")
        if list.exists { list.swipeDown(velocity: .slow) }
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
            if app.tabBars.buttons["المزيد"].exists || app.tabBars.buttons["More"].exists {
                issue("ظهر تبويب المزيد فوق القسيمة وأضيف شريط تنقّل ثانٍ")
            }
            if !searchTab.exists { issue("اختفى تبويب البحث عند فتح القسيمة") }
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

    // MARK: - اختيار ملف من نافذة "الملفات" (كما على الجهاز)

    /// يضغط أول عنصر يحتوي أحد النصوص (عناصر نافذة الملفات تختلف حسب اللغة).
    @discardableResult
    private func tapLabel(_ texts: [String], _ what: String, timeout: TimeInterval = 6, in query: XCUIElementQuery? = nil) -> Bool {
        let predicate = NSPredicate(format: texts.map { _ in "label CONTAINS %@" }.joined(separator: " OR "), argumentArray: texts)
        let e = (query ?? app.descendants(matching: .any)).matching(predicate).firstMatch
        guard e.waitForExistence(timeout: timeout) else { print("PICKER missing: \(what)"); return false }
        e.tap()
        return true
    }

    private func pickFile(_ name: String) -> Bool {
        step("فتح الإعدادات للاستيراد") {
            settingsTab.tap()
            let button = element("importFilesButton")
            wait(button, "زر استيراد ملفات")
            if !button.isHittable { issue("زر الاستيراد يحتاج تمريراً: الإعدادات ليست في صفحة واحدة") }
            button.tap()
        }
        var picked = false
        step("اختيار \(name) من نافذة الملفات") {
            // ننتظر نافذة الملفات نفسها (زر "تصفح" في شريطها السفلي) قبل البحث داخلها.
            guard tapLabel(["تصفح", "Browse"], "تبويب تصفح", timeout: 20, in: app.buttons) else { return }
            sleep(1)
            let fileCell = app.cells.matching(NSPredicate(format: "label CONTAINS %@", name)).firstMatch
            if !fileCell.waitForExistence(timeout: 3) {
                tapLabel(["iPhone"], "على iPhone", in: app.cells)
                tapLabel(["الرواتب", "SalaryApp"], "مجلد التطبيق", in: app.cells)
            }
            picked = tapLabel([name], "الملف \(name)", timeout: 8, in: app.cells)
            if picked {
                // زر "فتح" في أعلى النافذة؛ نعيد المحاولة إن لم يُغلق.
                for _ in 0..<3 {
                    let open = app.buttons.matching(NSPredicate(format: "label == %@ OR label == %@", "فتح", "Open")).firstMatch
                    guard open.waitForExistence(timeout: 4) else { break }
                    open.tap()
                    sleep(2)
                    if !open.exists { break }
                }
            }
        }
        return picked
    }

    func testImportThroughPicker() {
        step("تشغيل التطبيق") {
            app.launch()
            wait(app.navigationBars["الرئيسية"], "الشاشة الرئيسية", timeout: 30)
        }
        guard pickFile("رواتب آب 2026") else { issue("تعذّر اختيار الملف من نافذة الملفات (عناصر النافذة لم تُعثر)"); return }
        step("ظهور الفحص أو المراجعة بعد الاختيار") {
            let progress = element("importProgress"), review = element("importAll")
            let start = Date()
            while Date().timeIntervalSince(start) < 25 && !progress.exists && !review.exists { usleep(200_000) }
            if !progress.exists && !review.exists { issue("بعد اختيار الملف لم يظهر شريط الفحص ولا نافذة المراجعة") }
            if review.waitForExistence(timeout: 30) {
                review.tap()
                if app.alerts.firstMatch.waitForExistence(timeout: 30) {
                    print("IMPORT alert: \(app.alerts.firstMatch.label) \(app.alerts.firstMatch.staticTexts.allElementsBoundByIndex.map(\.label))")
                    app.alerts.firstMatch.buttons.firstMatch.tap()
                } else { issue("لم تظهر رسالة نتيجة الاستيراد") }
            } else { issue("لم تظهر نافذة المراجعة") }
        }
        step("انتظار انتهاء النسخ الاحتياطي التلقائي") {
            let progress = element("importProgress")
            let start = Date()
            while progress.exists && Date().timeIntervalSince(start) < 60 { usleep(300_000) }
            sleep(1)
        }
        guard pickFile("ملف غير متوافق") else { issue("تعذّر اختيار الملف غير المتوافق"); return }
        step("تنبيه الملف غير المتوافق") {
            let alert = app.alerts.firstMatch
            if alert.waitForExistence(timeout: 30) {
                let text = alert.staticTexts.allElementsBoundByIndex.map(\.label).joined(separator: " | ")
                print("REJECT alert: \(text)")
                if !text.contains("غير متوافق") { issue("رسالة الرفض لا تذكر السبب: \(text)") }
                alert.buttons.firstMatch.tap()
            } else { issue("لم يظهر تنبيه للملف غير المتوافق") }
        }
        print("RESULT import issues=\(issues.count)")
    }

    // MARK: - الاختبار

    /// يختبر شريط النظام والبحث والعودة والمشاركة دون فرض مقاس مخصص عليه.
    func testSystemNavigationBar() {
        let baseArguments = app.launchArguments + ["-unifiedMode", "name"]
        app.launchArguments = baseArguments + ["-skipLock", "YES", "-tab", "settings", "-openSettings", "NO",
                                               "-search", "", "-openFirst", "NO", "-openPerson", "NO", "-share", "NO"]
        app.launch()
        XCTAssertTrue(app.tabBars.firstMatch.waitForExistence(timeout: 30))
        XCTAssertTrue(settingsTab.isHittable)
        XCTAssertFalse(app.tabBars.buttons["مشاركة"].exists)
        homeTab.tap()
        element("homeReportsEntry").tap()
        XCTAssertTrue(app.navigationBars["التقارير"].waitForExistence(timeout: 8))
        element("closeReports").tap()
        XCTAssertTrue(app.navigationBars["الرئيسية"].waitForExistence(timeout: 8))
        searchTab.tap()
        XCTAssertTrue(searchField.waitForExistence(timeout: 8))
        type("احمد علي")
        XCTAssertTrue(element("resultCard").waitForExistence(timeout: 10))
        homeTab.tap()
        searchTab.tap()
        XCTAssertTrue(searchField.waitForExistence(timeout: 8))
        app.terminate()
        app.launchArguments = baseArguments + ["-skipLock", "YES", "-tab", "search", "-openSettings", "NO",
                                               "-search", "أحمد علي حسين الدليمي", "-openFirst", "NO", "-openPerson", "NO", "-share", "NO"]
        app.launch()
        let result = element("resultCard")
        XCTAssertTrue(result.waitForExistence(timeout: 20))
        result.press(forDuration: 1.0)
        let deductionAction = app.buttons["كشف الاستقطاعات"]
        XCTAssertTrue(deductionAction.waitForExistence(timeout: 5))
        deductionAction.tap()
        let selectedPerson = element("deductionSelectedPerson")
        XCTAssertTrue(selectedPerson.waitForExistence(timeout: 10))
        XCTAssertTrue(selectedPerson.label.contains("أحمد علي حسين الدليمي"))
        app.buttons["إغلاق"].tap()
        app.terminate()
        app.launchArguments = baseArguments + ["-skipLock", "YES", "-tab", "search", "-openSettings", "NO",
                                               "-search", "أحمد علي حسين الدليمي", "-openFirst", "NO",
                                               "-openPerson", "YES", "-share", "NO"]
        app.launch()
        let payslip = element("personPayslip")
        XCTAssertTrue(payslip.waitForExistence(timeout: 20))
        XCTAssertFalse(app.tabBars.buttons["مشاركة"].exists)
        XCTAssertFalse(app.tabBars.buttons["المزيد"].exists || app.tabBars.buttons["More"].exists)
        XCTAssertTrue(searchTab.exists)
        payslip.press(forDuration: 1.0)
        let image = app.buttons["قسيمة صورة"]
        XCTAssertTrue(image.waitForExistence(timeout: 5))
        image.tap()
        XCTAssertTrue(element("shareModePicker").waitForExistence(timeout: 8))
    }

    func testReportActions() {
        app.launchArguments += ["-skipLock", "YES", "-tab", "reports", "-reportByEntity", "NO",
                                "-openReport", "YES", "-openReportTitle", "تنفيذ الرمادي"]
        app.launch()
        let preview = element("previewReport")
        XCTAssertTrue(preview.waitForExistence(timeout: 30))
        XCTAssertTrue(element("exportReport").isHittable)
        preview.tap()
        XCTAssertTrue(element("reportPDFPreview").waitForExistence(timeout: 25))
        XCTAssertTrue(element("previewExport").isHittable)
        app.navigationBars.buttons["إغلاق"].tap()
        XCTAssertTrue(element("exportReport").waitForExistence(timeout: 8))
        element("exportReport").tap()
        XCTAssertTrue(app.navigationBars["تصدير الكشف"].waitForExistence(timeout: 15))
        XCTAssertTrue(element("reportListExcel").waitForExistence(timeout: 25))
        XCTAssertTrue(element("reportListPDF").isHittable)
    }

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
        step("فتح الإعدادات من الشريط السفلي") {
            settingsTab.tap()
            wait(app.navigationBars["الإعدادات"], "الإعدادات")
        }
        step("الإعدادات في صفحة واحدة") {
            let backup = element("backupRow")
            if wait(backup, "صف النسخ الاحتياطي"), !backup.isHittable { issue("الإعدادات تحتاج تمريراً") }
            backup.tap()
            wait(element("backupStatus"), "صفحة النسخ الاحتياطي")
            app.navigationBars.buttons.element(boundBy: 0).tap()
        }
        step("التقارير: بطاقة جهات التنفيذ ثم المعاينة") {
            homeTab.tap()
            let entry = element("homeReportsEntry")
            if wait(entry, "بطاقة التقارير") { entry.tap() }
            wait(app.navigationBars["التقارير"], "التقارير")
            wait(element("reportsMonthMenu"), "زر شهر التقارير")
            let card = element("entitiesCard")
            if wait(card, "بطاقة جهات التنفيذ") {
                card.tap()
                let row = element("reportGroup")
                if wait(row, "قائمة الجهات") {
                    row.tap()
                    let preview = element("previewReport")
                    if wait(preview, "زر معاينة الجدول") {
                        preview.tap()
                        wait(element("reportPDFPreview"), "معاينة الجدول", timeout: 20)
                        app.swipeDown(velocity: .fast)
                    }
                    app.navigationBars.buttons.element(boundBy: 0).tap()
                }
                app.navigationBars.buttons.element(boundBy: 0).tap()
            }
        }
        step("التقارير: نافذة التصدير") {
            let card = element("entitiesCard")
            if wait(card, "بطاقة جهات التنفيذ") { card.tap() }
            let export = element("deductionExportButton")
            if wait(export, "زر التصدير") {
                export.tap()
                let field = element("exportSearchField")
                if wait(field, "بحث الجهة في نافذة التصدير") {
                    field.tap()
                    field.typeText("فرع")
                }
                let group = element("exportModePicker")
                if group.exists { group.buttons["مجموعة جهات"].tap() }
                wait(element("exportCategory-directorates"), "خيارات المجموعات")
                app.swipeDown(velocity: .fast)
            }
            element("closeReports").tap()
            wait(app.navigationBars["الرئيسية"], "الرئيسية بعد التقارير")
        }

        step("تبويب البحث") {
            searchTab.tap()
            wait(searchField, "شريط البحث")
        }
        // عند تفعيل البحث يخفي النظام شريط العنوان؛ نتحقق من زر النوع قبل الكتابة.
        step("نوع البحث") {
            let menu = element("searchModeMenu")
            guard wait(menu, "زر نوع البحث") else { return }
            menu.tap()
            let mother = app.buttons["اسم الأم"]
            guard wait(mother, "خيار اسم الأم") else { return }
            mother.tap()
            guard wait(menu, "زر نوع البحث بعد الاختيار") else { return }
            menu.tap()
            let name = app.buttons["الاسم"]
            if wait(name, "خيار الاسم") { name.tap() }
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

        step("تضييق البحث") {
            searchField.tap()
            if !app.keyboards.firstMatch.waitForExistence(timeout: 2) { searchField.tap() }
            guard app.keyboards.firstMatch.waitForExistence(timeout: 2) else {
                issue("حقل البحث لم يستقبل التركيز للكتابة بعد إغلاق قائمة نوع البحث")
                return
            }
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

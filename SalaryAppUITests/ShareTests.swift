import XCTest

/// اختبار المشاركة من البداية للنهاية: تصدير كشف PDF ثم "حفظ في الملفات" عبر نافذة المشاركة الحقيقية،
/// ثم "نسخ". سير العمل (simulator.yml) يتحقق بعدها أن الملف المحفوظ في "الملفات" سليم (%PDF ونفس الحجم)
/// وأن الحافظة فيها محتوى الملف.
/// يعمل بلغة النظام الإنجليزية حتى تكون أسماء أزرار نافذة المشاركة ونافذة الملفات ثابتة.
final class ShareTests: XCTestCase {
    private let app = XCUIApplication()

    override func setUp() {
        continueAfterFailure = false
        app.launchArguments = ["-AppleLanguages", "(en)", "-AppleLocale", "en_US", "-skipLock", "YES",
                               "-shareTest", "YES", "-tab", "reports", "-reportByEntity", "NO",
                               "-openReport", "YES", "-openReportTitle", "تنفيذ الرمادي"]
    }

    private func element(_ id: String) -> XCUIElement {
        app.descendants(matching: .any).matching(identifier: id).firstMatch
    }

    /// يفتح نافذة تصدير الكشف ثم نافذة المشاركة لملف PDF.
    private func openShareSheet(_ button: String = "reportListPDF") {
        let export = element("exportReport")
        XCTAssertTrue(export.waitForExistence(timeout: 40), "زر التصدير")
        export.tap()
        let pdf = element(button)
        XCTAssertTrue(pdf.waitForExistence(timeout: 40), "زر الملف")
        pdf.tap()
    }

    /// زر في نافذة المشاركة (قد يحتاج تمريراً لقائمة الإجراءات).
    private func shareAction(_ labels: [String]) -> XCUIElement? {
        for _ in 0..<4 {
            for label in labels {
                let candidates = [app.buttons[label], app.cells[label], app.staticTexts[label]]
                if let hit = candidates.first(where: { $0.waitForExistence(timeout: 2) }) { return hit }
            }
            app.swipeUp()
        }
        return nil
    }

    func testSaveToFiles() { saveToFiles("reportListPDF") }

    func testSaveExcelToFiles() { saveToFiles("reportListExcel") }

    private func saveToFiles(_ button: String) {
        app.launch()
        openShareSheet(button)
        guard let save = shareAction(["Save to Files"]) else {
            XCTFail("Save to Files not found in the share sheet")
            return
        }
        save.tap()
        // نافذة الملفات: اختيار "On My iPhone" إن ظهر، ثم "Save".
        let onDevice = app.buttons.matching(NSPredicate(format: "label CONTAINS 'On My'")).firstMatch
        if onDevice.waitForExistence(timeout: 6) { onDevice.tap() }
        let saveButton = app.buttons["Save"].firstMatch
        XCTAssertTrue(saveButton.waitForExistence(timeout: 15), "Save button")
        saveButton.tap()
        // استبدال ملف بنفس الاسم من تشغيل سابق.
        let replace = app.buttons["Replace"].firstMatch
        if replace.waitForExistence(timeout: 3) { replace.tap() }
        sleep(3)
        print("SHARE saved-to-files")
    }

    func testCopy() {
        app.launch()
        openShareSheet()
        guard let copy = shareAction(["Copy"]) else {
            XCTFail("Copy not found in the share sheet")
            return
        }
        copy.tap()
        sleep(3)
        print("SHARE copied")
    }
}

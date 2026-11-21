import XCTest

final class RefundInputTests: XCTestCase {
    private let app = XCUIApplication()

    private func element(_ identifier: String) -> XCUIElement {
        app.descendants(matching: .any).matching(identifier: identifier).firstMatch
    }

    func testRefundAmountAndHistoryMenu() throws {
        continueAfterFailure = false
        app.launchArguments = ["-AppleLanguages", "(ar)", "-AppleLocale", "ar_IQ", "-skipLock", "YES", "-tab", "search", "-search", "أحمد علي حسين الدليمي",
                                "-bankStatementIBAN", "IQ90RAFB000000000000099", "-bankStatementAccountName", "دافع تجريبي"]
        app.launch()
        func reveal(_ item: XCUIElement, towardTop: Bool = false) {
            for _ in 0..<7 {
                if item.exists && item.isHittable && app.frame.contains(CGPoint(x: item.frame.midX, y: item.frame.midY)) { return }
                if towardTop { app.swipeDown() } else { app.swipeUp() }
            }
            XCTAssertTrue(item.exists && item.isHittable, app.debugDescription)
        }
        func capture(_ name: String) {
            let shot = XCTAttachment(screenshot: app.screenshot())
            shot.name = name
            shot.lifetime = .keepAlways
            add(shot)
        }
        let card = element("resultCard")
        XCTAssertTrue(card.waitForExistence(timeout: 45))
        card.press(forDuration: 1)
        element("searchRefundAction").tap()
        let searchAmount = element("searchRefundAmount")
        XCTAssertTrue(searchAmount.waitForExistence(timeout: 20))
        reveal(searchAmount)
        searchAmount.tap()
        app.typeText("7")
        XCTAssertEqual(searchAmount.value as? String, "7")
        app.typeText("500")
        XCTAssertEqual(searchAmount.value as? String, "7500")
        capture("search-amount-7500")
        element("refundAmountDone").tap()
        let saveFromSearch = element("saveSearchRefund")
        XCTAssertTrue(saveFromSearch.waitForExistence(timeout: 10), app.debugDescription)
        saveFromSearch.tap()
        element("viewSearchRefundBatch").tap()

        let amount = element("refundRecipientAmount")
        reveal(amount)
        amount.tap()
        app.typeText("1")
        let first = try XCTUnwrap(amount.value as? String)
        XCTAssertEqual(first.count, 5)
        app.typeText("2")
        let typed = try XCTUnwrap(amount.value as? String)
        XCTAssertEqual(typed.count, 6)
        XCTAssertTrue(typed.contains("12"))
        capture("batch-amount-editing")
        element("refundAmountDone").tap()
        XCTAssertEqual(amount.value as? String, typed)
        let save = app.buttons["حفظ الدفعة بتاريخ التحويل"]
        reveal(save)
        save.tap()
        let alert = app.alerts.firstMatch
        XCTAssertTrue(alert.waitForExistence(timeout: 15))
        XCTAssertTrue(alert.label.contains("حُفظت") || alert.staticTexts.containing(NSPredicate(format: "label CONTAINS %@", "حُفظت")).count > 0, alert.debugDescription)
        alert.buttons.firstMatch.tap()
        let history = element("refundHistory")
        reveal(history, towardTop: true)
        history.tap()
        let row = element("refundHistoryRow")
        XCTAssertTrue(row.waitForExistence(timeout: 10))
        row.press(forDuration: 1)
        XCTAssertTrue(element("editSavedRefund").waitForExistence(timeout: 5))
        capture("history-actions")
        element("editSavedRefund").tap()
        reveal(amount)
        XCTAssertEqual(amount.value as? String, typed)
        amount.tap()
        app.typeText("3")
        let edited = try XCTUnwrap(amount.value as? String)
        XCTAssertEqual(edited.count, typed.count + 1)
        capture("archive-amount-editing")
        element("refundAmountDone").tap()
        app.buttons["حفظ التعديلات"].tap()
        XCTAssertTrue(row.waitForExistence(timeout: 15))
        row.press(forDuration: 1)
        element("deleteSavedRefund").tap()
        XCTAssertTrue(alert.waitForExistence(timeout: 5))
        alert.buttons["إلغاء"].tap()
        XCTAssertTrue(row.exists)
        row.press(forDuration: 1)
        element("deleteSavedRefund").tap()
        alert.buttons["حذف الدفعة"].tap()
        XCTAssertTrue(app.staticTexts["لا توجد دفعات محفوظة."].waitForExistence(timeout: 10))
    }
}

import AppKit
import CoreGraphics

typealias UIColor = NSColor
private var previewContext: CGContext?
private var previousContexts: [(CGContext?, NSGraphicsContext?)] = []
func UIGraphicsGetCurrentContext() -> CGContext? { previewContext }
func UIGraphicsPushContext(_ context: CGContext) {
    previousContexts.append((previewContext, NSGraphicsContext.current))
    previewContext = context
    NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
}
func UIGraphicsPopContext() {
    let previous = previousContexts.removeLast()
    previewContext = previous.0
    NSGraphicsContext.current = previous.1
}

/// سياق PDF مستقل على macOS يستعمل إحداثيات UIKit حتى يظل كود الرسم الإنتاجي كما هو.
final class UIGraphicsPDFRendererContext {
    let cgContext: CGContext
    private var pageOpen = false
    init(_ context: CGContext) { cgContext = context }
    func beginPage(withBounds bounds: CGRect, pageInfo: [String: Any]) {
        finishPage()
        var box = bounds
        let data = Data(bytes: &box, count: MemoryLayout<CGRect>.size)
        cgContext.beginPDFPage([kCGPDFContextMediaBox as String: data] as CFDictionary)
        cgContext.saveGState()
        cgContext.translateBy(x: 0, y: bounds.height)
        cgContext.scaleBy(x: 1, y: -1)
        UIGraphicsPushContext(cgContext)
        pageOpen = true
    }
    func finishPage() {
        guard pageOpen else { return }
        UIGraphicsPopContext()
        cgContext.restoreGState()
        cgContext.endPDFPage()
        pageOpen = false
    }
}

import AppKit

typealias UIFont = NSFont

struct UIImage {
    let cgImage: CGImage?
    init?(named name: String) {
        guard let image = NSImage(contentsOfFile: "SalaryApp/Assets.xcassets/\(name).imageset/\(name).png") else { return nil }
        cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil)
    }
}

struct UIGraphicsPDFRenderer {
    let bounds: CGRect
    func writePDF(to url: URL, actions: (UIGraphicsPDFRendererContext) -> Void) throws {
        var media = bounds
        guard let cg = CGContext(url as CFURL, mediaBox: &media, nil) else { throw CocoaError(.fileWriteUnknown) }
        let context = UIGraphicsPDFRendererContext(cg)
        actions(context)
        context.finishPage()
        cg.closePDF()
    }
}

extension UIGraphicsPDFRendererContext {
    func beginPage() { beginPage(withBounds: CGRect(x: 0, y: 0, width: 595.3, height: 841.9), pageInfo: [:]) }
}

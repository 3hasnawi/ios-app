import SwiftUI
import WebKit

/// معاينة المستند محلياً داخل التطبيق، دون أزرار مشاركة أثناء التدقيق.
struct TransferWordPreview: UIViewRepresentable {
    let url: URL
    @Binding var loadFailed: Bool
    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.defaultWebpagePreferences.allowsContentJavaScript = false
        configuration.websiteDataStore = .nonPersistent()
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.navigationDelegate = context.coordinator
        view.isOpaque = false
        view.backgroundColor = .systemBackground
        context.coordinator.load(url, into: view)
        return view
    }
    func updateUIView(_ view: WKWebView, context: Context) {
        context.coordinator.load(url, into: view)
    }
    func makeCoordinator() -> Coordinator { Coordinator(loadFailed: $loadFailed) }
    final class Coordinator: NSObject, WKNavigationDelegate {
        private let loadFailed: Binding<Bool>
        private var loaded: URL?
        init(loadFailed: Binding<Bool>) { self.loadFailed = loadFailed; super.init() }
        func load(_ url: URL, into view: WKWebView) {
            guard loaded != url else { return }
            loaded = url
            view.loadFileURL(url, allowingReadAccessTo: url.deletingLastPathComponent())
        }
        func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { report(error) }
        func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { report(error) }
        func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
            DispatchQueue.main.async { self.loadFailed.wrappedValue = true }
        }
        private func report(_ error: Error) {
            guard (error as NSError).code != NSURLErrorCancelled else { return }
            DispatchQueue.main.async { self.loadFailed.wrappedValue = true }
        }
        func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                     decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            guard let url = navigationAction.request.url else { decisionHandler(.cancel); return }
            decisionHandler(url.isFileURL || url.scheme == "about" ? .allow : .cancel)
        }
    }
}

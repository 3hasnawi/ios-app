import SwiftUI
import UIKit

/// نرسم المؤشر فوق شريط العنوان الظاهر حتى تبقى أزرار التنقل وعنوان الصفحة في مواضعها.
struct SyncTransferIndicator: UIViewRepresentable {
    func makeUIView(context: Context) -> Anchor { Anchor() }
    func updateUIView(_ view: Anchor, context: Context) { view.updateIndicator() }
    static func dismantleUIView(_ view: Anchor, coordinator: ()) { view.stop() }

    final class Anchor: UIView {
        private let symbol = UIImageView(image: UIImage(systemName: "arrow.triangle.2.circlepath",
                                                         withConfiguration: UIImage.SymbolConfiguration(pointSize: 12, weight: .semibold)))
        private var timer: Timer?

        override func didMoveToWindow() {
            super.didMoveToWindow()
            stop()
            guard window != nil else { return }
            isUserInteractionEnabled = false
            symbol.contentMode = .scaleAspectFit
            symbol.isUserInteractionEnabled = false
            symbol.isAccessibilityElement = true
            symbol.accessibilityLabel = tr("جارٍ نقل بيانات المزامنة", "Transferring sync data")
            updateIndicator()
            // يتغير شريط العنوان عند التنقل أو فتح نافذة أثناء نقل دفعة طويلة.
            let timer = Timer(timeInterval: 0.25, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.updateIndicator() }
            }
            self.timer = timer
            RunLoop.main.add(timer, forMode: .common)
        }

        func stop() {
            timer?.invalidate()
            timer = nil
            symbol.layer.removeAllAnimations()
            symbol.removeFromSuperview()
        }

        private func visibleBar(in controller: UIViewController) -> UINavigationBar? {
            guard let view = controller.viewIfLoaded, view.window === window, !view.isHidden else { return nil }
            if let modal = controller.presentedViewController, !modal.isBeingDismissed {
                return visibleBar(in: modal)
            }
            if let tabs = controller as? UITabBarController, let selected = tabs.selectedViewController {
                return visibleBar(in: selected)
            }
            if let nav = controller as? UINavigationController {
                if let child = nav.visibleViewController, let inner = visibleBar(in: child) { return inner }
                return nav.isNavigationBarHidden || nav.navigationBar.isHidden ? nil : nav.navigationBar
            }
            for child in controller.children.reversed() {
                if let bar = visibleBar(in: child) { return bar }
            }
            return nil
        }

        func updateIndicator() {
            guard let window, window.windowScene?.activationState == .foregroundActive,
                  let root = window.rootViewController, let bar = visibleBar(in: root),
                  bar.bounds.height >= 28 else {
                symbol.removeFromSuperview()
                return
            }
            if symbol.superview !== bar { bar.addSubview(symbol) }
            symbol.tintColor = bar.traitCollection.userInterfaceStyle == .dark
                ? UIColor(red: 0.47, green: 0.84, blue: 0.75, alpha: 1)
                : UIColor(red: 0.08, green: 0.46, blue: 0.37, alpha: 1)
            let side: CGFloat = bar.bounds.height < 40 ? 8 : 12
            symbol.bounds = CGRect(x: 0, y: 0, width: side, height: side)
            symbol.center = CGPoint(x: bar.bounds.midX, y: bar.bounds.maxY - side / 2)
            if UIAccessibility.isReduceMotionEnabled {
                symbol.layer.removeAnimation(forKey: "syncRotation")
            } else if symbol.layer.animation(forKey: "syncRotation") == nil {
                let rotation = CABasicAnimation(keyPath: "transform.rotation.z")
                rotation.fromValue = 0
                rotation.toValue = Double.pi * 2
                rotation.duration = 1.3
                rotation.repeatCount = .infinity
                symbol.layer.add(rotation, forKey: "syncRotation")
            }
        }
    }
}

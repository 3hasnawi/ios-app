using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SalaryDesktop;

/// قفل البرنامج: عند الفتح وبعد مدة عدم استخدام، تغطي طبقة القفل كل النوافذ المفتوحة وتُعطّل محتواها حتى تُدخل كلمة السر
/// أو يُتحقق بـ Windows Hello. يحمي من استعمال جلسة ويندوز مفتوحة على جهاز مشترك؛ حماية الملفات نفسها من تشفير ويندوز (EFS).
public static class Locker
{
    public static bool IsLocked { get; private set; }
    private static DateTime lastInput = DateTime.UtcNow;
    private static DispatcherTimer? timer;
    private static readonly Dictionary<UIElement, bool> disabled = new();
    private static bool started;
    private static int failures;
    private const int Iterations = 200_000;

    public static bool Enabled => UiPreferences.Current.LockEnabled && UiPreferences.Current.LockHash is not null;

    public static void Start()
    {
        if (started) return;
        started = true;
        InputManager.Current.PreProcessInput += (_, e) => { if (e.StagingItem.Input is KeyboardEventArgs or MouseButtonEventArgs or MouseWheelEventArgs) lastInput = DateTime.UtcNow; };
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) =>
        {
            var minutes = UiPreferences.Current.LockMinutes;
            if (!IsLocked && Enabled && minutes > 0 && DateTime.UtcNow - lastInput > TimeSpan.FromMinutes(minutes)) Lock();
        };
        timer.Start();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => { if (IsLocked && sender is Window window) Cover(window); }));
        if (Enabled && !App.Unattended) Lock();
    }

    public static void Lock()
    {
        if (!Enabled) return;
        IsLocked = true;
        foreach (Window window in Application.Current.Windows) Cover(window);
    }

    private static void Cover(Window window)
    {
        if (window.Content is not UIElement content || AdornerLayer.GetAdornerLayer(content) is not { } layer) return;
        if (layer.GetAdorners(content)?.OfType<LockAdorner>().Any() == true) return;
        if (!disabled.ContainsKey(content)) { disabled[content] = content.IsEnabled; content.IsEnabled = false; }
        var adorner = new LockAdorner(content, window);
        layer.Add(adorner);
        if (window.IsActive || window == Application.Current.MainWindow) adorner.FocusPassword();
    }

    private static void Uncover()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window.Content is not UIElement content || AdornerLayer.GetAdornerLayer(content) is not { } layer) continue;
            foreach (var adorner in layer.GetAdorners(content)?.OfType<LockAdorner>().ToArray() ?? []) layer.Remove(adorner);
        }
        foreach (var (element, wasEnabled) in disabled) element.IsEnabled = wasEnabled;
        disabled.Clear();
    }

    private static void Unlock() { IsLocked = false; failures = 0; lastInput = DateTime.UtcNow; Uncover(); }

    // ——— كلمة السر ———

    private static string Hash(string password, byte[] salt) => Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32));

    /// تفعيل القفل بكلمة سر وإيقافه (الحفظ في ui.json على من يستدعي).
    public static void Enable(string password) { SetPassword(password); UiPreferences.Current.LockEnabled = true; }
    public static void Disable() { UiPreferences.Current.LockEnabled = false; UiPreferences.Current.LockHello = false; if (IsLocked) Unlock(); }

    public static void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        UiPreferences.Current.LockSalt = Convert.ToBase64String(salt);
        UiPreferences.Current.LockHash = Hash(password, salt);
    }

    public static bool Check(string password)
    {
        var saved = UiPreferences.Current;
        if (saved.LockHash is null || saved.LockSalt is null) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(saved.LockHash), Convert.FromBase64String(Hash(password, Convert.FromBase64String(saved.LockSalt)))); }
        catch (FormatException) { return false; }
    }

    public static bool TryUnlock(string password, out string message)
    {
        if (failures >= 5 && DateTime.UtcNow < blockedUntil) { message = "محاولات كثيرة؛ انتظر قليلاً ثم أعد المحاولة."; return false; }
        if (Check(password)) { message = ""; Unlock(); return true; }
        failures++;
        if (failures >= 5) blockedUntil = DateTime.UtcNow.AddSeconds(30);
        message = "كلمة السر غير صحيحة.";
        return false;
    }
    private static DateTime blockedUntil;

    // ——— Windows Hello ———

    public static async Task<bool> HelloAvailable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return false;
        try { return await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync() == Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// يطلب التحقق بـ Windows Hello (البصمة أو الوجه أو رمز PIN) فوق النافذة المعطاة.
    public static async Task<bool> Hello(Window owner, string message)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return false;
        try
        {
            var handle = new WindowInteropHelper(owner).Handle;
            var result = await Windows.Security.Credentials.UI.UserConsentVerifierInterop.RequestVerificationForWindowAsync(handle, message);
            return result == Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    internal static async Task<bool> TryHello(Window owner)
    {
        if (!UiPreferences.Current.LockHello || !await Hello(owner, "فتح تطبيق الرواتب")) return false;
        Unlock();
        return true;
    }

    // ——— تشفير بيانات البرنامج لحساب ويندوز (EFS) ———

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SalaryDesktop");
    public static bool DataEncrypted => Directory.Exists(DataFolder) && (File.GetAttributes(DataFolder) & FileAttributes.Encrypted) != 0;

    /// يشفّر المجلد (فتُشفّر الملفات الجديدة تلقائياً) والملفات الموجودة. لا يعمل على ويندوز Home.
    public static (int Files, int Skipped) EncryptData()
    {
        Directory.CreateDirectory(DataFolder);
        File.Encrypt(DataFolder);
        int files = 0, skipped = 0;
        foreach (var directory in Directory.GetDirectories(DataFolder, "*", SearchOption.AllDirectories))
        {
            try { if ((File.GetAttributes(directory) & FileAttributes.Encrypted) == 0) File.Encrypt(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        foreach (var file in Directory.GetFiles(DataFolder, "*", SearchOption.AllDirectories))
        {
            try { if ((File.GetAttributes(file) & FileAttributes.Encrypted) == 0) { File.Encrypt(file); files++; } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return (files, skipped);
    }
}

/// طبقة القفل فوق محتوى نافذة: تغطيه بالكامل وتعرض حقل كلمة السر وزر Windows Hello.
internal sealed class LockAdorner : Adorner
{
    private readonly Border panel;
    private readonly PasswordBox password = new() { Width = 260, Height = 38, FontSize = 15, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 4, 8, 4) };
    private readonly TextBlock message = new() { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0), MinHeight = 18 };

    public LockAdorner(UIElement element, Window window) : base(element)
    {
        message.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380 };
        stack.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/SalaryDesktop;component/Assets/AppIcon.png")), Width = 72, Height = 72, Margin = new Thickness(0, 0, 0, 16) });
        var title = new TextBlock { Text = "البرنامج مقفل", FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var hint = new TextBlock { Text = "أدخل كلمة سر البرنامج للمتابعة. النوافذ المفتوحة باقية كما هي.", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 16) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        stack.Children.Add(title); stack.Children.Add(hint); stack.Children.Add(password);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        var open = new Button { Content = "فتح", MinWidth = 110, IsDefault = true };
        open.SetResourceReference(StyleProperty, "ActionButton");
        open.Click += (_, _) => Submit();
        buttons.Children.Add(open);
        if (UiPreferences.Current.LockHello)
        {
            var hello = new Button { Content = "Windows Hello", Margin = new Thickness(8, 0, 0, 0) };
            hello.Click += async (_, _) => { if (!await Locker.TryHello(window)) message.Text = "لم يتم التحقق بـ Windows Hello."; };
            buttons.Children.Add(hello);
        }
        stack.Children.Add(buttons); stack.Children.Add(message);
        password.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
        panel = new Border { Child = stack, FlowDirection = FlowDirection.RightToLeft };
        panel.SetResourceReference(Border.BackgroundProperty, "Page");
        panel.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        panel.SetResourceReference(TextElement.FontFamilyProperty, "AppFont");
        AddVisualChild(panel);
    }

    private void Submit()
    {
        if (!Locker.TryUnlock(password.Password, out var text)) { message.Text = text; password.Clear(); password.Focus(); }
    }
    public void FocusPassword() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { password.Focus(); Keyboard.Focus(password); });

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => panel;
    protected override Size MeasureOverride(Size constraint) { panel.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
    protected override Size ArrangeOverride(Size finalSize) { panel.Arrange(new Rect(AdornedElement.RenderSize)); return finalSize; }
}

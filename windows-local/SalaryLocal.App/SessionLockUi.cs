using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SalaryLocal.Core;

namespace SalaryLocal.App;

/// فصل المحتوى يمنع عرضه والتنقل داخله أثناء القفل، حتى في النوافذ التابعة.
internal sealed class SessionLockUi : IDisposable
{
    internal bool IsLocked => lockedSnapshot;
    internal event Action? Unlocked;
    private static SessionLockUi? active;
    private volatile bool lockedSnapshot;
    private long generation;
    private bool lockObserved;
    private Window? nativeUnlockWindow;
    private int nativeDepth;
    private readonly Window owner;
    internal static void RequireUnlocked()
    {
        if (Volatile.Read(ref active)?.lockedSnapshot == true)
            throw new OperationCanceledException("أُلغيت العملية لأن الجلسة مقفلة. افتحها وأعد الطلب.");
    }
    internal static Action CaptureUnlockedSession()
    {
        var instance = Volatile.Read(ref active);
        RequireUnlocked();
        var captured = instance is null ? 0 : Interlocked.Read(ref instance.generation);
        return () => {
            RequireUnlocked();
            if (instance is not null && Interlocked.Read(ref instance.generation) != captured)
                throw new OperationCanceledException("أُلغيت العملية لأن الجلسة قُفلت أثناءها. أعد الطلب بعد الفتح.");
        };
    }
    internal static IDisposable BeginNativeDialog()
    {
        RequireUnlocked();
        var instance = Volatile.Read(ref active);
        if (instance is not null) instance.nativeDepth++;
        return new NativeScope(instance);
    }
    private sealed class NativeScope(SessionLockUi? instance) : IDisposable
    {
        public void Dispose() { if (instance is not null) instance.nativeDepth--; }
    }
    private readonly LocalSessionLock? state;
    private readonly string? startupError;
    private readonly Dictionary<Window, (object? Content, string Title)> covered = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool disposed;
    private bool Locked => startupError is not null || state?.IsLocked == true;

    public SessionLockUi(Window owner, string path)
    {
        this.owner = owner;
        try { state = new LocalSessionLock(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { startupError = "تعذّر قراءة إعدادات القفل. أُخفيت الواجهة ولم يُتجاوز القفل. أغلق التطبيق وراجع ملف القفل المحلي مع مسؤول الجهاز."; }
        active = this; lockedSnapshot = Locked;
        if (Locked) { generation = 1; lockObserved = true; }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
        InputManager.Current.PreProcessInput += Input;
        timer.Tick += Tick; timer.Start();
        if (Locked) Cover(owner);
    }
    private void WindowLoaded(object sender, RoutedEventArgs e) { if (!disposed && Locked && sender is Window window) Cover(window); }
    private void Input(object sender, PreProcessInputEventArgs e)
    {
        if (!Locked && e.StagingItem.Input is KeyboardEventArgs or MouseEventArgs or TouchEventArgs) state?.RecordActivity();
    }
    private void Tick(object? sender, EventArgs e) { if (state?.CheckIdle() == true) CoverAll(); }
    internal void LockNow()
    {
        if (state?.Enabled != true) { Ui.Message(owner, "فعّل كلمة سر الجلسة من الإعدادات أولاً."); return; }
        state.Lock(); CoverAll();
    }
    private void CoverAll()
    {
        lockedSnapshot = true;
        if (!lockObserved) { Interlocked.Increment(ref generation); lockObserved = true; }
        foreach (var window in Application.Current.Windows.Cast<Window>().ToArray()) Cover(window);
        // حوارات الملفات والطباعة ليست WPF. تُخفى قبل طلب الإلغاء ولا يعاد إظهارها.
        bool nativeFound = false;
        EnumThreadWindows(GetCurrentThreadId(), (handle, _) => {
            var name = new StringBuilder(256); GetClassName(handle, name, name.Capacity);
            if (name.ToString() != "#32770") return true;
            ShowWindow(handle, 0); EnableWindow(handle, false); nativeFound = true;
            PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            PostMessage(handle, 0x0111, new IntPtr(2), IntPtr.Zero); // IDCANCEL
            return true;
        }, IntPtr.Zero);
        if ((nativeFound || nativeDepth > 0) && nativeUnlockWindow is null)
        {
            nativeUnlockWindow = new Window { Title = "فتح الجلسة", Width = 600, Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true };
            var unlock = nativeUnlockWindow;
            unlock.Closed += (_, _) => { if (ReferenceEquals(nativeUnlockWindow, unlock)) nativeUnlockWindow = null; };
            Cover(unlock); unlock.Show(); unlock.Activate();
        }
    }
    private delegate bool EnumWindowCallback(IntPtr handle, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumThreadWindows(uint threadId, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr handle, bool enabled);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    private void Cover(Window window)
    {
        if (covered.ContainsKey(window)) return;
        covered.Add(window, (window.Content, window.Title));
        ClearPasswords(window.Content as DependencyObject);
        window.Content = null;
        window.Title = "الرواتب المحلية • الجلسة مقفلة";
        var panel = new StackPanel { MaxWidth = 480, Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(Ui.Text("الجلسة مقفلة", 28));
        panel.Children.Add(Ui.Text(startupError ?? "أدخل كلمة سر البرنامج للمتابعة. البيانات والنوافذ باقية دون تغيير."));
        panel.Children.Add(Ui.Text("هذا قفل للجلسة، وليس تشفيراً للملفات.", 13));
        if (startupError is null)
        {
            var password = new PasswordBox { MinWidth = 250, MaxLength = 1024, Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(password, "كلمة سر الجلسة");
            var error = Ui.Text("");
            void Submit()
            {
                bool unlocked = state!.TryUnlock(password.Password); password.Clear();
                if (unlocked) Uncover();
                else { error.Text = "كلمة السر غير صحيحة أو المحاولات متوقفة مؤقتاً. بعد خمس محاولات انتظر 30 ثانية."; password.Focus(); }
            }
            panel.Children.Add(password); panel.Children.Add(Ui.Button("فتح الجلسة", Submit)); panel.Children.Add(error);
            password.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Submit(); } };
            password.Loaded += (_, _) => password.Focus();
        }
        var shield = new Border { Child = panel, FlowDirection = FlowDirection.RightToLeft };
        shield.SetResourceReference(Border.BackgroundProperty, "Page");
        // لا تصل اختصارات الحفظ والطباعة في النافذة إلى محتواها المخفي.
        shield.KeyDown += (_, e) => e.Handled = true;
        shield.KeyUp += (_, e) => e.Handled = true;
        window.Content = shield;
        window.Closed += ForgetClosed;
    }
    private static void ClearPasswords(DependencyObject? element)
    {
        if (element is null) return;
        if (element is PasswordBox password) password.Clear();
        if (element is not Visual) return;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) ClearPasswords(VisualTreeHelper.GetChild(element, i));
    }
    private void ForgetClosed(object? sender, EventArgs e) { if (sender is Window window) { covered.Remove(window); window.Closed -= ForgetClosed; } }
    private void Uncover()
    {
        lockedSnapshot = false; lockObserved = false;
        foreach (var (window, prior) in covered.ToArray()) { window.Content = prior.Content; window.Title = prior.Title; window.Closed -= ForgetClosed; }
        covered.Clear();
        nativeUnlockWindow?.Close(); nativeUnlockWindow = null;
        Unlocked?.Invoke();
    }
    internal void ShowSettings()
    {
        if (Locked || state is null) return;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Ui.Text(state.Enabled ? "تغيير القفل أو إيقافه" : "تفعيل قفل الجلسة", 24));
        panel.Children.Add(Ui.Text(LocalSessionLock.Warning, 13));
        var current = new PasswordBox { MaxLength = 1024, Padding = new Thickness(8) };
        var next = new PasswordBox { MaxLength = 1024, Padding = new Thickness(8) };
        var repeat = new PasswordBox { MaxLength = 1024, Padding = new Thickness(8) };
        if (state.Enabled) { panel.Children.Add(Ui.Text("كلمة السر الحالية")); panel.Children.Add(current); }
        panel.Children.Add(Ui.Text("كلمة السر الجديدة (8 أحرف على الأقل)")); panel.Children.Add(next);
        panel.Children.Add(Ui.Text("تأكيد كلمة السر الجديدة")); panel.Children.Add(repeat);
        panel.Children.Add(Ui.Text("القفل التلقائي بعد دقائق من عدم استخدام التطبيق"));
        var minutes = new ComboBox { ItemsSource = new[] { 1, 2, 5, 10, 15, 30, 60, 120 }, SelectedItem = state.IdleMinutes, Margin = new Thickness(0, 4, 0, 8) }; panel.Children.Add(minutes);
        var result = Ui.Text("");
        var dialog = Ui.Dialog(owner, "إعداد قفل الجلسة", new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 620, 760);
        panel.Children.Add(Ui.Button(state.Enabled ? "حفظ كلمة السر والمدة" : "تفعيل القفل", () =>
        {
            try
            {
                if (Locked) return;
                if (next.Password != repeat.Password) throw new InvalidDataException("تأكيد كلمة السر لا يطابق الكلمة الجديدة.");
                if (state.Enabled) { if (!state.ChangePassword(current.Password, next.Password, (int)minutes.SelectedItem)) throw new InvalidDataException("كلمة السر الحالية غير صحيحة أو المحاولات متوقفة مؤقتاً؛ انتظر 30 ثانية بعد خمس محاولات."); }
                else state.Enable(next.Password, (int)minutes.SelectedItem);
                dialog.Close();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException) { result.Text = ex.Message; }
            finally { current.Clear(); next.Clear(); repeat.Clear(); }
        }));
        if (state.Enabled) panel.Children.Add(Ui.Button("إيقاف القفل بكلمة السر الحالية", () =>
        {
            try
            {
                if (Locked) return;
                if (!state.Disable(current.Password)) { result.Text = "كلمة السر الحالية غير صحيحة أو المحاولات متوقفة مؤقتاً."; return; }
                dialog.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { result.Text = ex.Message; }
            finally { current.Clear(); next.Clear(); repeat.Clear(); }
        }));
        panel.Children.Add(Ui.Button("إلغاء", () => dialog.Close())); panel.Children.Add(result);
        dialog.Closed += (_, _) => { current.Clear(); next.Clear(); repeat.Clear(); };
        dialog.ShowDialog();
    }
    public void Dispose() { lockedSnapshot = true; Interlocked.Increment(ref generation); if (ReferenceEquals(active, this)) active = null; disposed = true; nativeUnlockWindow?.Close(); nativeUnlockWindow = null; timer.Stop(); timer.Tick -= Tick; InputManager.Current.PreProcessInput -= Input; covered.Clear(); }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SalaryDesktop;

/// أيقونة البرنامج في منطقة الإشعارات بجانب الساعة (Shell_NotifyIcon مباشرة، دون مكتبة Windows Forms).
/// النقر يفتح النافذة، والزر الأيمن يعرض القائمة؛ وتُعاد الأيقونة إذا أُعيد تشغيل مستكشف ويندوز.
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 41;   // WM_APP + 41
    private const int WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const int NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10, NiifInfo = 1, NiifWarning = 2;

    private readonly HwndSource window;
    private readonly int taskbarCreated;
    private readonly IntPtr icon;
    private readonly string tip;
    private bool added, disposed;

    public event Action? Open;
    public event Action? Menu;

    public TrayIcon(string tip)
    {
        this.tip = tip.Length > 120 ? tip[..120] : tip;
        // نافذة رسائل مخفية تستقبل نقرات الأيقونة.
        window = new HwndSource(new HwndSourceParameters("SalaryDesktopTray") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        window.AddHook(Hook);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var path = Environment.ProcessPath ?? throw new Win32Exception("مسار البرنامج غير معروف.");
        var small = new IntPtr[1];
        if (ExtractIconEx(path, 0, null, small, 1) == 0 || small[0] == IntPtr.Zero) throw new Win32Exception("تعذّر تحميل أيقونة البرنامج.");
        icon = small[0];
        Add();
    }

    public IntPtr Handle => window.Handle;

    private NotifyIconData Data(int flags)
    {
        return new NotifyIconData { Size = Marshal.SizeOf<NotifyIconData>(), Window = window.Handle, Id = 1, Flags = flags, CallbackMessage = CallbackMessage, Icon = icon, Tip = tip, Info = "", InfoTitle = "" };
    }

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip);
        added = ShellNotifyIcon(NimAdd, ref data);
    }

    /// إشعار قصير من الأيقونة (مثل «البرنامج يعمل في الخلفية»).
    public void Balloon(string title, string text, bool warning = false)
    {
        if (disposed || !added) return;
        var data = Data(NifInfo);
        data.InfoTitle = title.Length > 60 ? title[..60] : title;
        data.Info = text.Length > 250 ? text[..250] : text;
        data.InfoFlags = warning ? NiifWarning : NiifInfo;
        ShellNotifyIcon(NimModify, ref data);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == taskbarCreated && taskbarCreated != 0) { Add(); return IntPtr.Zero; }
        if (message != CallbackMessage) return IntPtr.Zero;
        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case WmLButtonUp or WmLButtonDblClk: Open?.Invoke(); handled = true; break;
            case WmRButtonUp or WmContextMenu: SetForegroundWindow(window.Handle); Menu?.Invoke(); handled = true; break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (added) { var data = Data(0); ShellNotifyIcon(NimDelete, ref data); added = false; }
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        window.RemoveHook(Hook);
        window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] private static extern bool ShellNotifyIcon(int message, ref NotifyIconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
}

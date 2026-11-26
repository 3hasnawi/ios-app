param([string]$Executable,[string]$Image)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PayrollFrame {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Monitor { public int Size; public Rect Bounds, Work; public int Flags; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flag);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr h, ref Monitor m);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] public static extern int GetWindowStyle(IntPtr h,int index);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr other, int x, int y, int w, int height, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,int message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
$oldDpi = [PayrollFrame]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$p = Start-Process $Executable -PassThru
try {
    for ($i=0; $i -lt 100; $i++) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 100
    }
    $h=$p.MainWindowHandle
    if ($h -eq 0) { throw 'Missing native application window' }
    Start-Sleep -Milliseconds 600
    $r=[PayrollFrame+Rect]::new()
    $m=[PayrollFrame+Monitor]::new()
    $m.Size=[Runtime.InteropServices.Marshal]::SizeOf([type][PayrollFrame+Monitor])
    if (![PayrollFrame]::GetMonitorInfo([PayrollFrame]::MonitorFromWindow($h,2),[ref]$m) -or ![PayrollFrame]::GetWindowRect($h,[ref]$r)) { throw 'Missing frame geometry' }
    if ($r.Left -lt $m.Work.Left -or $r.Right -gt $m.Work.Right -or $r.Top -lt $m.Work.Top -or $r.Bottom -gt $m.Work.Bottom) { throw 'The initial window extends beyond the work area' }
    # Emulate a restored frame larger than its monitor, then notify the application.
    $aw=$m.Work.Right-$m.Work.Left; $ah=$m.Work.Bottom-$m.Work.Top
    [void][PayrollFrame]::SetWindowPos($h,[IntPtr]::Zero,($m.Work.Left-100),($m.Work.Top-60),($aw+200),($ah+120),0x14)
    [void][PayrollFrame]::SendMessage($h,0x001A,[IntPtr]::new(47),[IntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    [void][PayrollFrame]::GetWindowRect($h,[ref]$r)
    if ($r.Left -lt $m.Work.Left -or $r.Right -gt $m.Work.Right -or $r.Top -lt $m.Work.Top -or $r.Bottom -gt $m.Work.Bottom) { throw 'Oversize frame did not fit the monitor' }
    $style=[PayrollFrame]::GetWindowStyle($h,-16)
    $controls=0x00C70000  # WS_CAPTION, WS_THICKFRAME, WS_MINIMIZEBOX and WS_MAXIMIZEBOX
    if (($style -band $controls) -ne $controls) { throw 'Missing native resize or caption controls' }
    foreach ($state in 6,3,9) {
        [void][PayrollFrame]::ShowWindowAsync($h,$state)
        Start-Sleep -Milliseconds 400
        if (([PayrollFrame]::IsIconic($h) -ne ($state -eq 6)) -or ([PayrollFrame]::IsZoomed($h) -ne ($state -eq 3))) { throw "Failed native window state $state" }
    }
    $w=[Math]::Min(940,$m.Work.Right-$m.Work.Left); $height=[Math]::Min(630,$m.Work.Bottom-$m.Work.Top)
    if (![PayrollFrame]::SetWindowPos($h,[IntPtr]::Zero,$m.Work.Left,$m.Work.Top,$w,$height,0x14)) { throw 'Resize failed' }
    Start-Sleep -Milliseconds 300
    [void][PayrollFrame]::GetWindowRect($h,[ref]$r)
    if ($r.Right-$r.Left -ne $w -or $r.Bottom-$r.Top -ne $height) { throw 'The window does not accept a custom size' }
    $bmp=[Drawing.Bitmap]::new($w,$height)
    $graphics=[Drawing.Graphics]::FromImage($bmp)
    $dc=$graphics.GetHdc()
    try {if (![PayrollFrame]::PrintWindow($h,$dc,2)) {throw 'Native frame capture failed'}} finally {$graphics.ReleaseHdc($dc);$graphics.Dispose()}
    try {$bmp.Save($Image,[Drawing.Imaging.ImageFormat]::Png)} finally {$bmp.Dispose()}
} finally {
    if (!$p.HasExited) { [void]$p.CloseMainWindow(); if (!$p.WaitForExit(5000)) { $p.Kill() } }
    [void][PayrollFrame]::SetThreadDpiAwarenessContext($oldDpi)
}

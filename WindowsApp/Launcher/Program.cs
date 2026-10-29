using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SalaryLauncher;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    [STAThread]
    private static int Main()
    {
        string? directory = null;
        try
        {
            directory = Path.Combine(Path.GetTempPath(), "SalaryApp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("SalaryApp.Payload.zip")
                   ?? throw new InvalidDataException("ملفات تشغيل البرنامج غير موجودة."))
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
                archive.ExtractToDirectory(directory);

            var executable = Path.Combine(directory, "SalaryWindows.exe");
            if (!File.Exists(executable) || !File.Exists(Path.Combine(directory, "SalaryWindows.pri")))
                throw new InvalidDataException("ملفات تشغيل البرنامج غير مكتملة.");
            var diagnostic = Environment.GetEnvironmentVariable("SALARY_LAUNCHER_DIAGNOSTIC");
            if (!string.IsNullOrWhiteSpace(diagnostic)) File.WriteAllText(diagnostic, directory);
            using var process = Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false })
                ?? throw new InvalidOperationException("تعذّر تشغيل التطبيق.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                MessageBoxW(IntPtr.Zero, "توقف التطبيق بشكل غير متوقع. جرّب إعادة تشغيله، وتواصل معنا إذا استمرت المشكلة.", "الرواتب", 0x10);
            return process.ExitCode;
        }
        catch (Exception)
        {
            MessageBoxW(IntPtr.Zero, "تعذّر تجهيز تطبيق الرواتب. تأكد من توفر مساحة في القرص ثم حاول مجدداً.", "الرواتب", 0x10);
            return 1;
        }
        finally
        {
            if (directory != null) try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}

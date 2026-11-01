using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SalaryDesktop;

/// الإصدار والتحديث: كل تشغيل ناجح لفحص ويندوز ينشر البرنامج في صفحة الإصدارات بالوسم windows-latest،
/// والبرنامج يقارن رقم إصداره برقم المنشور هناك (بلا إرسال أي بيانات).
public static class Updates
{
    public const string Page = "https://github.com/3hasnawi/ios-app/releases/tag/windows-latest";
    public const string Download = "https://github.com/3hasnawi/ios-app/releases/download/windows-latest/SalaryDesktop-win-x64.zip";
    private const string Api = "https://api.github.com/repos/3hasnawi/ios-app/releases/tags/windows-latest";

    public static Version Current
    {
        get { var version = typeof(Updates).Assembly.GetName().Version ?? new Version(1, 0, 0); return new Version(version.Major, version.Minor, Math.Max(0, version.Build)); }
    }

    /// رقم أحدث إصدار منشور، أو null عند تعذّر الاتصال (شبكة الدائرة قد تمنع GitHub).
    public static async Task<Version?> Latest()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SalaryDesktop/" + Current);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var document = JsonDocument.Parse(await client.GetStringAsync(Api));
            var name = document.RootElement.TryGetProperty("name", out var value) ? value.GetString() ?? "" : "";
            var match = Regex.Match(name, @"(\d+)\.(\d+)\.(\d+)");
            return match.Success ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException or OverflowException or InvalidOperationException) { return null; }
    }

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

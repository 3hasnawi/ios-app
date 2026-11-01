using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryDesktop.Core;

public sealed record BackupServerStatus(int Count, long Bytes, long DiskFree, DateTimeOffset? Latest);

/// عميل سيرفر النسخ المحلي (إضافة Home Assistant «salary_backup_server») بنفس قواعد الآيفون في BackupServer.swift:
/// عنوان IP خاص وHTTP محلي فقط، رمز وصول لا يقل عن ٣٢ حرفاً، رفع ملف النسخة مع بصمة SHA-256 وتأكيد السيرفر لها،
/// واسترجاع آخر نسخة. السيرفر نفسه يرفض استبدال نسخة بأخرى ينقصها شهر أو قسم كامل.
public static class BackupServer
{
    public const string Place = "السيرفر المحلي";
    public const string Unreachable = "السيرفر غير متاح حالياً؛ ستُعاد محاولة النسخ تلقائياً عند توفر الاتصال.";

    /// http://IP-خاص:منفذ فقط (10.x أو 172.16–31.x أو 192.168.x)، بلا مستخدم أو مسار أو استعلام.
    public static Uri? Url(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/") return null;
        if (uri.HostNameType != UriHostNameType.IPv4 || !IPAddress.TryParse(uri.Host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ? new Uri(uri.GetLeftPart(UriPartial.Authority) + "/") : null;
    }

    public static bool ValidToken(string? token) => token is { Length: >= 32 } && token.All(c => c is > ' ' and < (char)127);

    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri baseUri, string path, string? token)
    {
        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return request;
    }

    private static string Failure(HttpStatusCode status, string fallback) => status switch
    {
        HttpStatusCode.Unauthorized => "رمز الوصول غير صحيح؛ انسخه من إعدادات الإضافة في Home Assistant.",
        HttpStatusCode.Conflict => "السيرفر مضبوط على مزامنة التعديلات بين الأجهزة، وهذا الإصدار لا يدعمها بعد؛ أوقف الخيار في إعدادات الإضافة.",
        HttpStatusCode.BadRequest => "رفض السيرفر النسخة: إما أن الملف غير مكتمل، أو أن نسخة السيرفر الحالية فيها أشهر أو بيانات غير موجودة هنا (مثلاً نسخة الآيفون أحدث). استرجع نسخة السيرفر أولاً ثم أعد النسخ.",
        _ => fallback,
    };

    /// لا تُجهّز النسخة إذا لم يكن السيرفر قابلاً للوصول؛ أي رد HTTP يكفي.
    public static async Task CheckReachability(HttpClient http, Uri baseUri, CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { using var request = Request(HttpMethod.Head, baseUri, "", null); using var _ = await http.SendAsync(request, timeout.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested) { throw new InvalidDataException(Unreachable, ex); }
    }

    /// يرفع الملف ويعيد الحجم الذي أكّده السيرفر بعد مطابقة البصمة.
    public static async Task<long> Upload(HttpClient http, Uri baseUri, string token, string file, CancellationToken cancel = default)
    {
        var digest = await Task.Run(() => Sha256(file), cancel).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        await using var stream = File.OpenRead(file);
        using var request = Request(HttpMethod.Post, baseUri, "v1/backups", token);
        request.Headers.Add("X-Content-SHA256", digest);
        request.Content = new StreamContent(stream, 1024 * 1024);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        request.Content.Headers.ContentLength = stream.Length;
        const string unconfirmed = "لم يؤكد السيرفر حفظ النسخة؛ تحقق من العنوان ورمز الوصول.";
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created) throw new InvalidDataException(Failure(response.StatusCode, unconfirmed));
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sha256", out var sha) || sha.GetString() != digest
                || !root.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes) || bytes <= 0)
                throw new InvalidDataException(unconfirmed);
            return bytes;
        }
        catch (JsonException ex) { throw new InvalidDataException(unconfirmed, ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested) { throw new InvalidDataException("انقطع الاتصال بالسيرفر أثناء رفع النسخة؛ أعد المحاولة.", ex); }
    }

    /// ينزّل آخر نسخة من السيرفر إلى ملف (للاسترجاع).
    public static async Task DownloadLatest(HttpClient http, Uri baseUri, string token, string destination, CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var request = Request(HttpMethod.Get, baseUri, "v1/backups/latest", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("لا توجد نسخة على السيرفر بعد.");
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException(Failure(response.StatusCode, "تعذّر تنزيل النسخة من السيرفر."));
            await using var output = File.Create(destination);
            await response.Content.CopyToAsync(output, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested) { throw new InvalidDataException("تعذّر الاتصال بالسيرفر لتنزيل النسخة.", ex); }
    }

    /// اختبار الاتصال: عدد النسخ على السيرفر وتاريخ آخرها والمساحة المتبقية.
    public static async Task<BackupServerStatus> Status(HttpClient http, Uri baseUri, string token, CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = Request(HttpMethod.Get, baseUri, "v1/status", token);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException(Failure(response.StatusCode, "رد السيرفر غير متوقع؛ تأكد أن العنوان لإضافة النسخ الاحتياطي."));
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
            var root = json.RootElement;
            if (!root.TryGetProperty("service", out var service) || service.GetString() != "salary-backup") throw new InvalidDataException("العنوان لا يخص سيرفر النسخ الاحتياطي.");
            long Long(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
            DateTimeOffset? latest = null;
            if (root.TryGetProperty("backups", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
                && list[0].TryGetProperty("created_at", out var at) && DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)) latest = parsed;
            return new BackupServerStatus((int)Long("backup_count"), Long("backup_bytes"), Long("disk_free"), latest);
        }
        catch (JsonException ex) { throw new InvalidDataException("رد السيرفر غير مفهوم.", ex); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested) { throw new InvalidDataException(Unreachable, ex); }
    }
}

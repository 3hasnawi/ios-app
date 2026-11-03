using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryDesktop.Core;

/// أداة تأسيسية فقط؛ لا تغيّر صيغة النسخ أو تُفعّل الترحيل.
/// تُفصل بادئة أعمدة JSON كما هي بالبايتات، لتبقى القيم والحقول الإضافية الأصلية دون إعادة ترميز.
public static class SharedPayrollColumns
{
    public sealed record Parts(string SchemaId, byte[] Prefix, byte[] Tail);

    public static Parts? Split(byte[]? raw)
    {
        if (raw is null || raw.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("headers", out var headers) || !root.TryGetProperty("values", out var values) ||
                headers.ValueKind != JsonValueKind.Array || values.ValueKind != JsonValueKind.Array || headers.GetArrayLength() != values.GetArrayLength() ||
                headers.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String) || values.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) return null;
            var reader = new Utf8JsonReader(raw);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject || !reader.Read() || reader.TokenType != JsonTokenType.PropertyName ||
                reader.ValueIsEscaped || !reader.ValueSpan.SequenceEqual("headers"u8) || !reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) if (reader.TokenType != JsonTokenType.String) return null;
            if (reader.TokenType != JsonTokenType.EndArray) return null;
            var end = checked((int)reader.BytesConsumed);
            var prefix = raw.AsSpan(0, end).ToArray();
            return new Parts(Convert.ToHexString(SHA256.HashData(prefix)), prefix, raw.AsSpan(end).ToArray());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException) { return null; }
    }

    public static byte[] Join(string schemaId, byte[] prefix, byte[] tail)
    {
        if (schemaId.Length != 64 || !string.Equals(Convert.ToHexString(SHA256.HashData(prefix)), schemaId, StringComparison.Ordinal))
            throw new InvalidDataException("بصمة أعمدة الرواتب لا تطابق المخطط المحفوظ.");
        var result = new byte[checked(prefix.Length + tail.Length)];
        prefix.CopyTo(result, 0); tail.CopyTo(result, prefix.Length);
        return result;
    }
}

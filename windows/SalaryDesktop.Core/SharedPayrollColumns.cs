using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryDesktop.Core;

/// أداة تأسيسية فقط؛ لا تغيّر صيغة النسخ أو تُفعّل الترحيل.
/// تُفصل مصفوفة الأعمدة كما هي بالبايتات، مهما كان ترتيب الخصائص، ويبقى باقي السجل كما هو.
public static class SharedPayrollColumns
{
    public sealed record Parts(string SchemaId, byte[] Prefix, byte[] Before, byte[] Tail);

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
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || reader.ValueIsEscaped || !reader.ValueSpan.SequenceEqual("headers"u8)) continue;
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;
                var start = checked((int)reader.TokenStartIndex);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) if (reader.TokenType != JsonTokenType.String) return null;
                if (reader.TokenType != JsonTokenType.EndArray) return null;
                var end = checked((int)reader.BytesConsumed);
                var prefix = raw.AsSpan(start, end - start).ToArray();
                return new Parts(Convert.ToHexString(SHA256.HashData(prefix)), prefix, raw.AsSpan(0, start).ToArray(), raw.AsSpan(end).ToArray());
            }
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException) { return null; }
    }

    public static byte[] Join(string schemaId, byte[] prefix, byte[] before, byte[] tail)
    {
        if (schemaId.Length != 64 || !string.Equals(Convert.ToHexString(SHA256.HashData(prefix)), schemaId, StringComparison.Ordinal))
            throw new InvalidDataException("بصمة أعمدة الرواتب لا تطابق المخطط المحفوظ.");
        var result = new byte[checked(before.Length + prefix.Length + tail.Length)];
        before.CopyTo(result, 0); prefix.CopyTo(result, before.Length); tail.CopyTo(result, before.Length + prefix.Length);
        return result;
    }
}

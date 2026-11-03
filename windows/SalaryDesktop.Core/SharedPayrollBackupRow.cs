using System.Security.Cryptography;
using System.Text.Json;

namespace SalaryDesktop.Core;

/// تمثيل تجريبي غير مفعّل: تُحفظ بايتات سطر الموظف كاملة، وتُشارك مقاطع الأعمدة المتكررة.
/// لا يغيّر تنسيق النسخة الحالية؛ يجب وضع حدود وإصدار واضح قبل ربطه بالتطبيق.
public static class SharedPayrollBackupRow
{
    public sealed record Packed(string? SchemaId, byte[]? Before, byte[]? After, byte[]? Original, string RowHash);
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static Packed Pack(byte[] line, IDictionary<string, byte[]> schemas)
    {
        var rowHash = Hash(line);
        Packed Fallback() => new(null, null, null, line.ToArray(), rowHash);
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Fallback();
            var reader = new Utf8JsonReader(line);
            int tokenStart = -1, tokenEnd = -1, count = 0;
            string? base64 = null; byte[]? rawColumns = null;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || !reader.ValueTextEquals("rawColumns"u8)) continue;
                count++;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) return Fallback();
                tokenStart = checked((int)reader.TokenStartIndex); tokenEnd = checked((int)reader.BytesConsumed);
                base64 = reader.GetString();
                try { rawColumns = Convert.FromBase64String(base64 ?? ""); }
                catch (FormatException) { return Fallback(); }
            }
            if (count != 1 || base64 is null || rawColumns is null) return Fallback();
            if (!string.Equals(Convert.ToBase64String(rawColumns), base64, StringComparison.Ordinal)) return Fallback();
            var columns = SharedPayrollColumns.Split(rawColumns);
            if (columns is null) return Fallback();
            var start = checked(((columns.Before.Length + 2) / 3) * 3);
            var end = checked(((columns.Before.Length + columns.Prefix.Length) / 3) * 3);
            if (end - start < 12) return Fallback();
            var firstChar = checked(start / 3 * 4); var lastChar = checked(end / 3 * 4);
            if (lastChar > base64.Length) return Fallback();
            var offsets = Base64StringOffsets(line.AsSpan(tokenStart, tokenEnd - tokenStart), base64);
            if (offsets is null) return Fallback();
            var begin = checked(tokenStart + offsets[firstChar]); var finish = checked(tokenStart + offsets[lastChar]);
            var schema = line.AsSpan(begin, finish - begin).ToArray();
            var id = Hash(schema);
            if (schemas.TryGetValue(id, out var saved))
            {
                if (!saved.AsSpan().SequenceEqual(schema)) throw new InvalidDataException("تعارضت بصمة مخطط أعمدة الرواتب.");
            }
            else schemas.Add(id, schema);
            return new(id, line.AsSpan(0, begin).ToArray(), line.AsSpan(finish).ToArray(), null, rowHash);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException or ArgumentException)
        { return Fallback(); }
    }

    // يربط كل محرف Base64 بموضعه الفعلي في سلسلة JSON، بما في ذلك \\/ وتهريب Unicode.
    private static int[]? Base64StringOffsets(ReadOnlySpan<byte> token, string decoded)
    {
        if (token.Length < 2 || token[0] != '"' || token[^1] != '"') return null;
        var positions = new int[decoded.Length + 1];
        var pos = 1;
        for (var i = 0; i < decoded.Length; i++)
        {
            positions[i] = pos;
            if (pos >= token.Length - 1) return null;
            int character = token[pos++];
            if (character == '\\')
            {
                if (pos >= token.Length - 1) return null;
                character = token[pos++];
                if (character == 'u')
                {
                    if (pos + 4 > token.Length - 1) return null;
                    var value = 0;
                    for (var n = 0; n < 4; n++)
                    {
                        var h = token[pos++];
                        var digit = h is >= (byte)'0' and <= (byte)'9' ? h - '0' : h is >= (byte)'A' and <= (byte)'F' ? h - 'A' + 10 : h is >= (byte)'a' and <= (byte)'f' ? h - 'a' + 10 : -1;
                        if (digit < 0) return null;
                        value = value * 16 + digit;
                    }
                    character = value;
                }
                else if (character != '/') return null;
            }
            if (character != decoded[i]) return null;
        }
        positions[decoded.Length] = pos;
        return pos == token.Length - 1 ? positions : null;
    }

    public static byte[] Unpack(Packed row, IReadOnlyDictionary<string, byte[]> schemas)
    {
        byte[] result;
        if (row.SchemaId is null)
        {
            if (row.Original is null || row.Before is not null || row.After is not null) throw new InvalidDataException("سجل الرواتب الأصلي غير صالح.");
            result = row.Original.ToArray();
        }
        else
        {
            if (row.Original is not null || row.Before is null || row.After is null || !schemas.TryGetValue(row.SchemaId, out var schema) || Hash(schema) != row.SchemaId)
                throw new InvalidDataException("مخطط أعمدة الرواتب مفقود أو تالف.");
            result = new byte[checked(row.Before.Length + schema.Length + row.After.Length)];
            row.Before.CopyTo(result, 0); schema.CopyTo(result, row.Before.Length); row.After.CopyTo(result, row.Before.Length + schema.Length);
        }
        if (Hash(result) != row.RowHash) throw new InvalidDataException("سجل الرواتب لا يطابق بصمته.");
        return result;
    }
}

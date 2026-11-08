using System.Text;

namespace Payroll.Core;

/// <summary>Normalizes Arabic for search without changing the stored name or merging records.</summary>
public static class ArabicSearch
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var result = new StringBuilder(text.Length);
        var previousSpace = true;
        foreach (var character in text)
        {
            if (character is >= '\u064B' and <= '\u065F' or '\u0670' or '\u0640') continue;
            if (char.IsWhiteSpace(character))
            {
                if (!previousSpace) result.Append(' ');
                previousSpace = true;
                continue;
            }
            previousSpace = false;
            result.Append(character switch
            {
                'آ' or 'أ' or 'إ' or 'ٱ' => 'ا', 'ة' => 'ه', 'ى' or 'ئ' => 'ي', 'ؤ' => 'و',
                >= '\u0660' and <= '\u0669' => (char)('0' + character - '\u0660'),
                >= '\u06F0' and <= '\u06F9' => (char)('0' + character - '\u06F0'),
                _ => char.ToLowerInvariant(character)
            });
        }
        return result.ToString().TrimEnd();
    }
}

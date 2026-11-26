namespace SalaryDesktop.Core;

public static class ArabicAmountWords
{
    private static readonly string[] Units = ["", "واحد", "اثنان", "ثلاثة", "أربعة", "خمسة", "ستة", "سبعة", "ثمانية", "تسعة"];
    private static readonly string[] Tens = ["", "", "عشرون", "ثلاثون", "أربعون", "خمسون", "ستون", "سبعون", "ثمانون", "تسعون"];
    private static readonly string[] Hundreds = ["", "مائة", "مائتان", "ثلاثمائة", "أربعمائة", "خمسمائة", "ستمائة", "سبعمائة", "ثمانمائة", "تسعمائة"];
    private static readonly (string Single, string Dual, string Plural)[] Scales = [("", "", ""), ("ألف", "ألفان", "آلاف"), ("مليون", "مليونان", "ملايين"), ("مليار", "ملياران", "مليارات"), ("تريليون", "تريليونان", "تريليونات")];
    private static string BelowThousand(int n)
    {
        var parts = new List<string>(); if (n / 100 > 0) parts.Add(Hundreds[n / 100]); var r = n % 100;
        if (r > 0) parts.Add(r switch { < 10 => Units[r], 10 => "عشرة", 11 => "أحد عشر", 12 => "اثنا عشر", < 20 => Units[r - 10] + " عشر", _ when r % 10 == 0 => Tens[r / 10], _ => Units[r % 10] + " و" + Tens[r / 10] });
        return string.Join(" و", parts);
    }
    public static string Dinars(long amount)
    {
        if (amount < 0 || amount >= 1_000_000_000_000_000) throw new InvalidDataException("المبلغ خارج النطاق المدعوم لكتابته بالحروف.");
        if (amount == 0) return "صفر دينار";
        var chunks = new List<string>(); var value = amount; var scale = 0; var embedded = false; var last = (int)(amount % 100);
        while (value > 0)
        {
            int n = (int)(value % 1000);
            if (n > 0)
            {
                if (scale == 0 && last is 1 or 2) { var unit = last == 1 ? "دينار واحد" : "ديناران"; chunks.Add(n / 100 > 0 ? Hundreds[n / 100] + " و" + unit : unit); embedded = true; }
                else if (scale == 0) chunks.Add(BelowThousand(n));
                else { var unit = Scales[scale]; chunks.Add(n switch { 1 => unit.Single, 2 => unit.Dual, <= 10 => BelowThousand(n) + " " + unit.Plural, _ => BelowThousand(n) + " " + unit.Single }); }
            }
            value /= 1000; scale++;
        }
        chunks.Reverse(); var result = string.Join(" و", chunks); return embedded ? result : result + (last is >= 3 and <= 10 ? " دنانير" : " دينار");
    }
}

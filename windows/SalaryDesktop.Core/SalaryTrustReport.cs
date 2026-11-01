using System.Globalization;
using System.Text;

namespace SalaryDesktop.Core;

/// كشف أمانات الرواتب PDF وExcel (نقل لـ SalaryTrustReport.swift): نفس الأعمدة وسطر «المتبقي المؤكد»،
/// والكشف مبني على لقطة القيود المعروضة بعد المرشحات؛ يُرفض إن تغيّر الدفتر أو لم تتسع خلية في PDF.
public static class SalaryTrustReports
{
    private static string Amount(long n) => n.ToString("#,0", CultureInfo.GetCultureInfo("en-US"));
    private static string Clean(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public static string FileName => "كشف أمانات الرواتب";

    public static ReportSheet Sheet(SalaryTrustLedger ledger, IReadOnlyList<SalaryTrustEntry> entries, string header, string month, string status, string query)
    {
        SalaryTrust.Validate(ledger);
        if (entries.Count == 0 || entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            throw new InvalidDataException("لا توجد قيود صالحة للتصدير.");
        var byId = ledger.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        if (!entries.All(e => byId.TryGetValue(e.Id, out var stored) && Equals(stored, e))) throw new InvalidDataException("تغيّرت بيانات الأمانات؛ أعد فتح الكشف.");
        var filters = string.Join(" · ", new[] { month, status, query.Length == 0 ? "" : "بحث: " + query }.Where(f => f.Length > 0));
        ReportColumn Text(string title, double width) => new(title, width, ReportColumnKind.Text);
        ReportColumn[] columns = [Text("ت", 5), Text("الاسم", 30), Text("الشهر", 15), Text("الصرف", 17), Text("الحالة", 26), Text("الأمانة", 18), Text("المصروف", 18), Text("المتبقي", 18)];
        var rows = entries.Select((e, i) => new[]
        {
            (i + 1).ToString(CultureInfo.InvariantCulture), Clean(e.Name), SalaryTrust.MonthLabel(e.Year, e.Month), SalaryTrust.PaymentStatus(e), Clean(e.CaseStatus),
            Amount(e.Amount), e.ReviewPending ? "—" : Amount(SalaryTrust.Paid(e)), e.ReviewPending ? "—" : Amount(SalaryTrust.Remaining(e)),
        }).ToList();
        var review = entries.Count(e => e.ReviewPending);
        rows.Add(["", "المتبقي المؤكد", "", review > 0 ? $"للمراجعة: {review.ToString("N0", CultureInfo.GetCultureInfo("en-US"))}" : "", "", "", "", Amount(SalaryTrust.Outstanding(entries))]);
        return new ReportSheet(header, "أمانات الرواتب · " + filters, columns, rows, [], ShowTotal: false);
    }

    /// PDF متعدد الأسطر والصفحات دون اقتطاع (SalaryTrustPdf)، مثل الآيفون بعد إصلاح 575e5ce.
    public static byte[] Pdf(ReportSheet sheet, ReportAssets assets, DateTime stamp) => SalaryTrustPdf.Generate(sheet, assets, stamp);

    /// القيود بعد المرشحات المتقاطعة (الشهر وحالة الصرف والبحث بالاسم) بترتيب الآيفون: الأحدث شهراً ثم الاسم تنازلياً.
    public static IReadOnlyList<SalaryTrustEntry> Filtered(IEnumerable<SalaryTrustEntry> entries, int month, SalaryTrustFilter status, string search)
    {
        var query = PersonProfiles.Key(search.Trim());
        return entries.Where(e => (month == 0 || e.Year * 100 + e.Month == month) && SalaryTrust.Includes(status, e) &&
                (query.Length == 0 || PersonProfiles.Key(e.Name).Contains(query, StringComparison.Ordinal)))
            .OrderByDescending(e => e.Year).ThenByDescending(e => e.Month).ThenByDescending(e => e.Name, StringComparer.Ordinal).ToArray();
    }

    /// Excel: المبالغ أرقام حقيقية والنصوص كما هي، بنفس رأس الكشف وتجميد الصفوف الثلاثة الأولى.
    public static byte[] Excel(ReportSheet sheet, IReadOnlyList<SalaryTrustEntry> entries)
    {
        sheet.Validate();
        var last = XlsxReport.Letter(sheet.Columns.Count - 1);
        var body = new StringBuilder();
        body.Append("<row r=\"1\">").Append(XlsxReport.Text("A1", sheet.Header, 1)).Append("</row>");
        body.Append("<row r=\"2\">").Append(XlsxReport.Text("A2", sheet.Title, 2)).Append("</row><row r=\"3\">");
        for (int c = 0; c < sheet.Columns.Count; c++) body.Append(XlsxReport.Text($"{XlsxReport.Letter(c)}3", sheet.Columns[c].Title, 3));
        body.Append("</row>");
        for (int i = 0; i < sheet.Rows.Count; i++)
        {
            var row = i + 4; body.Append($"<row r=\"{row}\">");
            for (int c = 0; c < sheet.Columns.Count; c++)
            {
                var reference = XlsxReport.Letter(c) + row.ToString(CultureInfo.InvariantCulture);
                long? value = null;
                if (i < entries.Count)
                {
                    var e = entries[i];
                    value = c switch { 0 => i + 1, 5 => e.Amount, 6 when !e.ReviewPending => SalaryTrust.Paid(e), 7 when !e.ReviewPending => SalaryTrust.Remaining(e), _ => null };
                }
                else if (c == 7) value = SalaryTrust.Outstanding(entries);
                body.Append(value is { } v ? $"<c r=\"{reference}\" s=\"6\"><v>{v.ToString(CultureInfo.InvariantCulture)}</v></c>" : XlsxReport.Text(reference, sheet.Rows[i][c], 4));
            }
            body.Append("</row>");
        }
        var widths = string.Concat(sheet.Columns.Select((c, i) => $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{c.Width.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>"));
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr><sheetViews><sheetView rightToLeft=\"true\" workbookViewId=\"0\"><pane ySplit=\"3\" topLeftCell=\"A4\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>" +
            $"<cols>{widths}</cols><sheetData>{body}</sheetData><mergeCells count=\"2\"><mergeCell ref=\"A1:{last}1\"/><mergeCell ref=\"A2:{last}2\"/></mergeCells>" +
            "<pageMargins left=\"0.4\" right=\"0.4\" top=\"0.5\" bottom=\"0.5\" header=\"0.3\" footer=\"0.3\"/><pageSetup paperSize=\"9\" orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/></worksheet>";
        return XlsxReport.Package([("الأمانات", xml)], XlsxReport.Styles, "$1:$3");
    }
}

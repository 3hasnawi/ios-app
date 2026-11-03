using System.Globalization;
using System.Text;

namespace SalaryDesktop.Core;
/// Excel لمقارنة الاستقطاعات: المبالغ أرقام عندما لا تتجاوز دقة Excel، وإلا تُحفظ نصاً بلا فقد.
public static class DeductionComparisonReport
{
    public static byte[] Excel(ReportSheet sheet, IReadOnlyList<DeductionChange> changes)
    {
        sheet.Validate();
        if (sheet.Columns.Count != 7 || sheet.Rows.Count != changes.Count) throw new InvalidDataException("صفوف المقارنة لا تطابق الكشف.");
        var body = new StringBuilder();
        body.Append("<row r=\"1\">").Append(XlsxReport.Text("A1", sheet.Header, 1)).Append("</row>");
        body.Append("<row r=\"2\">").Append(XlsxReport.Text("A2", sheet.Title, 2)).Append("</row><row r=\"3\">");
        for (var c = 0; c < 7; c++) body.Append(XlsxReport.Text($"{XlsxReport.Letter(c)}3", sheet.Columns[c].Title, 3));
        body.Append("</row>");
        for (var i = 0; i < changes.Count; i++)
        {
            var expected = changes[i];
            string Exact(decimal n) => n.ToString("0.############################", CultureInfo.InvariantCulture);
            if (sheet.Rows[i][0] != expected.Name || sheet.Rows[i][1] != expected.Entity || sheet.Rows[i][2] != expected.Note ||
                sheet.Rows[i][3] != Exact(expected.Before) || sheet.Rows[i][4] != Exact(expected.After) || sheet.Rows[i][5] != Exact(expected.Difference) || sheet.Rows[i][6] != expected.Status)
                throw new InvalidDataException("تغيّرت بيانات صف المقارنة قبل التصدير.");
            var row = i + 4; body.Append($"<row r=\"{row}\">");
            for (var c = 0; c < 7; c++)
            {
                var reference = XlsxReport.Letter(c) + row.ToString(CultureInfo.InvariantCulture);
                if (c is >= 3 and <= 5)
                {
                    var value = c == 3 ? changes[i].Before : c == 4 ? changes[i].After : changes[i].Difference;
                    var exact = value.ToString("0.############################", CultureInfo.InvariantCulture);
                    var digits = new string(exact.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
                    // Excel يحتفظ بـ15 رقماً مهماً فقط؛ لا نحول ما يتجاوزها إلى رقم يفقد الدقة.
                    body.Append(digits.Length <= 15 ? $"<c r=\"{reference}\" s=\"6\"><v>{exact}</v></c>" : XlsxReport.Text(reference, exact, 4));
                }
                else body.Append(XlsxReport.Text(reference, sheet.Rows[i][c], 4));
            }
            body.Append("</row>");
        }
        var widths = string.Concat(sheet.Columns.Select((c, i) => $"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{c.Width.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>"));
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<sheetViews><sheetView rightToLeft=\"true\" workbookViewId=\"0\"><pane ySplit=\"3\" topLeftCell=\"A4\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>" +
            $"<cols>{widths}</cols><sheetData>{body}</sheetData><mergeCells count=\"2\"><mergeCell ref=\"A1:G1\"/><mergeCell ref=\"A2:G2\"/></mergeCells></worksheet>";
        var styles = XlsxReport.Styles.Replace("formatCode=\"#,##0\"", "formatCode=\"#,##0.############################\"", StringComparison.Ordinal);
        return XlsxReport.Package([("المقارنة", xml)], styles, "$1:$3");
    }
}

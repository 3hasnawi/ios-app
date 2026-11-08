using ClosedXML.Excel;

namespace Payroll.Core;

/// <summary>Writes a portrait, right-to-left statement for one exact branch.</summary>
public static class DeductionExcelWriter
{
    private const decimal ExcelSafeInteger = 999_999_999_999_999m;

    public static void Write(DeductionReportData report, string header, Stream destination)
    {
        if (report.Rows.Count == 0) throw new InvalidOperationException("لا توجد مبالغ لتصديرها في الكشف.");
        // ClosedXML builds the workbook in memory. Keep very large exports from exhausting the desktop process.
        if (report.Rows.Count > 30_000) throw new InvalidOperationException("يتجاوز الكشف ٣٠٬٠٠٠ قيد؛ تصدير هذا الحجم غير مدعوم حالياً حتى لا تنفد ذاكرة التطبيق.");
        if (string.IsNullOrWhiteSpace(header)) throw new InvalidOperationException("أدخل ترويسة الكشف.");
        static bool Safe(decimal value) => decimal.Truncate(value) == value && decimal.Abs(value) <= ExcelSafeInteger;
        if (!Safe(report.Total) || report.Rows.Any(row => !Safe(row.Amount)))
            throw new InvalidOperationException("يحتوي الكشف على كسور أو مبلغ كبير لا يمكن ضمان دقته في Excel. راجع المبالغ قبل التصدير.");
        decimal verifiedTotal = 0, absoluteTotal = 0;
        foreach (var row in report.Rows)
        {
            if (row.Rank.Length > 32767 || row.Name.Length > 32767 || row.Note.Length > 32767)
                throw new InvalidOperationException("أحد حقول الكشف أطول من الحد المدعوم في خلية Excel.");
            if (Math.Max(Lines(row.Rank, 8), Math.Max(Lines(row.Name, 31), Lines(row.Note, 29))) * 16 + 6 > 409)
                throw new InvalidOperationException("أحد الأسماء أو أرقام الإضبارة طويل جداً للعرض كاملاً في صف Excel واحد.");
            verifiedTotal = checked(verifiedTotal + row.Amount);
            absoluteTotal = checked(absoluteTotal + decimal.Abs(row.Amount));
        }
        if (!Safe(absoluteTotal)) throw new InvalidOperationException("مجموع القيم المطلقة أكبر من الحد الذي يمكن ضمان دقته في Excel.");
        if (verifiedTotal != report.Total) throw new InvalidDataException("مجموع الكشف لا يطابق صفوفه؛ لم يُصدّر.");

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("الكشف");
        sheet.RightToLeft = true;
        sheet.Style.Font.FontName = "Arial";
        sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        sheet.PageSetup.PaperSize = XLPaperSize.A4Paper;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(1, 3);
        sheet.PageSetup.Margins.Left = sheet.PageSetup.Margins.Right = 0.4;
        sheet.PageSetup.Margins.Top = sheet.PageSetup.Margins.Bottom = 0.5;
        sheet.SheetView.FreezeRows(3);
        var widths = new double[] { 5, 11, 34, 18, 32 };
        for (var c = 1; c <= widths.Length; c++) sheet.Column(c).Width = widths[c - 1];

        sheet.Range("A1:E1").Merge();
        sheet.Cell("A1").Value = header;
        var headerHeight = Math.Max(24, Lines(header, 85) * 18 + 6);
        if (header.Length > 32767 || headerHeight > 409) throw new InvalidOperationException("ترويسة الكشف طويلة جداً للعرض كاملاً في Excel.");
        sheet.Row(1).Height = headerHeight;
        sheet.Cell("A1").Style.Font.SetBold().Font.SetFontSize(14);
        sheet.Range("A2:E2").Merge();
        var title = $"قائمة الاستقطاعات لـ شهر {EasternDigits(report.Month)} - {report.Entity}";
        sheet.Cell("A2").Value = title;
        var titleHeight = Math.Max(30, Lines(title, 75) * 20 + 8);
        if (title.Length > 32767 || titleHeight > 409) throw new InvalidOperationException("عنوان الكشف طويل جداً للعرض كاملاً في Excel.");
        sheet.Row(2).Height = titleHeight;
        sheet.Range("A2:E2").Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
        sheet.Cell("A2").Style.Font.SetBold().Font.SetFontSize(15).Font.SetFontColor(XLColor.White);
        string[] columns = ["ت", "الرتبة", "الاسم الكامل", "المبلغ", "رقم وتاريخ الاضبارة"];
        for (int i = 0; i < columns.Length; i++) sheet.Cell(3, i + 1).Value = columns[i];
        sheet.Row(3).Height = 22;
        sheet.Range("A3:E3").Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEBF7");
        sheet.Range("A3:E3").Style.Font.SetBold().Font.SetFontSize(11);
        sheet.Range("A1:E3").Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Alignment.SetVertical(XLAlignmentVerticalValues.Center).Alignment.SetWrapText();

        for (var i = 0; i < report.Rows.Count; i++)
        {
            var row = report.Rows[i];
            var n = i + 4;
            sheet.Cell(n, 1).Value = i + 1;
            sheet.Cell(n, 2).Value = row.Rank;
            sheet.Cell(n, 3).Value = row.Name;
            sheet.Cell(n, 4).Value = (double)row.Amount;
            sheet.Cell(n, 5).Value = row.Note;
            var lines = Math.Max(Lines(row.Rank, 8), Math.Max(Lines(row.Name, 31), Lines(row.Note, 29)));
            var height = Math.Max(24, lines * 16 + 6);
            if (height > 409) throw new InvalidOperationException("أحد الأسماء أو أرقام الإضبارة طويل جداً للعرض كاملاً في صف Excel واحد.");
            sheet.Row(n).Height = height;
            sheet.Range(n, 1, n, 5).Style.Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Center);
            sheet.Cell(n, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Cell(n, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Cell(n, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            sheet.Cell(n, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            sheet.Cell(n, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            sheet.Range(n, 1, n, 5).Style.Font.FontSize = 10.5;
            sheet.Cell(n, 3).Style.Font.SetBold().Font.SetFontSize(11.5);
            sheet.Cell(n, 4).Style.Font.SetBold().Font.SetFontSize(11);
            sheet.Cell(n, 4).Style.NumberFormat.Format = "#,##0";
            if ((i & 1) == 1) sheet.Range(n, 1, n, 5).Style.Fill.BackgroundColor = XLColor.FromHtml("#F3F7FB");
        }
        var totalRow = report.Rows.Count + 4;
        sheet.Range(totalRow, 1, totalRow, 3).Merge();
        sheet.Cell(totalRow, 1).Value = "المجموع";
        sheet.Cell(totalRow, 4).FormulaA1 = $"SUM(D4:D{totalRow - 1})";
        sheet.Cell(totalRow, 4).Style.NumberFormat.Format = "#,##0";
        sheet.Range(totalRow, 1, totalRow, 5).Style.Font.SetBold().Font.SetFontSize(11.5);
        sheet.Range(totalRow, 1, totalRow, 5).Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEBF7");
        sheet.Row(totalRow).Height = 24;
        var borders = sheet.Range(2, 1, totalRow, 5).Style.Border;
        borders.SetInsideBorder(XLBorderStyleValues.Thin);
        borders.SetOutsideBorder(XLBorderStyleValues.Thin);
        workbook.SaveAs(destination, new SaveOptions { EvaluateFormulasBeforeSaving = true });
    }

    private static int Lines(string value, int width) => value.Split('\n').Sum(line => Math.Max(1, (line.Length + width - 1) / width));
    private static string EasternDigits(string value) => string.Concat(value.Select(c => c is >= '0' and <= '9' ? (char)('٠' + c - '0') : c));
}

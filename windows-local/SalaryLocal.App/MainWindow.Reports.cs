using System.IO;
using System.Text.RegularExpressions;
using SalaryDesktop.Core;
namespace SalaryLocal.App;
public partial class MainWindow
{
    private static byte[] SpecialPdf(BackupSnapshot source, string month, string key)
    {
        var assets = ReportAssets.Load(Path.Combine(AppContext.BaseDirectory, "WatermarkLogo.png")); var stamp = DateTime.Now;
        if (FundReport.Applies(key)) return FundReport.Pdf(FundReport.Summary(source, month, key), FundReport.Title(source, month), assets, stamp);
        if (FoodContractReport.Applies(key)) return FoodContractReport.Pdf(FoodContractReport.Rows(source, month, key), FoodContractReport.Title(source, month), assets, stamp);
        if (HealthCareReport.Applies(key)) return HealthCareReport.Pdf(HealthCareReport.Build(source, month, key), HealthCareReport.Title(source, month, false), HealthCareReport.Title(source, month, true), assets, stamp);
        return ReportPdf.Generate([StandardReports.ItemList(source, month, key)], assets, stamp);
    }
    private static ReportSheet EntitySheet(BackupSnapshot source, string month, string entity)
    {
        if (source.Deductions.Any(d => d.Month == month && d.Title == entity && !PayrollAnalysis.IsInactive(d) && d.Amount is null or < 0)) throw new InvalidDataException("توجد مبالغ تنفيذ ناقصة أو سالبة؛ صحح المصدر قبل تصدير كشف الجهة.");
        return StandardReports.EntityDeductions(source, month, entity);
    }
    private void BulkEntities(BackupSnapshot source, string month)
    {
        (string Name, ReportSheet Sheet)[] Sheets()
        {
            var titles = PayrollAnalysis.Groups(source, month).Select(g => g.Title).ToArray(); if (titles.Length == 0) throw new InvalidDataException("لا توجد جهات تنفيذ محتسبة لهذا الشهر.");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<(string, ReportSheet)>();
            foreach (var title in titles) { var cleaned = Regex.Replace(title, @"[\\/:*?\[\]]", "-").Trim().Trim('\''); if (cleaned.Length == 0) cleaned = "جهة"; var stem = cleaned[..Math.Min(cleaned.Length, 25)]; var name = stem; int i = 1; while (!used.Add(name)) name = stem + " " + (++i); result.Add((name, EntitySheet(source, month, title))); }
            return result.ToArray();
        }
        SaveExcel(() => XlsxReport.Generate(Sheets()), "جهات التنفيذ - " + month, () => ReportPdf.Generate(Sheets().Select(s => s.Sheet).ToArray(), ReportAssets.Load(Path.Combine(AppContext.BaseDirectory, "WatermarkLogo.png")), DateTime.Now));
    }
}

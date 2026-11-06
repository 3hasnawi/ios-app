using System.Globalization;
using System.Text;
using System.Text.Json;
using Claunia.PropertyList;
using SalaryDesktop.Core;

internal static class BankCorrectionTests
{
    private static int checks;
    private static void Check(bool condition, string label) { checks++; if (!condition) throw new Exception(label); }
    private static byte[] Plist(NSObject value) { using var memory = new MemoryStream(); PropertyListParser.SaveAsBinary(value, memory); return memory.ToArray(); }
    private static void Reject(Action action, string label) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(label); }
    private const string Month = "2026-11";
    private static BackupSnapshot Fixture(decimal nominal, decimal retirement = 2)
    {
        var details = new Dictionary<string, string> {
            ["طريقة الدفع"] = "بطاقة", ["الاسمي"] = nominal.ToString(CultureInfo.InvariantCulture),
            ["توقيفات تقاعدية"] = retirement.ToString(CultureInfo.InvariantCulture), ["مجموع استقطاعات المصارف"] = "7" };
        var items = new Dictionary<string, decimal> { ["صندوق الشهداء"] = 0, ["الرعاية الصحية"] = 0, ["النشاط الرياضي"] = 0,
            ["الضريبة"] = 0, ["الحماية الاجتماعية"] = 0, ["شركة عراق الجوال"] = 0 };
        var account = new ExecutionAccount("جهة وهمية", "IQ06TEST123000000000001", "مصرف الرافدين فرع وهمي", "TESTIQBA", null);
        return new BackupSnapshot("synthetic", new string('A', 64), 6, [new(Month, 1, 1)],
            [new(Month, "منتسب وهمي", "عقود", "قسم وهمي", 1000, JsonSerializer.SerializeToUtf8Bytes(items), JsonSerializer.SerializeToUtf8Bytes(details), null)],
            [new(Month, "منتسب وهمي", account.Entity, 7, "إضبارة وهمية", "عقود")], 0, [account], [],
            new Dictionary<string, byte[]> { ["bankStatementAccountName"] = Plist(new NSString("حساب دافع وهمي")), ["bankStatementIBAN"] = Plist(new NSString("IQ76TEST123000000000002")) });
    }
    public static void Run()
    {
        Check(typeof(BankStatementCalculator).Assembly.GetName().Name == "SalaryLocal.Engine", "Application uses corrected local engine, not unchanged legacy assembly");
        // نفس حالات المرجع في TransferFilesSelfTest.swift مع كسور حول الحد.
        foreach (var (nominal, share) in new (decimal, decimal)[] { (0, 0), (1000, 120), (1041, 120), (1042, 130), (1125, 140), (178723001, 21446760), (1124.999m, 130), (1125.001m, 140) })
        {
            var bank = BankStatementCalculator.Calculate(Fixture(nominal), Month);
            Check(bank.ContractNominalShare == share, "iPhone nearest-ten parity: " + nominal);
            Check(bank.CalculatedContractContribution == share + 2, "Retirement added after rounding: " + nominal);
            Check(bank.BankTotal == 1000 + 7 + share + 2, "Bank total uses corrected amount once: " + nominal);
        }
        var source = Fixture(1125); var summary = BankStatementCalculator.Calculate(source, Month);
        Check(summary.TransferWarnings.Count == 0 && summary.ExecutionMatched, "Complete synthetic sources");
        var suggestions = ReconciliationSuggestions.Prepare(source, Month);
        Check(suggestions.Items.Single(i => i.Id.EndsWith("|contracts", StringComparison.Ordinal)).Amount == 142, "Reconciliation consumes corrected contract amount");
        var transfer = TransferPreparation.Prepare(source, Month); var date = new DateOnly(2026, 11, 5);
        var csv = TransferPreparation.MakeCsv(transfer, TransferGroup.Rafidain, date);
        TransferPreparation.VerifyCsv(csv, transfer, TransferGroup.Rafidain, date);
        Check(Encoding.UTF8.GetString(csv).Contains("|7|IQD|", StringComparison.Ordinal), "Transfer CSV keeps execution deduction, does not add contract twice");
        Check(!csv.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "CSV UTF-8 without BOM");
        Reject(() => TransferPreparation.VerifyCsv(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(csv).Replace("|7|IQD|", "|8|IQD|", StringComparison.Ordinal)), transfer, TransferGroup.Rafidain, date), "CSV tampering rejected");
        var settings = new Dictionary<string, byte[]>(source.SettingsPlists) { [ContractOverrides.SettingsKey] = ContractOverrides.Set(null, Month, new(200, 1125, 2, 1, 0)) };
        var overridden = source with { SettingsPlists = settings }; var adjusted = BankStatementCalculator.Calculate(overridden, Month);
        Check(adjusted.CalculatedContractContribution == 142 && adjusted.ContractContribution == 200, "Explicit matching override remains separate from corrected base");
        var changed = Fixture(1042) with { SettingsPlists = settings }; var stale = BankStatementCalculator.Calculate(changed, Month);
        Check(stale.ContractOverrideStale && stale.ContractContribution == 132, "Changed nominal invalidates saved override");
        Check(!ReconciliationSuggestions.Prepare(changed, Month).Items.Any(i => i.Id.EndsWith("|contracts", StringComparison.Ordinal)), "Stale override prevents suggested contract posting");
        var rank = source with { Employees = source.Employees.Select(e => e with { Rank = "عقد" }).ToArray() };
        Check(BankStatementCalculator.Calculate(rank, Month).ContractCount == 0, "Only عقود rank participates");
        Console.WriteLine($"Bank correction: {checks} assertions passed (iPhone rounding, bank, CSV, reconciliation, override invalidation).");
    }
}

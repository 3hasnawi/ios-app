using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SalaryDesktop.Core;
using SalaryLocal.Core;

internal static class Benchmark
{
    public static void Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("--benchmark input.xlsx month output-directory");
        string input = Path.GetFullPath(args[1]), month = args[2], output = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(output);
        string hash; using (var f = File.OpenRead(input)) hash = Convert.ToHexString(SHA256.HashData(f));
        var timer = Stopwatch.StartNew(); var preview = PayrollSpreadsheet.Read(input, month); var parse = timer.Elapsed.TotalSeconds;
        if (preview.Problems.Count > 0) throw new InvalidDataException(string.Join("; ", preview.Problems.Take(5)));
        using var store = new LocalPayrollStore(Path.Combine(output, "dataset"));
        timer.Restart(); var imported = store.Import(preview, Path.GetFileName(input), hash, store.GetStatus().RevisionId, false); var import = timer.Elapsed.TotalSeconds;
        timer.Restart(); var before = store.GetStatus(); var summaries = store.GetMonths(); var summary = timer.Elapsed.TotalSeconds;
        timer.Restart(); var duplicate = store.Import(preview, Path.GetFileName(input), hash, before.RevisionId, false); var repeat = timer.Elapsed.TotalSeconds;
        if (duplicate.Changed || store.GetStatus().EmployeeRowCount != before.EmployeeRowCount || store.GetStatus().DatabaseBytes != before.DatabaseBytes) throw new Exception("Repeated import grew data");
        timer.Restart(); var after = store.GetSnapshot(month); var load = timer.Elapsed.TotalSeconds;
        if (after.Employees.Count != preview.Employees.Count || after.Deductions.Count != preview.Deductions.Count) throw new Exception("Count differs");
        for (int i = 0; i < preview.Employees.Count; i++)
            if (!JsonSerializer.SerializeToUtf8Bytes(preview.Employees[i]).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(after.Employees[i]))) throw new Exception("Employee differs");
        var expected = new BackupSnapshot(input, hash, 6, [new(month, preview.Employees.Count, preview.Deductions.Count)], preview.Employees, preview.Deductions, 0, [], [], new Dictionary<string, byte[]>());
        if (JsonSerializer.Serialize(BankStatementCalculator.Calculate(expected, month)) != JsonSerializer.Serialize(BankStatementCalculator.Calculate(after, month))) throw new Exception("Bank calculation differs");
        string backup = Path.Combine(output, "synthetic.salarybackup");
        timer.Restart(); BackupService.Create(store, backup); var backupSeconds = timer.Elapsed.TotalSeconds;
        timer.Restart(); var review = BackupService.Inspect(backup, store); var restoredPath = BackupService.RestoreAsNewWorkspace(backup, Path.Combine(output, "restored"), review.Digest); var restore = timer.Elapsed.TotalSeconds;
        using var restored = new LocalPayrollStore(restoredPath);
        var roundtrip = restored.GetSnapshot(month);
        if (JsonSerializer.Serialize(roundtrip.Employees) != JsonSerializer.Serialize(after.Employees) || JsonSerializer.Serialize(roundtrip.Deductions) != JsonSerializer.Serialize(after.Deductions)) throw new Exception("Restore differs");
        using (var f = File.OpenRead(input)) if (hash != Convert.ToHexString(SHA256.HashData(f))) throw new Exception("Original source modified");
        var iphone = Path.Combine(output, "synthetic-iphone.zip");
        timer.Restart(); IphoneInteropService.Export(store, iphone); var iphoneExport = timer.Elapsed.TotalSeconds;
        timer.Restart(); var iphoneReview = IphoneInteropService.Inspect(iphone); var iphonePath = IphoneInteropService.ImportNewWorkspace(iphone, Path.Combine(output, "iphone-restored"), iphoneReview.Digest); var iphoneRestore = timer.Elapsed.TotalSeconds;
        using (var iphoneStore = new LocalPayrollStore(iphonePath))
        {
            var read = iphoneStore.GetSnapshot(month);
            if (JsonSerializer.Serialize(read.Employees) != JsonSerializer.Serialize(after.Employees) || JsonSerializer.Serialize(read.Deductions) != JsonSerializer.Serialize(after.Deductions)) throw new Exception("iPhone format roundtrip differs");
        }
        var results = new { SyntheticDataOnly = true, Employees = preview.Employees.Count, Deductions = preview.Deductions.Count, SourceXlsxBytes = new FileInfo(input).Length,
            RepeatedRawColumnsJsonBytes = preview.Employees.Sum(e => (long)(e.RawColumns?.Length ?? 0)), DatabaseBytes = before.DatabaseBytes, BackupBytes = new FileInfo(backup).Length,
            HeaderSchemas = before.HeaderSchemaCount, UniqueEmployeeRows = before.EmployeeRowCount, RepeatedImportAddedRows = 0, RepeatedImportAddedDatabaseBytes = 0,
            ParseSeconds = parse, ImportSeconds = import, SummarySeconds = summary, RepeatedImportSeconds = repeat, LoadMonthSeconds = load, BackupSeconds = backupSeconds, InspectAndRestoreSeconds = restore,
            IphoneArchiveBytes = new FileInfo(iphone).Length, IphoneExportSeconds = iphoneExport, IphoneInspectAndImportSeconds = iphoneRestore, IphoneRecordRoundtrip = true,
            FullRecordRoundtrip = true, BankCalculationParity = true, SourceFileUnchanged = true };
        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "benchmark.json"), json); Console.WriteLine(json);
    }
}

using System.Diagnostics;

if (args.Length > 0 && args[0] == "--benchmark") { Benchmark.Run(args); return; }

var timer = Stopwatch.StartNew();
StoreTests.Run();
BackupTests.Run();
WorkspaceTests.Run();
InteropTests.Run();
LocalReconciliationTests.Run();
BankCorrectionTests.Run();
LocalSalaryTrustTests.Run();
LocalTrustSpreadsheetTests.Run();
LocalMonthlyCashTrustTests.Run();
LocalArchiveTests.Run();
LocalAccessReaderTests.Run();
LocalStaffTests.Run();
LocalTransferLettersTests.Run();
LocalTransferLetterBatchTests.Run();
LocalPdfPrintLayoutTests.Run();
LocalLegacyMigrationTests.Run();
LocalSessionLockTests.Run();
LocalTransferRecipientsTests.Run();
LocalArchiveImageTests.Run();
Console.WriteLine($"All local-store and backup safety tests passed in {timer.Elapsed.TotalSeconds:F2}s.");

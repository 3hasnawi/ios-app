using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SalaryDesktop.Core;
using SalaryLocal.Core;
namespace SalaryLocal.Smoke;
internal static class Fixture
{
    public static void Seed(LocalPayrollStore store)
    {
        if (store.HasData() || store.GetStatus().RevisionCount != 0) throw new InvalidOperationException("Refusing nonempty workspace.");
        string[] months = ["أيلول 2026", "تشرين الأول 2026"];
        string[] names = ["موظف وهمي أول TEST", "موظفة وهمية ثانية TEST", "منتسب وهمي ثالث TEST", "متعاقد وهمي رابع TEST"];
        string[] ranks = ["نقيب", "موظف", "شرطي", "عقود"];
        var employees = months.SelectMany((month, mi) => names.Select((name, i) => new Employee(month, name, ranks[i], "وحدة اختبار وهمية TEST", 700000 + i * 50000 + mi * 10000,
            JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, decimal> { ["الاسمي"] = 500000, ["صندوق الشهداء"] = i == 0 ? 5000 : 4000, ["الرعاية الصحية"] = 7000, ["شركة عراق الجوال"] = 20000, ["الضريبة"] = 10000, ["مجموع استقطاعات المصارف"] = 25000 }),
            JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["طريقة الدفع"] = i % 2 == 0 ? "كاش" : "بطاقة", ["IBAN"] = "TEST-NOT-A-BANK-ACCOUNT-" + i, ["الرقم الموحد"] = "TEST-" + i }), null))).ToArray();
        var deductions = months.SelectMany((month, mi) => names.Select((name, i) => new Deduction(month, name, "جهة تنفيذ وهمية TEST", 25000 + mi * 1000, "إضبارة اختبار TEST فقط", ranks[i]))).ToArray();
        var staff = names.Select((name, i) => new StaffMember(name, "أم وهمية TEST", "TEST-STAT-" + i, "TEST-ID-" + i, ranks[i], "مستمر", "وحدة اختبار وهمية TEST", null)).ToArray();
        var period = new BankPeriod(Guid.NewGuid(), months[1], new(2026, 10, 1), new(2026, 10, 31), 1000000, "عضو وهمي TEST 1", "عضو وهمي TEST 2", null, null,
            [new(Guid.NewGuid(), new(2026, 10, 5), "حركة اختبار وهمية TEST", 200000, MovementDirection.Credit)]);
        var trust = new SalaryTrustLedger(1, [new(Guid.NewGuid().ToString(), names[0], ranks[0], "وحدة اختبار TEST", 2026, 9, 700000, "اختبار", "بيانات وهمية فقط", "", false, false, [])], true);
        var settings = new Dictionary<string, byte[]> { [BankReconciliation.SettingsKey] = BankReconciliation.LedgerPlist([period]), [SalaryTrust.SettingsKey] = SalaryTrust.Plist(trust) };
        var source = new BackupSnapshot("SYNTHETIC-ONLY", new string('A', 64), 6, months.Select(m => new PayrollMonth(m, 4, 4)).ToArray(), employees, deductions, staff.Length,
            [new("جهة تنفيذ وهمية TEST", "TEST-NOT-A-VALID-IBAN", "مصرف وهمي TEST", "TESTIQ00", null)], [], settings);
        store.ImportDataset(source, staff, new Dictionary<string, byte[]>(), store.GetStatus().RevisionId);
        var sheet = new ReportSheet("بيانات وهمية TEST", "كتاب اختبار وهمي غير رسمي", [new("البيان", 60, ReportColumnKind.Text)], [new[] { "هذا مستند اصطناعي للاختبار فقط TEST" }], [], ShowTotal: false);
        store.AddArchiveBook("كتاب وهمي للاختبار TEST", months[1], "جهة تنفيذ وهمية TEST", ReportPdf.Generate([sheet], ReportAssets.Load(null), new DateTime(2026, 10, 1)), store.GetStatus().RevisionId);
        if (store.GetSnapshot().Employees.Count != 8 || store.GetStaff().Count != 4 || store.GetSnapshot().Books.Count != 1) throw new InvalidOperationException("Fixture validation failed.");
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ExcelDataReader;

namespace SalaryDesktop.Core;

public sealed record AccountImportProblem(string Sheet, int Row, string Message);
public sealed record AccountImportPreview(IReadOnlyList<ExecutionAccount> Accounts, IReadOnlyList<AccountImportProblem> Problems);
public static class AccountSpreadsheet
{
    public static AccountImportPreview Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 32 * 1024 * 1024 || file.Length == 0) throw new InvalidDataException("حجم ملف Excel غير مدعوم.");
        try
        {
            var digest = SHA256.HashData(file); file.Position = 0;
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read, true))
            {
                long size = 0; var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries)
                {
                    if (!names.Add(entry.FullName) || entry.Length > 128 * 1024 * 1024 || (size += entry.Length) > 256 * 1024 * 1024) throw new InvalidDataException("ملف Excel يحتوي بيانات مكررة أو يتجاوز الحجم المدعوم.");
                }
            }
            file.Position = 0;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(file);
            var accounts = new List<ExecutionAccount>(); var problems = new List<AccountImportProblem>();
            var seen = new HashSet<string>(StringComparer.Ordinal); bool found = false; int totalRows = 0;
            do
            {
                var sheet = reader.Name ?? "ورقة"; int row = 0, entity = -1, iban = -1, branch = -1, swift = -1;
                while (reader.Read())
                {
                    if (++row > 100_000 || ++totalRows > 200_000 || reader.FieldCount > 500) throw new InvalidDataException("عدد صفوف ملف الحسابات أو أعمدته يتجاوز الحد المدعوم.");
                    string Cell(int i) => i < 0 || i >= reader.FieldCount ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? "";
                    if (entity < 0)
                    {
                        if (row > 20) break;
                        var headers = Enumerable.Range(0, reader.FieldCount).Select(i => PayrollAnalysis.Normalize(Cell(i))).ToArray();
                        int Find(string[] names, bool exact, HashSet<int> used)
                        {
                            foreach (var name in names.Select(PayrollAnalysis.Normalize))
                                for (var i = 0; i < headers.Length; i++) if (!used.Contains(i) && headers[i].Length != 0 && (exact ? headers[i] == name : headers[i].Contains(name, StringComparison.Ordinal))) { used.Add(i); return i; }
                            return -1;
                        }
                        var used = new HashSet<int>();
                        var candidate = Find(["جهة التنفيذ", "الجهة", "المديرية"], true, used);
                        var candidateIban = Find(["رقم الايبان", "الايبان", "iban"], true, used);
                        if (candidateIban < 0) candidateIban = Find(["رقم الايبان", "الايبان", "iban"], false, used);
                        if (candidate < 0 || candidateIban < 0) continue;
                        entity = candidate; iban = candidateIban; branch = Find(["الفرع", "اسم الفرع"], true, used); if (branch < 0) branch = Find(["الفرع", "اسم الفرع"], false, used);
                        swift = Find(["swift", "رمز السويفت"], true, used); if (swift < 0) swift = Find(["swift", "رمز السويفت"], false, used);
                        found = true; continue;
                    }
                    var name = Cell(entity);
                    if (name.Length == 0)
                    {
                        if (Cell(iban).Length > 0 || Cell(branch).Length > 0 || Cell(swift).Length > 0) problems.Add(new(sheet, row, "توجد بيانات حساب دون اسم جهة."));
                        continue;
                    }
                    if (!seen.Add(PayrollAnalysis.Normalize(name))) { problems.Add(new(sheet, row, "اسم الجهة مكرر في الملف.")); continue; }
                    if (reader.GetValue(iban) is not string) { problems.Add(new(sheet, row, "احفظ خانة الحساب كنص؛ القيمة الرقمية قد تفقد أصفاراً.")); continue; }
                    var entered = TransferPreparation.NormalizeAccount(Cell(iban)); var bank = Cell(branch); var bic = Cell(swift).ToUpperInvariant();
                    var candidateAccount = new ExecutionAccount(name, entered, bank, bic, null);
                    var allowed = PayrollAnalysis.Normalize(name).Contains(PayrollAnalysis.Normalize("صندوق الإسكان"), StringComparison.Ordinal) || PayrollAnalysis.Normalize(name).Contains(PayrollAnalysis.Normalize("الموارد المائية"), StringComparison.Ordinal);
                    if (entered.Length == 15 && entered.All(char.IsAsciiDigit) && allowed && TransferPreparation.Classify(candidateAccount) == TransferGroup.Anbar)
                        candidateAccount = candidateAccount with { Iban = "", LocalAccount = entered };
                    else if (!TransferPreparation.ValidIban(entered)) { problems.Add(new(sheet, row, "IBAN غير صالح أو غير مكتمل.")); continue; }
                    if (bic.Length > 0 && !TransferPreparation.ValidSwift(bic)) { problems.Add(new(sheet, row, "رمز SWIFT غير صالح.")); continue; }
                    accounts.Add(candidateAccount);
                }
            } while (reader.NextResult());
            if (!found) throw new InvalidDataException("لم يُعثر على أعمدة الجهة وIBAN في أول 20 صفاً من أي ورقة.");
            if (accounts.Count == 0 && problems.Count == 0) throw new InvalidDataException("لم يُعثر على حسابات في الملف.");
            file.Position = 0;
            if (!digest.AsSpan().SequenceEqual(SHA256.HashData(file))) throw new InvalidDataException("تغيّر ملف Excel أثناء قراءته؛ لم تُغيّر البيانات.");
            return new(accounts, problems);
        }
        catch (Exception ex) when (ex is not InvalidDataException and not IOException and not UnauthorizedAccessException)
        { throw new InvalidDataException("تعذّر قراءة ملف حسابات Excel؛ لم تُغيّر البيانات.", ex); }
    }
}

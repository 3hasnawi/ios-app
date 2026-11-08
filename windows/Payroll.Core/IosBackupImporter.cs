using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Payroll.Core;

public sealed record ImportSummary(string Directory, int FormatVersion, int Employees, int Deductions, int Staff, int Accounts, int Books, IReadOnlyList<string> PayrollMonths, int ArchivedPayrollRows);

/// Imports to a new, isolated dataset. The source archive and every previously imported dataset remain untouched.
public static class IosBackupImporter
{
    private const long MaxArchiveBytes = 16L * 1024 * 1024 * 1024;
    private const long MaxEntryBytes = 8L * 1024 * 1024 * 1024;
    private const int MaxLineCharacters = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<ImportSummary> ImportAsync(string backupFile, string dataRoot, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var root = Path.GetFullPath(dataRoot);
        System.IO.Directory.CreateDirectory(root);
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(root, ".import-" + id);
        var destination = Path.Combine(root, "dataset-" + id);
        System.IO.Directory.CreateDirectory(staging);
        var completed = false;
        try
        {
            progress?.Report("نسخ النسخة الأصلية بأمان…");
            var copied = Path.Combine(staging, "original.zip");
            var initial = new FileInfo(backupFile);
            if (!initial.Exists || initial.Length <= 0 || initial.Length > MaxArchiveBytes) throw new InvalidDataException("حجم ملف النسخة غير صالح.");
            var initialLength = initial.Length;
            var initialWrite = initial.LastWriteTimeUtc;
            await using (var source = new FileStream(backupFile, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var target = new FileStream(copied, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await source.CopyToAsync(target, cancellation);
            initial.Refresh();
            if (!initial.Exists || initial.Length != initialLength || initial.LastWriteTimeUtc != initialWrite || new FileInfo(copied).Length != initialLength)
                throw new IOException("تغيّر ملف النسخة أثناء قراءته؛ لم يُستورد.");
            using var zip = ZipFile.OpenRead(copied);
            var entries = IndexEntries(zip);
            using var manifest = await ReadSmallJson(entries, "manifest.json", 1_000_000, cancellation);
            var rootJson = manifest.RootElement;
            var version = RequiredInt(rootJson, "version");
            if (!((version >= 1 && version <= 6) || version is 8 or 9 or 10)) throw new InvalidDataException("إصدار النسخة غير مدعوم أو أحدث من البرنامج.");
            var expectedEmployees = RequiredInt(rootJson, "employees");
            var expectedDeductions = RequiredInt(rootJson, "deductions");
            var expectedStaff = RequiredInt(rootJson, "staff");
            var expectedAccounts = version >= 2 ? RequiredInt(rootJson, "accounts") : 0;
            var expectedBooks = version >= 3 ? RequiredInt(rootJson, "books") : 0;
            if (new[] { expectedEmployees, expectedDeductions, expectedStaff, expectedAccounts, expectedBooks }.Any(x => x < 0) || expectedBooks > 10_000)
                throw new InvalidDataException("أعداد السجلات في النسخة غير صالحة.");
            var declaredMonths = rootJson.GetProperty("months").EnumerateArray().Select(x => x.GetString() ?? throw new InvalidDataException("اسم شهر غير صالح.")).ToArray();
            if (declaredMonths.Any(string.IsNullOrWhiteSpace) || declaredMonths.Distinct(StringComparer.Ordinal).Count() != declaredMonths.Length)
                throw new InvalidDataException("فهرس الأشهر في النسخة غير صالح.");
            if (declaredMonths.Length == 0 && (long)expectedEmployees + expectedDeductions + expectedStaff + expectedAccounts + expectedBooks == 0)
                throw new InvalidDataException("النسخة لا تحتوي بيانات.");
            using var settings = await ReadSmallJson(entries, "settings.json", 256L * 1024 * 1024, cancellation);
            if (settings.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("إعدادات النسخة غير صالحة.");
            await ValidateBooks(entries, version, expectedBooks, cancellation);
            progress?.Report("فحص السجلات وإنشاء قاعدة بيانات محلية…");
            var databasePath = Path.Combine(staging, "data.sqlite");
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            await db.OpenAsync(cancellation);
            using (var init = db.CreateCommand())
            {
                init.CommandText = """
                    PRAGMA journal_mode=DELETE;
                    PRAGMA synchronous=FULL;
                    CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE months(name TEXT PRIMARY KEY, imported_at TEXT, employee_count INTEGER NOT NULL, deduction_count INTEGER NOT NULL);
                    CREATE TABLE employees(id INTEGER PRIMARY KEY, month TEXT NOT NULL, name TEXT NOT NULL, rank TEXT NOT NULL, workplace TEXT NOT NULL, net TEXT, original BLOB NOT NULL);
                    CREATE TABLE deductions(id INTEGER PRIMARY KEY, month TEXT NOT NULL, name TEXT NOT NULL, title TEXT NOT NULL, amount TEXT, note TEXT NOT NULL, rank TEXT NOT NULL, original BLOB NOT NULL);
                    CREATE TABLE staff(id INTEGER PRIMARY KEY, name TEXT NOT NULL, original BLOB NOT NULL);
                    CREATE TABLE accounts(id INTEGER PRIMARY KEY, entity TEXT NOT NULL, original BLOB NOT NULL);
                    """;
                await init.ExecuteNonQueryAsync(cancellation);
            }
            using var transaction = db.BeginTransaction();
            var monthSet = declaredMonths.ToHashSet(StringComparer.Ordinal);
            var seenMonths = new HashSet<string>(StringComparer.Ordinal);
            using var addMonth = Command(db, transaction, "INSERT INTO months VALUES ($name,$date,$employees,$deductions)", "$name", "$date", "$employees", "$deductions");
            var monthCount = await ReadRecords(entries, "months.jsonl", record =>
            {
                var name = RequiredString(record, "name");
                if (!monthSet.Contains(name) || !seenMonths.Add(name)) throw new InvalidDataException("فهرس الأشهر لا يطابق السجلات.");
                Set(addMonth, name, record.TryGetProperty("importedAt", out var date) ? date.GetRawText() : null, RequiredInt(record, "employeeCount"), RequiredInt(record, "deductionCount"));
                addMonth.ExecuteNonQuery();
            }, cancellation);
            if (monthCount != declaredMonths.Length) throw new InvalidDataException("عدد الأشهر لا يطابق النسخة.");
            var payrollMonths = new HashSet<string>(StringComparer.Ordinal);
            using var addEmployee = Command(db, transaction, "INSERT INTO employees(month,name,rank,workplace,net,original) VALUES ($month,$name,$rank,$workplace,$net,$original)", "$month", "$name", "$rank", "$workplace", "$net", "$original");
            var employees = await ReadRecords(entries, "employees.jsonl", record =>
            {
                var month = RequiredString(record, "month");
                if (!monthSet.Contains(month)) throw new InvalidDataException("سجل راتب مرتبط بشهر غير موجود.");
                payrollMonths.Add(month);
                Set(addEmployee, month, RequiredString(record, "name"), RequiredString(record, "rank", true), RequiredString(record, "workplace", true), Number(record, "net"), Compress(record));
                addEmployee.ExecuteNonQuery();
            }, cancellation);
            if (employees != expectedEmployees) throw new InvalidDataException("عدد سجلات الرواتب لا يطابق النسخة.");
            var keep = MonthOrder.LatestTwo(payrollMonths);
            using (var removeOld = db.CreateCommand())
            {
                removeOld.Transaction = transaction;
                removeOld.CommandText = "DELETE FROM employees WHERE month NOT IN (SELECT value FROM json_each($keep))";
                removeOld.Parameters.AddWithValue("$keep", JsonSerializer.Serialize(keep));
                removeOld.ExecuteNonQuery();
            }
            using var addDeduction = Command(db, transaction, "INSERT INTO deductions(month,name,title,amount,note,rank,original) VALUES ($month,$name,$title,$amount,$note,$rank,$original)", "$month", "$name", "$title", "$amount", "$note", "$rank", "$original");
            var deductions = await ReadRecords(entries, "deductions.jsonl", record =>
            {
                var month = RequiredString(record, "month");
                if (!monthSet.Contains(month)) throw new InvalidDataException("سجل استقطاع مرتبط بشهر غير موجود.");
                Set(addDeduction, month, RequiredString(record, "name"), RequiredString(record, "title"), Number(record, "amount"), RequiredString(record, "note", true), RequiredString(record, "rank", true), Compress(record));
                addDeduction.ExecuteNonQuery();
            }, cancellation);
            if (deductions != expectedDeductions) throw new InvalidDataException("عدد الاستقطاعات لا يطابق النسخة.");
            using var addStaff = Command(db, transaction, "INSERT INTO staff(name,original) VALUES ($name,$original)", "$name", "$original");
            var staff = await ReadRecords(entries, "staff.jsonl", record => { Set(addStaff, RequiredString(record, "name"), Compress(record)); addStaff.ExecuteNonQuery(); }, cancellation);
            if (staff != expectedStaff) throw new InvalidDataException("عدد سجلات الملاك لا يطابق النسخة.");
            var accounts = 0;
            if (version >= 2)
            {
                using var addAccount = Command(db, transaction, "INSERT INTO accounts(entity,original) VALUES ($entity,$original)", "$entity", "$original");
                accounts = await ReadRecords(entries, "accounts.jsonl", record => { Set(addAccount, RequiredString(record, "entity"), Compress(record)); addAccount.ExecuteNonQuery(); }, cancellation);
            }
            if (accounts != expectedAccounts) throw new InvalidDataException("عدد الحسابات لا يطابق النسخة.");
            using (var finish = db.CreateCommand())
            {
                finish.Transaction = transaction;
                finish.CommandText = "CREATE INDEX ix_deductions_month_title ON deductions(month,title); CREATE INDEX ix_employees_month ON employees(month); INSERT INTO meta VALUES ('sourceVersion',$version);";
                finish.Parameters.AddWithValue("$version", version.ToString());
                finish.ExecuteNonQuery();
            }
            cancellation.ThrowIfCancellationRequested();
            transaction.Commit();
            using var count = db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM employees";
            var keptEmployees = Convert.ToInt32(count.ExecuteScalar());
            await db.CloseAsync();
            cancellation.ThrowIfCancellationRequested();
            zip.Dispose();
            System.IO.Directory.Move(staging, destination);
            completed = true;
            progress?.Report("اكتمل استيراد النسخة في مساحة محلية جديدة.");
            return new ImportSummary(destination, version, keptEmployees, deductions, staff, accounts, expectedBooks, keep.OrderByDescending(MonthOrder.SortKey).ToArray(), employees - keptEmployees);
        }
        finally
        {
            if (!completed && System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
        }
    }

    private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive zip)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (name.Length == 0 || name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(p => p is "." or "..") || name.Contains(':'))
                throw new InvalidDataException("تحتوي النسخة مساراً غير صالح.");
            if (!entries.TryAdd(name, entry)) throw new InvalidDataException("تحتوي النسخة ملفات مكررة.");
            if (entry.Length < 0 || entry.Length > MaxEntryBytes) throw new InvalidDataException("حجم أحد ملفات النسخة يتجاوز الحد المدعوم.");
        }
        return entries;
    }

    private static ZipArchiveEntry Entry(Dictionary<string, ZipArchiveEntry> entries, string name)
    {
        if (!entries.TryGetValue(name, out var entry) || !string.Equals(entry.FullName, name, StringComparison.Ordinal) || entry.FullName.EndsWith('/'))
            throw new InvalidDataException($"النسخة لا تحتوي الملف المطلوب: {name}");
        return entry;
    }

    private static async Task<JsonDocument> ReadSmallJson(Dictionary<string, ZipArchiveEntry> entries, string name, long limit, CancellationToken cancellation)
    {
        var entry = Entry(entries, name);
        if (entry.Length > limit) throw new InvalidDataException($"حجم {name} يتجاوز الحد المدعوم.");
        await using var stream = entry.Open();
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
    }

    private static async Task<int> ReadRecords(Dictionary<string, ZipArchiveEntry> entries, string name, Action<JsonElement> consume, CancellationToken cancellation)
    {
        var entry = Entry(entries, name);
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, StrictUtf8, true, 32768, false);
        var buffer = new char[32768];
        var line = new StringBuilder();
        var count = 0;
        void ConsumeLine()
        {
            if (line.Length == 0) return;
            if (line[^1] == '\r') line.Length--;
            if (line.Length == 0) return;
            using var doc = JsonDocument.Parse(line.ToString());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"سجل غير صالح في {name}.");
            consume(doc.RootElement);
            checked { count++; }
            line.Clear();
        }
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellation);
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n') { ConsumeLine(); line.Clear(); }
                else { line.Append(buffer[i]); if (line.Length > MaxLineCharacters) throw new InvalidDataException($"سجل كبير جداً في {name}."); }
            }
        }
        ConsumeLine();
        return count;
    }

    private static async Task ValidateBooks(Dictionary<string, ZipArchiveEntry> entries, int version, int expected, CancellationToken cancellation)
    {
        if (version < 3) return;
        using var index = await ReadSmallJson(entries, "books/index.json", 10_000_000, cancellation);
        if (index.RootElement.ValueKind != JsonValueKind.Array || index.RootElement.GetArrayLength() != expected) throw new InvalidDataException("فهرس الكتب لا يطابق النسخة.");
        var ids = new HashSet<Guid>();
        foreach (var book in index.RootElement.EnumerateArray())
        {
            var id = Guid.Parse(RequiredString(book, "id"));
            if (!ids.Add(id) || RequiredInt(book, "pages") is < 1 or > 3) throw new InvalidDataException("بيانات أحد الكتب غير صالحة.");
            _ = RequiredString(book, "title");
            _ = RequiredString(book, "month");
            var pdf = Entry(entries, "books/" + id.ToString().ToUpperInvariant() + ".pdf");
            if (pdf.Length is < 6 or >= 60_000_000) throw new InvalidDataException("حجم أحد الكتب غير صالح.");
            await using var stream = pdf.Open();
            var header = new byte[5];
            await stream.ReadExactlyAsync(header, cancellation);
            if (!header.AsSpan().SequenceEqual("%PDF-"u8)) throw new InvalidDataException("أحد ملفات الكتب ليس PDF صالحاً.");
            await stream.CopyToAsync(Stream.Null, cancellation);
        }
    }

    private static string RequiredString(JsonElement value, string name, bool allowEmpty = false)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) throw new InvalidDataException($"الحقل {name} غير صالح.");
        var text = property.GetString()!;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text)) throw new InvalidDataException($"الحقل {name} فارغ.");
        return text;
    }

    private static int RequiredInt(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || !property.TryGetInt32(out var number)) throw new InvalidDataException($"الحقل {name} غير صالح.");
        return number;
    }

    private static string? Number(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDecimal(out _)) throw new InvalidDataException($"المبلغ {name} غير صالح.");
        return property.GetRawText();
    }

    private static byte[] Compress(JsonElement element)
    {
        var raw = Encoding.UTF8.GetBytes(element.GetRawText());
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, true)) brotli.Write(raw);
        return output.ToArray();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params string[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var name in parameters) command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        command.Prepare();
        return command;
    }
    private static void Set(SqliteCommand command, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++) command.Parameters[i].Value = values[i] ?? DBNull.Value;
    }
}

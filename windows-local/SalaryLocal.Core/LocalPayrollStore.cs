using System.Globalization;
using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

public sealed record StoreStatus(string DatasetId, string RevisionId, DateTimeOffset CreatedAt, long RevisionCount, long EmployeeRowCount, long HeaderSchemaCount, long DatabaseBytes);
public sealed record StoredMonth(string Name, int EmployeeCount, int DeductionCount, decimal NetTotal, string RevisionId);
public sealed record ImportResult(bool Changed, int Employees, int Deductions, string RevisionId);
public sealed record HistoryEntry(string RevisionId, string ParentRevisionId, string Month, string SourceName, DateTimeOffset CreatedAt, int Employees, int Deductions);

/// صفوف وأقسام ثابتة ببصماتها، مع مؤشرات منفصلة للأشهر الحالية.
/// معاملة كتابة فورية تمنع حفظ استيراد راجعه المستخدم قبل تغيّر البيانات.
public sealed partial class LocalPayrollStore : IDisposable
{
    private const int ApplicationId = 1396788300;
    private const int MaximumRowBytes = 16 * 1024 * 1024;
    private bool disposed;
    public string DatabasePath { get; }
    private static readonly string[] Schema = [
        "CREATE TABLE metadata (singleton INTEGER PRIMARY KEY CHECK(singleton=1), dataset TEXT NOT NULL, root TEXT NOT NULL, current TEXT NOT NULL, created TEXT NOT NULL) STRICT",
        "CREATE TABLE headers (id TEXT PRIMARY KEY, bytes BLOB NOT NULL) STRICT",
        "CREATE TABLE objects (id TEXT PRIMARY KEY, kind INTEGER NOT NULL CHECK(kind IN (0,1)), payload BLOB NOT NULL, codec INTEGER NOT NULL CHECK(codec IN (0,1)), raw_length INTEGER NOT NULL CHECK(raw_length>0 AND raw_length<=16777216), schema_id TEXT REFERENCES headers(id)) STRICT",
        "CREATE TABLE sections (id TEXT PRIMARY KEY, kind INTEGER NOT NULL CHECK(kind IN (0,1)), count INTEGER NOT NULL CHECK(count>0), net_total TEXT NOT NULL) STRICT",
        "CREATE TABLE section_rows (section_id TEXT NOT NULL REFERENCES sections(id), ordinal INTEGER NOT NULL CHECK(ordinal>=0), object_id TEXT NOT NULL REFERENCES objects(id), PRIMARY KEY(section_id,ordinal)) STRICT",
        "CREATE TABLE revisions (seq INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE, parent TEXT NOT NULL, month TEXT NOT NULL, source TEXT NOT NULL, source_hash TEXT NOT NULL, created TEXT NOT NULL, payroll TEXT REFERENCES sections(id), deductions TEXT REFERENCES sections(id)) STRICT",
        "CREATE TABLE months (name TEXT PRIMARY KEY, revision TEXT NOT NULL REFERENCES revisions(id)) STRICT"
    ];
    // تبقى البايتات غير المعروفة كاملة؛ الفصل يحفظ النص الأصلي والحقول الإضافية حرفياً.
    private sealed record Row(Employee? Employee, Deduction? Deduction, string? Schema, byte[]? Before, byte[]? Tail, byte[]? Raw);
    private sealed record Revision(string Parent, string Month, string Source, string SourceHash, string Created, string? Payroll, string? Deductions);
    private static byte[] Encode<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    // البصمة للنص الأصلي، والضغط للتخزين فقط. لا نقبل بيانات زائدة بعد نهاية الضغط.
    private static byte[] CompressRow(byte[] raw)
    {
        if (raw.Length is <= 0 or > MaximumRowBytes) throw Invalid();
        var output = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
        if (!BrotliEncoder.TryCompress(raw, output, out var written, quality: 4, window: 22)) throw Invalid();
        return output.AsSpan(0, written).ToArray();
    }
    private static byte[] DecompressRow(SqliteDataReader reader, int blobIndex, int lengthIndex, int compressedLengthIndex, int codecIndex)
    {
        long length = reader.GetInt64(lengthIndex), compressedLength = reader.GetInt64(compressedLengthIndex);
        if (length is <= 0 or > MaximumRowBytes || compressedLength <= 0 || compressedLength > BrotliEncoder.GetMaxCompressedLength((int)length)) throw Invalid();
        int codec = reader.GetInt32(codecIndex);
        if (codec == 0)
        {
            if (compressedLength != length) throw Invalid();
            return (byte[])reader[blobIndex];
        }
        if (codec != 1) throw Invalid();
        var compressed = (byte[])reader[blobIndex]; var raw = new byte[(int)length];
        using var decoder = new BrotliDecoder();
        var status = decoder.Decompress(compressed, raw, out int consumed, out int written);
        if (status != OperationStatus.Done || consumed != compressed.Length || written != raw.Length) throw Invalid();
        return raw;
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static InvalidDataException Invalid() => new("بيانات الرواتب المحلية غير صالحة؛ لم تُستبدل البيانات.");
    private static SqliteConnection Open(string path, bool readOnly = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 30 }.ToString());
        c.Open(); Execute(c, null, "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF;"); return c;
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? t, string sql, params object?[] args)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        return cmd;
    }
    private static void Execute(SqliteConnection c, SqliteTransaction? t, string sql, params object?[] args)
    { using var cmd = Command(c, t, sql, args); cmd.ExecuteNonQuery(); }
    private static object Scalar(SqliteConnection c, SqliteTransaction? t, string sql, params object?[] args)
    { using var cmd = Command(c, t, sql, args); return cmd.ExecuteScalar() ?? throw Invalid(); }
    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private SqliteConnection Connect() { ObjectDisposedException.ThrowIf(disposed, this); return Open(DatabasePath); }
    public LocalPayrollStore(string directory)
    {
        Directory.CreateDirectory(directory); DatabasePath = Path.GetFullPath(Path.Combine(directory, "payroll.db"));
        bool exists = File.Exists(DatabasePath);
        if (exists) { ValidateDatabase(DatabasePath); MigrateDatabase(DatabasePath); return; }
        using var c = Open(DatabasePath); using var t = c.BeginTransaction(deferred: false);
        // ربما أنشأت عملية أخرى الجداول أثناء فتح الملف.
        if ((long)Scalar(c, t, "SELECT count(*) FROM sqlite_master WHERE type='table'") == 0)
        {
            foreach (var sql in Schema.Concat(AuxiliarySchema)) Execute(c, t, sql);
            Execute(c, t, $"PRAGMA application_id={ApplicationId}; PRAGMA user_version=2;");
            string dataset = Guid.NewGuid().ToString("N"), root = Hash(Encode(Guid.NewGuid().ToString("N")));
            Execute(c, t, "INSERT INTO metadata VALUES(1,$0,$1,$1,$2)", dataset, root, DateTimeOffset.UtcNow.ToString("O"));
        }
        t.Commit();
    }
    public void Dispose() => disposed = true;
    private static StoreStatus Status(SqliteConnection c, SqliteTransaction? t, string path)
    {
        using var cmd = Command(c, t, "SELECT dataset,current,created,(SELECT count(*) FROM revisions),(SELECT count(*) FROM objects WHERE kind=0),(SELECT count(*) FROM headers) FROM metadata WHERE singleton=1");
        using var r = cmd.ExecuteReader(); if (!r.Read()) throw Invalid();
        return new(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), new FileInfo(path).Length);
    }
    public StoreStatus GetStatus() { using var c = Connect(); return Status(c, null, DatabasePath); }
    private static List<(string Name, string Revision, string? Payroll, string? Deductions)> MonthPointers(SqliteConnection c, SqliteTransaction? t)
    {
        using var cmd = Command(c, t, "SELECT m.name,m.revision,r.payroll,r.deductions FROM months m JOIN revisions r ON r.id=m.revision");
        using var r = cmd.ExecuteReader(); var result = new List<(string, string, string?, string?)>();
        while (r.Read()) result.Add((r.GetString(0), r.GetString(1), Text(r, 2), Text(r, 3))); return result;
    }
    private static IEnumerable<Row> ReadRows(SqliteConnection c, SqliteTransaction? t, string? section)
    {
        if (section is null) yield break;
        using var cmd = Command(c, t, "SELECT o.payload,h.bytes,o.raw_length,length(o.payload),o.codec FROM section_rows l JOIN objects o ON o.id=l.object_id LEFT JOIN headers h ON h.id=o.schema_id WHERE l.section_id=$0 ORDER BY l.ordinal", section);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = JsonSerializer.Deserialize<Row>(DecompressRow(r, 0, 2, 3, 4)) ?? throw Invalid();
            if (row.Schema is not null) row = row with { Raw = SharedPayrollColumns.Join(row.Schema, (byte[])r[1], row.Before!, row.Tail!) };
            yield return row;
        }
    }
    public BackupSnapshot GetSnapshot(params string[] months)
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: true);
        var status = Status(c, t, DatabasePath); var selected = months.ToHashSet(StringComparer.Ordinal);
        var emps = new List<Employee>(); var deds = new List<Deduction>(); var summaries = new List<PayrollMonth>();
        foreach (var m in MonthPointers(c, t).OrderByDescending(m => MonthNames.SortKey(m.Name)))
        {
            if (selected.Count != 0 && !selected.Contains(m.Name)) continue;
            var e = ReadRows(c, t, m.Payroll).Select(r => r.Employee! with { Month = m.Name, RawColumns = r.Raw }).ToArray();
            var d = ReadRows(c, t, m.Deductions).Select(r => r.Deduction! with { Month = m.Name }).ToArray();
            emps.AddRange(e); deds.AddRange(d); summaries.Add(new(m.Name, e.Length, d.Length));
        }
        var auxiliary = ReadAuxiliarySnapshot(c, t);
        t.Commit();
        return new(DatabasePath, status.RevisionId, auxiliary.Version, summaries, emps, deds, auxiliary.StaffCount, auxiliary.Accounts, auxiliary.Books, auxiliary.Settings) { CreatedAt = auxiliary.CreatedAt };
    }
    public IReadOnlyList<StoredMonth> GetMonths()
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: true); var result = new List<StoredMonth>();
        foreach (var m in MonthPointers(c, t).OrderByDescending(m => MonthNames.SortKey(m.Name)))
        {
            int employees = 0; decimal net = 0;
            if (m.Payroll is not null)
            {
                using var cmd = Command(c, t, "SELECT count,net_total FROM sections WHERE id=$0", m.Payroll);
                using var r = cmd.ExecuteReader(); if (!r.Read()) throw Invalid();
                employees = r.GetInt32(0); net = decimal.Parse(r.GetString(1), CultureInfo.InvariantCulture);
            }
            var d = m.Deductions is null ? 0 : Convert.ToInt32(Scalar(c, t, "SELECT count FROM sections WHERE id=$0", m.Deductions), CultureInfo.InvariantCulture);
            result.Add(new(m.Name, employees, d, net, m.Revision));
        }
        t.Commit(); return result;
    }
    private static void CheckEmployee(Employee e, string month, bool historical = false)
    {
        if (e.Month != month || string.IsNullOrWhiteSpace(e.Name) || e.Rank is null || e.Workplace is null || (historical ? e.Net is < -9_007_199_254_740_991m or > 9_007_199_254_740_991m : e.Net is null or < 0 or > 9_007_199_254_740_991m)) throw Invalid();
    }
    private static void CheckDeduction(Deduction d, string month, bool historical = false)
    {
        if (d.Month != month || string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.Title) || d.Rank is null || d.Note is null || (historical ? d.Amount is < -9_007_199_254_740_991m or > 9_007_199_254_740_991m : d.Amount is < 0 or > 9_007_199_254_740_991m || d.Amount is null && !PayrollAnalysis.IsInactive(d))) throw Invalid();
    }
    private static string SaveSection(SqliteConnection c, SqliteTransaction t, IReadOnlyList<Row> rows, int kind)
    {
        var ids = new List<string>();
        using var exists = Command(c, t, "SELECT 1 FROM objects WHERE id=$0", ""); exists.Prepare();
        using var insert = Command(c, t, "INSERT INTO objects VALUES($0,$1,$2,1,$3,$4)", "", kind, Array.Empty<byte>(), 0, null); insert.Prepare();
        foreach (var row in rows)
        {
            var payload = Encode(row); if (payload.Length is <= 0 or > MaximumRowBytes) throw Invalid();
            string id = Hash(payload); ids.Add(id);
            exists.Parameters[0].Value = id; if (exists.ExecuteScalar() is not null) continue;
            insert.Parameters[0].Value = id; insert.Parameters[2].Value = CompressRow(payload);
            insert.Parameters[3].Value = payload.Length; insert.Parameters[4].Value = (object?)row.Schema ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
        string section = Hash(Encode(new { Kind = kind, Rows = ids }));
        if ((long)Scalar(c, t, "SELECT count(*) FROM sections WHERE id=$0", section) == 0)
        {
            Execute(c, t, "INSERT INTO sections VALUES($0,$1,$2,$3)", section, kind, rows.Count, rows.Sum(r => r.Employee?.Net ?? 0).ToString(CultureInfo.InvariantCulture));
            using var cmd = Command(c, t, "INSERT INTO section_rows VALUES($0,$1,$2)", section, 0, ""); cmd.Prepare();
            for (int i = 0; i < ids.Count; i++) { cmd.Parameters[1].Value = i; cmd.Parameters[2].Value = ids[i]; cmd.ExecuteNonQuery(); }
        }
        return section;
    }
    public ImportResult Import(PayrollImportPreview preview, string sourceName, string sourceSha256, string expectedRevision, bool replaceExisting)
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false);
        var result = ImportCore(c, t, preview, sourceName, sourceSha256, expectedRevision, replaceExisting);
        t.Commit(); return result;
    }
    private static ImportResult ImportCore(SqliteConnection c, SqliteTransaction t, PayrollImportPreview preview, string sourceName, string sourceSha256, string expectedRevision, bool replaceExisting, bool historical = false)
    {
        if (preview is null || preview.Problems is null || preview.Problems.Count != 0 || preview.Employees is null || preview.Deductions is null || (historical ? string.IsNullOrWhiteSpace(preview.Month) || preview.Month.Length > 200 : MonthNames.SortKey(preview.Month) == 0) || !historical && preview.Employees.Count + preview.Deductions.Count == 0 || preview.Employees.Count > 200_000 || preview.Deductions.Count > 500_000 || string.IsNullOrWhiteSpace(sourceName) || !IsHash(sourceSha256?.ToUpperInvariant())) throw Invalid();
        foreach (var e in preview.Employees) { if (e is null) throw Invalid(); CheckEmployee(e, preview.Month, historical); }
        foreach (var d in preview.Deductions) { if (d is null) throw Invalid(); CheckDeduction(d, preview.Month, historical); }
        string current = (string)Scalar(c, t, "SELECT current FROM metadata");
        if (current != expectedRevision) throw new InvalidDataException("تغيّرت البيانات منذ المراجعة؛ أعد مراجعة الاستيراد.");
        var pointers = MonthPointers(c, t);
        if (!historical && pointers.Any(m => m.Name != preview.Month && MonthNames.SortKey(m.Name) == MonthNames.SortKey(preview.Month))) throw Invalid();
        var old = pointers.SingleOrDefault(m => m.Name == preview.Month);
        string? payroll = old.Payroll, deductions = old.Deductions;
        if (preview.Employees.Count > 0)
        {
            var rows = new List<Row>();
            foreach (var e in preview.Employees)
            {
                var split = SharedPayrollColumns.Split(e.RawColumns);
                if (split is not null) Execute(c, t, "INSERT OR IGNORE INTO headers VALUES($0,$1)", split.SchemaId, split.Prefix);
                rows.Add(new(e with { Month = "", RawColumns = null }, null, split?.SchemaId, split?.Before, split?.Tail, split is null ? e.RawColumns : null));
            }
            payroll = SaveSection(c, t, rows, 0);
        }
        if (preview.Deductions.Count > 0) deductions = SaveSection(c, t, preview.Deductions.Select(d => new Row(null, d with { Month = "" }, null, null, null, null)).ToArray(), 1);
        if (old.Name is not null && old.Payroll == payroll && old.Deductions == deductions) return new(false, preview.Employees.Count, preview.Deductions.Count, current); // التراجع يشمل أي كائنات غير مستخدمة
        if (!replaceExisting && (preview.Employees.Count > 0 && old.Payroll is not null || preview.Deductions.Count > 0 && old.Deductions is not null)) throw new InvalidDataException("هذا القسم محفوظ للشهر؛ يلزم تأكيد الاستبدال بعد المراجعة.");
        var revision = new Revision(current, preview.Month, sourceName, sourceSha256!.ToUpperInvariant(), DateTimeOffset.UtcNow.ToString("O"), payroll, deductions);
        string id = Hash(Encode(revision));
        Execute(c, t, "INSERT INTO revisions(id,parent,month,source,source_hash,created,payroll,deductions) VALUES($0,$1,$2,$3,$4,$5,$6,$7)", id, revision.Parent, revision.Month, revision.Source, revision.SourceHash, revision.Created, payroll, deductions);
        Execute(c, t, "INSERT INTO months VALUES($0,$1) ON CONFLICT(name) DO UPDATE SET revision=excluded.revision", preview.Month, id);
        Execute(c, t, "UPDATE metadata SET current=$0 WHERE singleton=1", id);
        return new(true, preview.Employees.Count, preview.Deductions.Count, id);
    }
    public IReadOnlyList<HistoryEntry> GetHistory()
    {
        using var c = Connect(); using var cmd = Command(c, null, "SELECT r.id,r.parent,r.month,r.source,r.created,COALESCE(p.count,0),COALESCE(d.count,0) FROM revisions r LEFT JOIN sections p ON p.id=r.payroll LEFT JOIN sections d ON d.id=r.deductions ORDER BY r.seq DESC");
        using var r = cmd.ExecuteReader(); var result = new List<HistoryEntry>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture), r.GetInt32(5), r.GetInt32(6))); return result;
    }
    public void CreateDatabaseSnapshot(string destination)
    {
        using var c = Connect(); bool created = false;
        try
        {
            using (var reservation = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { reservation.Flush(true); created = true; }
            using (var target = Open(destination)) c.BackupDatabase(target);
            TranscodeDatabase(destination, 0);
        }
        catch { if (created) File.Delete(destination); throw; }
    }
    /// تُستدعى على نسخة الاسترجاع المؤقتة فقط، قبل اعتماد مجلدها الجديد؛ لا على ملف المستخدم الأصلي.
    public static void PrepareRestoredDatabase(string path) => TranscodeDatabase(path, 1);

    // النص غير المضغوط داخل النسخة يسمح لضغط ZIP باستغلال التشابه بين الصفوف.
    // التحويل وVACUUM يحتاجان مساحة مؤقتة؛ لا يتغير أصل البيانات أو سجل المراجعات.
    private static void TranscodeDatabase(string path, int targetCodec)
    {
        var before = ValidateDatabase(path);
        using (var c = Open(path))
        {
            using (var t = c.BeginTransaction(deferred: false))
            {
                var ids = new List<string>();
                using (var query = Command(c, t, "SELECT id FROM objects WHERE codec<>$0", targetCodec))
                using (var r = query.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
                using var select = Command(c, t, "SELECT payload,raw_length,length(payload),codec FROM objects WHERE id=$0", "");
                using var update = Command(c, t, "UPDATE objects SET payload=$0,codec=$1 WHERE id=$2", Array.Empty<byte>(), targetCodec, "");
                select.Prepare(); update.Prepare();
                foreach (var id in ids)
                {
                    select.Parameters[0].Value = id;
                    byte[] raw;
                    using (var r = select.ExecuteReader()) { if (!r.Read()) throw Invalid(); raw = DecompressRow(r, 0, 1, 2, 3); }
                    if (Hash(raw) != id) throw Invalid();
                    update.Parameters[0].Value = targetCodec == 0 ? raw : CompressRow(raw);
                    update.Parameters[2].Value = id; update.ExecuteNonQuery();
                }
                if ((long)Scalar(c, t, "PRAGMA user_version") == 2) TranscodeAuxiliary(c, t, targetCodec);
                t.Commit();
            }
            Execute(c, null, "VACUUM");
        }
        var after = ValidateDatabase(path);
        if (before with { DatabaseBytes = after.DatabaseBytes } != after) throw Invalid();
    }
    /// فحص للقراءة فقط؛ يرفض البنية غير المعروفة أو سلسلة المراجعات غير السليمة.
    public static StoreStatus ValidateDatabase(string path)
    {
        try
        {
            using var c = Open(path, readOnly: true); using var t = c.BeginTransaction(deferred: true);
            long version = (long)Scalar(c, t, "PRAGMA user_version");
            if (Convert.ToInt32(Scalar(c, t, "PRAGMA application_id"), CultureInfo.InvariantCulture) != ApplicationId || version is not (1 or 2)) throw Invalid();
            using (var cmd = Command(c, t, "PRAGMA integrity_check"))
            using (var r = cmd.ExecuteReader()) { if (!r.Read() || r.GetString(0) != "ok" || r.Read()) throw Invalid(); }
            using (var cmd = Command(c, t, "PRAGMA foreign_key_check"))
            using (var r = cmd.ExecuteReader()) if (r.Read()) throw Invalid();
            var schemas = new HashSet<string>(version == 1 ? Schema : Schema.Concat(AuxiliarySchema), StringComparer.Ordinal);
            using (var cmd = Command(c, t, "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) if (!schemas.Remove(r.GetString(0))) throw Invalid();
            if (schemas.Count != 0 || (long)Scalar(c, t, "SELECT count(*) FROM metadata") != 1) throw Invalid();
            string root;
            using (var cmd = Command(c, t, "SELECT singleton,dataset,root,current,created FROM metadata"))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read() || r.GetInt32(0) != 1 || !Guid.TryParseExact(r.GetString(1), "N", out _) || !IsHash(r.GetString(2)) || !IsHash(r.GetString(3)) || !DateTimeOffset.TryParseExact(r.GetString(4), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw Invalid();
                root = r.GetString(2);
            }
            var headers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var cmd = Command(c, t, "SELECT id,bytes FROM headers"))
            using (var r = cmd.ExecuteReader()) while (r.Read())
            {
                var bytes = (byte[])r[1]; using var doc = JsonDocument.Parse(bytes);
                if (Hash(bytes) != r.GetString(0) || doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) throw Invalid();
                headers.Add(r.GetString(0), bytes);
            }
            var objects = new Dictionary<string, (int Kind, decimal Net)>(StringComparer.Ordinal); var usedHeaders = new HashSet<string>(StringComparer.Ordinal);
            using (var cmd = Command(c, t, "SELECT id,kind,payload,schema_id,raw_length,length(payload),codec FROM objects"))
            using (var r = cmd.ExecuteReader()) while (r.Read())
            {
                var payload = DecompressRow(r, 2, 4, 5, 6); var row = JsonSerializer.Deserialize<Row>(payload) ?? throw Invalid(); int kind = r.GetInt32(1);
                if (Hash(payload) != r.GetString(0) || row.Schema != Text(r, 3)) throw Invalid();
                if (kind == 0)
                {
                    if (row.Employee is null || row.Deduction is not null || row.Employee.RawColumns is not null) throw Invalid(); CheckEmployee(row.Employee, "", historical: version == 2);
                    if (row.Schema is null) { if (row.Before is not null || row.Tail is not null) throw Invalid(); }
                    else
                    {
                        if (row.Raw is not null || row.Before is null || row.Tail is null || !headers.TryGetValue(row.Schema, out var header)) throw Invalid();
                        var raw = SharedPayrollColumns.Join(row.Schema, header, row.Before, row.Tail);
                        var parts = SharedPayrollColumns.Split(raw);
                        if (parts is null || parts.SchemaId != row.Schema || !parts.Before.SequenceEqual(row.Before) || !parts.Tail.SequenceEqual(row.Tail)) throw Invalid();
                        usedHeaders.Add(row.Schema);
                    }
                }
                else if (kind == 1)
                {
                    if (row.Deduction is null || row.Employee is not null || row.Schema is not null || row.Before is not null || row.Tail is not null || row.Raw is not null) throw Invalid(); CheckDeduction(row.Deduction, "", historical: version == 2);
                }
                else throw Invalid();
                objects.Add(r.GetString(0), (kind, row.Employee?.Net ?? 0));
            }
            if (usedHeaders.Count != headers.Count) throw Invalid();
            var sections = new Dictionary<string, (int Kind, int Count, decimal Total)>(StringComparer.Ordinal);
            using (var cmd = Command(c, t, "SELECT id,kind,count,net_total FROM sections"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) sections.Add(r.GetString(0), (r.GetInt32(1), r.GetInt32(2), decimal.Parse(r.GetString(3), CultureInfo.InvariantCulture)));
            var usedObjects = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, section) in sections)
            {
                var ids = new List<string>(); decimal total = 0;
                using var cmd = Command(c, t, "SELECT ordinal,object_id FROM section_rows WHERE section_id=$0 ORDER BY ordinal", id); using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string objectId = r.GetString(1);
                    if (r.GetInt32(0) != ids.Count || !objects.TryGetValue(objectId, out var item) || item.Kind != section.Kind) throw Invalid();
                    ids.Add(objectId); usedObjects.Add(objectId); total += item.Net;
                }
                if (total != section.Total || section.Count <= 0 || ids.Count != section.Count || id != Hash(Encode(new { Kind = section.Kind, Rows = ids }))) throw Invalid();
            }
            if (usedObjects.Count != objects.Count) throw Invalid();
            var auxiliaryRevisions = version == 2 ? ValidateAuxiliary(c, t) : new Dictionary<string, byte[]>();
            var seenAuxiliary = new HashSet<string>(StringComparer.Ordinal);
            var latest = new Dictionary<string, string>(StringComparer.Ordinal);
            var priorSections = new Dictionary<string, (string? Payroll, string? Deductions)>(StringComparer.Ordinal); var monthKeys = new Dictionary<int, string>();
            var usedSections = new HashSet<string>(StringComparer.Ordinal); string parent = root; long seq = 0;
            using (var cmd = Command(c, t, "SELECT seq,id,parent,month,source,source_hash,created,payroll,deductions FROM revisions ORDER BY seq"))
            using (var r = cmd.ExecuteReader()) while (r.Read())
            {
                var rev = new Revision(r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), Text(r, 7), Text(r, 8));
                int key = MonthNames.SortKey(rev.Month);
                string revisionId = r.GetString(1);
                if (r.GetInt64(0) != ++seq || rev.Parent != parent || string.IsNullOrWhiteSpace(rev.Source) || !IsHash(rev.SourceHash) || !DateTimeOffset.TryParseExact(rev.Created, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw Invalid();
                if (rev.Month.Length == 0)
                {
                    if (rev.Payroll is not null || rev.Deductions is not null || !auxiliaryRevisions.TryGetValue(revisionId, out var state) || Hash(Encode(new AuxiliaryRevision(rev, state))) != revisionId) throw Invalid();
                    seenAuxiliary.Add(revisionId); parent = revisionId; continue;
                }
                if ((version == 1 ? key == 0 : string.IsNullOrWhiteSpace(rev.Month) || rev.Month.Length > 200) || Hash(Encode(rev)) != revisionId || version == 1 && rev.Payroll is null && rev.Deductions is null || auxiliaryRevisions.ContainsKey(revisionId)) throw Invalid();
                if (version == 1 && monthKeys.TryGetValue(key, out var existingName) && existingName != rev.Month) throw Invalid(); monthKeys[key] = rev.Month;
                void CheckSection(string? id, int kind) { if (id is null) return; if (!sections.TryGetValue(id, out var s) || s.Kind != kind) throw Invalid(); usedSections.Add(id); }
                CheckSection(rev.Payroll, 0); CheckSection(rev.Deductions, 1);
                if (priorSections.TryGetValue(rev.Month, out var previous) &&
                    (previous.Payroll is not null && rev.Payroll is null || previous.Deductions is not null && rev.Deductions is null ||
                     previous.Payroll == rev.Payroll && previous.Deductions == rev.Deductions)) throw Invalid();
                priorSections[rev.Month] = (rev.Payroll, rev.Deductions);
                parent = r.GetString(1); latest[rev.Month] = parent;
            }
            if (seenAuxiliary.Count != auxiliaryRevisions.Count || usedSections.Count != sections.Count || (string)Scalar(c, t, "SELECT current FROM metadata") != parent) throw Invalid();
            var pointers = MonthPointers(c, t);
            if (pointers.Count != latest.Count || pointers.Any(m => !latest.TryGetValue(m.Name, out var id) || id != m.Revision)) throw Invalid();
            // اسم الشهر يجب أن يطابق المراجعة المشار إليها.
            if ((long)Scalar(c, t, "SELECT count(*) FROM months m JOIN revisions r ON r.id=m.revision WHERE m.name<>r.month") != 0) throw Invalid();
            var result = Status(c, t, path); t.Commit(); return result;
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or FormatException or OverflowException or ArgumentException or KeyNotFoundException or NullReferenceException)
        { throw new InvalidDataException("تعذّر التحقق من قاعدة النسخة الاحتياطية؛ لم تُسترجع البيانات.", ex); }
    }
}

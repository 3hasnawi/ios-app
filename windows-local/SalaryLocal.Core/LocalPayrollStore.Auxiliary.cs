using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Claunia.PropertyList;
using SalaryDesktop.Core;

namespace SalaryLocal.Core;

public sealed record MutationResult(bool Changed, string RevisionId);

public sealed partial class LocalPayrollStore
{
    private const int ChunkBytes = 1024 * 1024;
    private const int MaximumAuxiliaryValueBytes = 128 * 1024 * 1024;
    private static readonly string[] Areas = ["accounts", "attachments", "books", "meta", "settings", "staff"];
    private static readonly string[] AuxiliarySchema = [
        "CREATE TABLE auxiliary_chunks (id TEXT PRIMARY KEY, payload BLOB NOT NULL, codec INTEGER NOT NULL CHECK(codec IN (0,1)), raw_length INTEGER NOT NULL CHECK(raw_length>0 AND raw_length<=1048576)) STRICT",
        "CREATE TABLE auxiliary_values (id TEXT PRIMARY KEY, length INTEGER NOT NULL CHECK(length>=0 AND length<=134217728), chunks BLOB NOT NULL) STRICT",
        "CREATE TABLE auxiliary_sets (id TEXT PRIMARY KEY, area TEXT NOT NULL, count INTEGER NOT NULL CHECK(count>0)) STRICT",
        "CREATE TABLE auxiliary_entries (set_id TEXT NOT NULL REFERENCES auxiliary_sets(id), key TEXT NOT NULL, value_id TEXT NOT NULL REFERENCES auxiliary_values(id), PRIMARY KEY(set_id,key)) STRICT",
        "CREATE TABLE auxiliary_revisions (revision TEXT PRIMARY KEY REFERENCES revisions(id), state BLOB NOT NULL) STRICT"
    ];
    private sealed record AuxiliaryRevision(Revision Revision, byte[] State);
    private sealed record SetEntry(string Key, string Value);
    private sealed record AuxiliarySnapshot(int Version, double? CreatedAt, int StaffCount, IReadOnlyList<ExecutionAccount> Accounts, IReadOnlyList<ArchiveBook> Books, IReadOnlyDictionary<string, byte[]> Settings);
    private static SortedDictionary<string, string> EmptyMap() => new(StringComparer.Ordinal);
    private static string Ordinal(int index) => index.ToString("D10", CultureInfo.InvariantCulture);
    private static void CheckSource(string sourceName, string sourceHash)
    { if (string.IsNullOrWhiteSpace(sourceName) || !IsHash(sourceHash?.ToUpperInvariant())) throw Invalid(); }
    private static string CheckCurrent(SqliteConnection c, SqliteTransaction t, string expected)
    {
        string current = (string)Scalar(c, t, "SELECT current FROM metadata");
        if (current != expected) throw new InvalidDataException("تغيّرت البيانات منذ المراجعة؛ أعد مراجعة العملية.");
        return current;
    }
    // الترحيل إضافي فقط: تبقى جميع صفوف الإصدار الأول وبصماته ومراجعاته كما هي.
    private static void MigrateDatabase(string path)
    {
        using var c = Open(path); using var t = c.BeginTransaction(deferred: false);
        long version = (long)Scalar(c, t, "PRAGMA user_version");
        if (version == 1)
        {
            foreach (var sql in AuxiliarySchema) Execute(c, t, sql);
            Execute(c, t, "PRAGMA user_version=2");
        }
        else if (version != 2) throw Invalid();
        t.Commit();
    }
    private static SortedDictionary<string, string> CurrentAuxiliaryState(SqliteConnection c, SqliteTransaction? t)
    {
        using var cmd = Command(c, t, "SELECT a.state FROM auxiliary_revisions a JOIN revisions r ON r.id=a.revision ORDER BY r.seq DESC LIMIT 1");
        var bytes = cmd.ExecuteScalar() as byte[];
        return bytes is null ? EmptyMap() : ParseState(bytes);
    }
    private static SortedDictionary<string, string> ParseState(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
        var result = EmptyMap();
        foreach (var property in document.RootElement.EnumerateObject())
            if (!Areas.Contains(property.Name, StringComparer.Ordinal) || property.Value.ValueKind != JsonValueKind.String || !IsHash(property.Value.GetString()) || !result.TryAdd(property.Name, property.Value.GetString()!)) throw Invalid();
        if (!Encode(result).SequenceEqual(bytes)) throw Invalid();
        return result;
    }
    private static SortedDictionary<string, string> ReadSet(SqliteConnection c, SqliteTransaction? t, string? id)
    {
        var entries = EmptyMap(); if (id is null) return entries;
        using var cmd = Command(c, t, "SELECT key,value_id FROM auxiliary_entries WHERE set_id=$0 ORDER BY key COLLATE BINARY", id);
        using var r = cmd.ExecuteReader(); while (r.Read()) entries.Add(r.GetString(0), r.GetString(1)); return entries;
    }
    private static string SaveValue(SqliteConnection c, SqliteTransaction t, byte[] bytes)
    {
        if (bytes.Length > MaximumAuxiliaryValueBytes) throw Invalid();
        string id = Hash(bytes);
        if ((long)Scalar(c, t, "SELECT count(*) FROM auxiliary_values WHERE id=$0", id) != 0) return id;
        var chunks = new List<string>();
        for (int i = 0; i < bytes.Length; i += ChunkBytes)
        {
            var part = bytes.AsSpan(i, Math.Min(ChunkBytes, bytes.Length - i)).ToArray(); string chunk = Hash(part); chunks.Add(chunk);
            if ((long)Scalar(c, t, "SELECT count(*) FROM auxiliary_chunks WHERE id=$0", chunk) == 0)
                Execute(c, t, "INSERT INTO auxiliary_chunks VALUES($0,$1,1,$2)", chunk, CompressRow(part), part.Length);
        }
        Execute(c, t, "INSERT INTO auxiliary_values VALUES($0,$1,$2)", id, bytes.Length, Encode(chunks)); return id;
    }
    private static (int Length, string[] Chunks) ValueParts(SqliteConnection c, SqliteTransaction? t, string id)
    {
        using var cmd = Command(c, t, "SELECT length,chunks FROM auxiliary_values WHERE id=$0", id); using var r = cmd.ExecuteReader();
        if (!r.Read()) throw Invalid(); long length = r.GetInt64(0);
        if (length is < 0 or > MaximumAuxiliaryValueBytes || ((byte[])r[1]).Length > 64 * 1024) throw Invalid();
        var chunks = JsonSerializer.Deserialize<string[]>((byte[])r[1]) ?? throw Invalid();
        if (chunks.Length != (length + ChunkBytes - 1) / ChunkBytes || chunks.Any(h => !IsHash(h))) throw Invalid();
        return ((int)length, chunks);
    }
    private static byte[] ReadChunk(SqliteConnection c, SqliteTransaction? t, string id)
    {
        using var cmd = Command(c, t, "SELECT payload,raw_length,length(payload),codec FROM auxiliary_chunks WHERE id=$0", id); using var r = cmd.ExecuteReader();
        if (!r.Read() || r.GetInt64(1) > ChunkBytes) throw Invalid();
        var raw = DecompressRow(r, 0, 1, 2, 3); if (Hash(raw) != id) throw Invalid(); return raw;
    }
    private static byte[] ReadValue(SqliteConnection c, SqliteTransaction? t, string id)
    {
        var parts = ValueParts(c, t, id); var bytes = new byte[parts.Length]; int offset = 0;
        foreach (var chunk in parts.Chunks)
        {
            var raw = ReadChunk(c, t, chunk);
            if (raw.Length != Math.Min(ChunkBytes, parts.Length - offset)) throw Invalid();
            raw.CopyTo(bytes, offset); offset += raw.Length;
        }
        if (offset != bytes.Length || Hash(bytes) != id) throw Invalid(); return bytes;
    }
    private static string? SaveSet(SqliteConnection c, SqliteTransaction t, string area, SortedDictionary<string, string> entries)
    {
        if (entries.Count == 0) return null;
        var content = entries.Select(p => new SetEntry(p.Key, p.Value)).ToArray();
        string id = Hash(Encode(new { Area = area, Entries = content }));
        if ((long)Scalar(c, t, "SELECT count(*) FROM auxiliary_sets WHERE id=$0", id) != 0) return id;
        Execute(c, t, "INSERT INTO auxiliary_sets VALUES($0,$1,$2)", id, area, entries.Count);
        using var cmd = Command(c, t, "INSERT INTO auxiliary_entries VALUES($0,$1,$2)", id, "", ""); cmd.Prepare();
        foreach (var entry in content) { cmd.Parameters[1].Value = entry.Key; cmd.Parameters[2].Value = entry.Value; cmd.ExecuteNonQuery(); }
        return id;
    }
    private static void SetArea(SqliteConnection c, SqliteTransaction t, SortedDictionary<string, string> state, string area, SortedDictionary<string, string> entries)
    { var id = SaveSet(c, t, area, entries); if (id is null) state.Remove(area); else state[area] = id; }
    private static MutationResult SaveAuxiliaryRevision(SqliteConnection c, SqliteTransaction t, SortedDictionary<string, string> state, string sourceName, string sourceHash, string expected)
    {
        CheckSource(sourceName, sourceHash); string current = CheckCurrent(c, t, expected);
        var bytes = Encode(state);
        if (bytes.SequenceEqual(Encode(CurrentAuxiliaryState(c, t)))) return new(false, current);
        var rev = new Revision(current, "", sourceName, sourceHash.ToUpperInvariant(), DateTimeOffset.UtcNow.ToString("O"), null, null);
        string id = Hash(Encode(new AuxiliaryRevision(rev, bytes)));
        Execute(c, t, "INSERT INTO revisions(id,parent,month,source,source_hash,created,payroll,deductions) VALUES($0,$1,'',$2,$3,$4,NULL,NULL)", id, current, sourceName, rev.SourceHash, rev.Created);
        Execute(c, t, "INSERT INTO auxiliary_revisions VALUES($0,$1)", id, bytes);
        Execute(c, t, "UPDATE metadata SET current=$0 WHERE singleton=1", id); return new(true, id);
    }
    private static IReadOnlyList<T> ReadList<T>(SqliteConnection c, SqliteTransaction? t, SortedDictionary<string, string> state, string area)
    {
        var result = new List<T>();
        foreach (var entry in ReadSet(c, t, state.GetValueOrDefault(area)))
        {
            if (entry.Key != Ordinal(result.Count)) throw Invalid();
            result.Add(JsonSerializer.Deserialize<T>(ReadValue(c, t, entry.Value)) ?? throw Invalid());
        }
        return result;
    }
    private static void SaveList<T>(SqliteConnection c, SqliteTransaction t, SortedDictionary<string, string> state, string area, IReadOnlyList<T> values)
    {
        var entries = EmptyMap(); for (int i = 0; i < values.Count; i++) entries.Add(Ordinal(i), SaveValue(c, t, Encode(values[i])));
        SetArea(c, t, state, area, entries);
    }
    private static AuxiliarySnapshot ReadAuxiliarySnapshot(SqliteConnection c, SqliteTransaction? t)
    {
        var state = CurrentAuxiliaryState(c, t); var settings = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in ReadSet(c, t, state.GetValueOrDefault("settings"))) settings.Add(entry.Key, ReadValue(c, t, entry.Value));
        var meta = ReadSet(c, t, state.GetValueOrDefault("meta"));
        int version = meta.TryGetValue("sourceVersion", out var versionId) ? JsonSerializer.Deserialize<int>(ReadValue(c, t, versionId)) : 5;
        double? createdAt = meta.TryGetValue("createdAt", out var dateId) ? JsonSerializer.Deserialize<double?>(ReadValue(c, t, dateId)) : null;
        int staffCount = state.TryGetValue("staff", out var staffSet) ? Convert.ToInt32(Scalar(c, t, "SELECT count FROM auxiliary_sets WHERE id=$0", staffSet), CultureInfo.InvariantCulture) : 0;
        return new(version, createdAt, staffCount, ReadList<ExecutionAccount>(c, t, state, "accounts"), ReadList<ArchiveBook>(c, t, state, "books"), settings);
    }
    public IReadOnlyList<StaffMember> GetStaff()
    { using var c = Connect(); using var t = c.BeginTransaction(deferred: true); var result = ReadList<StaffMember>(c, t, CurrentAuxiliaryState(c, t), "staff"); t.Commit(); return result; }
    public IReadOnlyList<string> GetAttachments()
    { using var c = Connect(); using var t = c.BeginTransaction(deferred: true); var result = ReadSet(c, t, CurrentAuxiliaryState(c, t).GetValueOrDefault("attachments")).Keys.ToArray(); t.Commit(); return result; }
    public byte[] ReadAttachment(string key)
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: true);
        var entries = ReadSet(c, t, CurrentAuxiliaryState(c, t).GetValueOrDefault("attachments"));
        if (!entries.TryGetValue(key, out var value)) throw new FileNotFoundException("المرفق غير موجود في مساحة العمل.");
        var bytes = ReadValue(c, t, value); t.Commit(); return bytes;
    }
    public void CopyAttachment(string key, Stream destination)
    { ArgumentNullException.ThrowIfNull(destination); destination.Write(ReadAttachment(key)); }
    public bool HasData()
    {
        using var c = Connect(); using var t = c.BeginTransaction(deferred: true);
        bool result = (long)Scalar(c, t, "SELECT count(*) FROM months") != 0 || CurrentAuxiliaryState(c, t).Keys.Any(area => area != "meta"); t.Commit(); return result;
    }
    private static void ValidateSettingUpdates(IReadOnlyDictionary<string, byte[]> updates)
    {
        var snapshot = new BackupSnapshot("", "", 6, [], [], [], 0, [], [], updates);
        try
        {
            if (updates.ContainsKey(ContractOverrides.SettingsKey)) _ = ContractOverrides.Read(snapshot, "");
            if (updates.TryGetValue("bankStatementManualAmounts", out var raw))
            {
                if (PropertyListParser.Parse(raw) is not NSData data) throw Invalid();
                using var json = JsonDocument.Parse(data.Bytes);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var month in json.RootElement.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(month.Name) || month.Name.Length > 200 || !names.Add(month.Name)) throw Invalid();
                    _ = BankStatementCalculator.Manual(snapshot, month.Name);
                }
            }
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException)
        { throw new InvalidDataException("إعدادات الحسابات غير صالحة؛ لم تُغيّر البيانات.", ex); }
    }
    public MutationResult SaveSettings(IReadOnlyDictionary<string, byte[]> updates, string expectedRevision, string description)
    {
        if (updates is null || updates.Any(p => string.IsNullOrEmpty(p.Key) || p.Key.Length > 1024 || p.Value is null || p.Value.Length > MaximumRowBytes)) throw Invalid();
        ValidateSettingUpdates(updates);
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); CheckCurrent(c, t, expectedRevision);
        var state = CurrentAuxiliaryState(c, t); var entries = ReadSet(c, t, state.GetValueOrDefault("settings"));
        foreach (var p in updates) entries[p.Key] = SaveValue(c, t, p.Value);
        SetArea(c, t, state, "settings", entries);
        if (updates.ContainsKey(ContractOverrides.SettingsKey))
        {
            var meta = ReadSet(c, t, state.GetValueOrDefault("meta"));
            int version = meta.TryGetValue("sourceVersion", out var versionId) ? JsonSerializer.Deserialize<int>(ReadValue(c, t, versionId)) : 5;
            if (version < 6) { meta["sourceVersion"] = SaveValue(c, t, Encode(6)); SetArea(c, t, state, "meta", meta); }
        }
        var result = SaveAuxiliaryRevision(c, t, state, description, Hash(Encode(updates.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray())), expectedRevision);
        if (result.Changed) t.Commit(); return result;
    }
    public MutationResult ImportAccounts(AccountImportPreview preview, string sourceName, string sourceSha256, string expectedRevision)
    {
        CheckSource(sourceName, sourceSha256);
        if (preview is null || preview.Problems is null || preview.Problems.Count != 0 || preview.Accounts is null || preview.Accounts.Count is 0 or > 100_000) throw Invalid();
        var incoming = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in preview.Accounts)
        {
            CheckAccount(a);
            if (!incoming.Add(PayrollAnalysis.Normalize(a.Entity)) || a.Iban.Length > 0 && !TransferPreparation.ValidIban(a.Iban) || a.Swift.Length > 0 && !TransferPreparation.ValidSwift(a.Swift) || a.Iban.Length == 0 && string.IsNullOrEmpty(a.LocalAccount)) throw Invalid();
            if (a.LocalAccount is { Length: > 0 } local)
            {
                var name = PayrollAnalysis.Normalize(a.Entity);
                bool allowed = name.Contains(PayrollAnalysis.Normalize("صندوق الإسكان"), StringComparison.Ordinal) || name.Contains(PayrollAnalysis.Normalize("الموارد المائية"), StringComparison.Ordinal);
                if (local.Length != 15 || !local.All(char.IsAsciiDigit) || !allowed || TransferPreparation.Classify(a) != TransferGroup.Anbar || a.Iban.Length > 0 && !a.Iban.EndsWith(local, StringComparison.Ordinal)) throw Invalid();
            }
        }
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); CheckCurrent(c, t, expectedRevision);
        var state = CurrentAuxiliaryState(c, t); var accounts = ReadList<ExecutionAccount>(c, t, state, "accounts").ToList();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < accounts.Count; i++) if (!indices.TryAdd(PayrollAnalysis.Normalize(accounts[i].Entity), i)) throw new InvalidDataException("أسماء الحسابات المحفوظة متشابهة؛ راجعها قبل الدمج.");
        foreach (var a in preview.Accounts)
        {
            if (!indices.TryGetValue(PayrollAnalysis.Normalize(a.Entity), out int index)) { indices.Add(PayrollAnalysis.Normalize(a.Entity), accounts.Count); accounts.Add(a); continue; }
            var old = accounts[index];
            if (!string.IsNullOrEmpty(old.LocalAccount) && !string.IsNullOrEmpty(a.LocalAccount) && old.LocalAccount != a.LocalAccount) throw new InvalidDataException("رقم الحساب المحلي يتعارض مع المحفوظ؛ لم تُغيّر الحسابات.");
            var combined = a with { Branch = a.Branch.Length == 0 ? old.Branch : a.Branch, Iban = a.Iban.Length == 0 ? old.Iban : a.Iban, Swift = a.Swift.Length == 0 ? old.Swift : a.Swift, LocalAccount = string.IsNullOrEmpty(a.LocalAccount) ? old.LocalAccount : a.LocalAccount };
            if (!string.IsNullOrEmpty(combined.LocalAccount) && combined.Iban.Length > 0 && !combined.Iban.EndsWith(combined.LocalAccount, StringComparison.Ordinal)) throw new InvalidDataException("الحساب المحلي لا يطابق IBAN؛ لم تُغيّر الحسابات.");
            accounts[index] = combined;
        }
        SaveList(c, t, state, "accounts", accounts); var result = SaveAuxiliaryRevision(c, t, state, sourceName, sourceSha256, expectedRevision);
        if (result.Changed) t.Commit(); return result;
    }
    public MutationResult ImportStaff(IReadOnlyList<StaffMember> staff, string sourceName, string sourceSha256, string expectedRevision, bool replaceExisting)
    {
        CheckSource(sourceName, sourceSha256);
        if (staff is null || staff.Count is 0 or > 500_000) throw Invalid(); foreach (var member in staff) CheckStaff(member);
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); CheckCurrent(c, t, expectedRevision);
        var state = CurrentAuxiliaryState(c, t); string? old = state.GetValueOrDefault("staff");
        SaveList(c, t, state, "staff", staff);
        if (old is not null && old != state.GetValueOrDefault("staff") && !replaceExisting) throw new InvalidDataException("يلزم تأكيد استبدال الملاك بعد المراجعة.");
        var result = SaveAuxiliaryRevision(c, t, state, sourceName, sourceSha256, expectedRevision); if (result.Changed) t.Commit(); return result;
    }
    public MutationResult ImportAnnualDeductions(AnnualDeductionPreview preview, IReadOnlyDictionary<string, bool> selectedMonths, string sourceName, string sourceSha256, string expectedRevision)
    {
        CheckSource(sourceName, sourceSha256);
        if (preview is null || preview.Problems is null || preview.Problems.Count != 0 || preview.Months is null || preview.Months.Count > 5000 || selectedMonths is null || preview.Months.Any(p => p is null || p.Problems is null || p.Problems.Count != 0 || p.Employees is null || p.Employees.Count != 0 || p.Deductions is null || p.Deductions.Count == 0) || preview.Months.Select(p => p.Month).Distinct(StringComparer.Ordinal).Count() != preview.Months.Count || selectedMonths.Keys.Any(k => !preview.Months.Any(p => p.Month == k))) throw Invalid();
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); string current = CheckCurrent(c, t, expectedRevision); bool changed = false;
        foreach (var month in preview.Months)
        {
            if (!selectedMonths.TryGetValue(month.Month, out bool replace)) continue;
            var result = ImportCore(c, t, month, sourceName, sourceSha256, current, replace); current = result.RevisionId; changed |= result.Changed;
        }
        if (changed) t.Commit(); return new(changed, current);
    }
    private static void CheckAccount(ExecutionAccount a)
    { if (a is null || string.IsNullOrWhiteSpace(a.Entity) || a.Iban is null || a.Branch is null || a.Swift is null) throw Invalid(); }
    private static void CheckStaff(StaffMember s)
    { if (s is null || string.IsNullOrWhiteSpace(s.Name) || s.Mother is null || s.Stat is null || s.Unified is null || s.Rank is null || s.Status is null || s.Workplace is null) throw Invalid(); }
    private static void CheckBook(ArchiveBook b)
    { if (b is null || b.Id == Guid.Empty || string.IsNullOrWhiteSpace(b.Title) || b.Month is null || b.Pages is < 1 or > 3 || b.CreatedAt is { } date && (!double.IsFinite(date) || Math.Abs(date) >= 4e9)) throw Invalid(); }
    private static void CheckAttachmentKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 2048 || key.StartsWith('/') || key.Contains('\\') || key.Contains(':') || key.Any(char.IsControl) || key.Split('/').Any(p => p is "" or "." or "..")) throw Invalid();
    }
    /// يستورد مجموعة كاملة داخل مساحة جديدة فقط؛ لا يمزجها ببيانات الجهاز الحالية.
    public MutationResult ImportDataset(BackupSnapshot snapshot, IReadOnlyList<StaffMember> staff, IReadOnlyDictionary<string, byte[]> attachments, string expectedRevision)
    {
        if (snapshot is null || staff is null || attachments is null || snapshot.Months is null || snapshot.Employees is null || snapshot.Deductions is null || snapshot.Accounts is null || snapshot.Books is null || snapshot.SettingsPlists is null || snapshot.Version is < 1 or > 10 || snapshot.Months.Count > 5000 || snapshot.StaffCount != staff.Count || staff.Count > 500_000 || snapshot.Accounts.Count > 100_000 || snapshot.Books.Count > 100_000 || snapshot.Months.Any(m => m is null || string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 200) || snapshot.Months.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count() != snapshot.Months.Count) throw Invalid();
        var months = snapshot.Months.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        if (snapshot.Employees.Any(e => e is null || !months.Contains(e.Month)) || snapshot.Deductions.Any(d => d is null || !months.Contains(d.Month))) throw Invalid();
        foreach (var a in snapshot.Accounts) CheckAccount(a); foreach (var s in staff) CheckStaff(s); foreach (var b in snapshot.Books) CheckBook(b);
        if (snapshot.Books.Select(b => b.Id).Distinct().Count() != snapshot.Books.Count) throw Invalid();
        var attachmentNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in attachments) { CheckAttachmentKey(entry.Key); if (entry.Value is null || entry.Value.Length > MaximumAuxiliaryValueBytes || !attachmentNames.TryAdd(entry.Key, entry.Key)) throw Invalid(); }
        foreach (var b in snapshot.Books)
        {
            if (!attachmentNames.TryGetValue($"books/{b.Id:D}.pdf", out var key) || attachments[key].Length is < 5 or > 100_000_000 || !attachments[key].AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw Invalid();
        }
        if (snapshot.SettingsPlists.Any(p => string.IsNullOrEmpty(p.Key) || p.Key.Length > 1024 || p.Value is null || p.Value.Length > MaximumRowBytes)) throw Invalid();
        if (snapshot.CreatedAt is { } date && (!double.IsFinite(date) || Math.Abs(date) >= 4e9)) throw Invalid();
        using var c = Connect(); using var t = c.BeginTransaction(deferred: false); string current = CheckCurrent(c, t, expectedRevision);
        if ((long)Scalar(c, t, "SELECT count(*) FROM revisions") != 0 || (long)Scalar(c, t, "SELECT count(*) FROM months") != 0) throw new InvalidDataException("استيراد مجموعة كاملة متاح في مساحة جديدة فقط.");
        var employees = snapshot.Employees.GroupBy(e => e.Month, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var deductions = snapshot.Deductions.GroupBy(d => d.Month, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        string sourceHash = IsHash(snapshot.SourceDigest?.ToUpperInvariant()) ? snapshot.SourceDigest!.ToUpperInvariant() : Hash(Encode(snapshot.Months));
        foreach (var month in snapshot.Months)
        {
            var e = employees.GetValueOrDefault(month.Name) ?? []; var d = deductions.GetValueOrDefault(month.Name) ?? [];
            if (e.Length != month.EmployeeCount || d.Length != month.DeductionCount) throw Invalid();
            current = ImportCore(c, t, new(month.Name, e, d, []), "استيراد مجموعة بيانات", sourceHash, current, false, historical: true).RevisionId;
        }
        var state = EmptyMap();
        SaveList(c, t, state, "accounts", snapshot.Accounts); SaveList(c, t, state, "staff", staff); SaveList(c, t, state, "books", snapshot.Books);
        void SaveMap(string area, IReadOnlyDictionary<string, byte[]> data)
        { var entries = EmptyMap(); foreach (var p in data) entries.Add(p.Key, SaveValue(c, t, p.Value)); SetArea(c, t, state, area, entries); }
        SaveMap("settings", snapshot.SettingsPlists); SaveMap("attachments", attachments);
        SaveMap("meta", new Dictionary<string, byte[]> { ["sourceVersion"] = Encode(snapshot.Version), ["createdAt"] = Encode(snapshot.CreatedAt) });
        var result = SaveAuxiliaryRevision(c, t, state, "استيراد بيانات المجموعة المرافقة", sourceHash, current); t.Commit(); return new(true, result.RevisionId);
    }
    private static void TranscodeAuxiliary(SqliteConnection c, SqliteTransaction t, int targetCodec)
    {
        var ids = new List<string>();
        using (var cmd = Command(c, t, "SELECT id FROM auxiliary_chunks WHERE codec<>$0", targetCodec))
        using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        using var update = Command(c, t, "UPDATE auxiliary_chunks SET payload=$0,codec=$1 WHERE id=$2", Array.Empty<byte>(), targetCodec, ""); update.Prepare();
        foreach (var id in ids)
        {
            var bytes = ReadChunk(c, t, id); update.Parameters[0].Value = targetCodec == 0 ? bytes : CompressRow(bytes); update.Parameters[2].Value = id; update.ExecuteNonQuery();
        }
    }
    // يفحص جميع القيم التاريخية والروابط، وليس حالة الشاشة الحالية وحدها.
    private static Dictionary<string, byte[]> ValidateAuxiliary(SqliteConnection c, SqliteTransaction t)
    {
        var chunks = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var cmd = Command(c, t, "SELECT id,payload,raw_length,length(payload),codec FROM auxiliary_chunks"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            if (r.GetInt64(2) > ChunkBytes) throw Invalid(); var bytes = DecompressRow(r, 1, 2, 3, 4);
            if (Hash(bytes) != r.GetString(0)) throw Invalid(); chunks.Add(r.GetString(0), bytes.Length);
        }
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var cmd = Command(c, t, "SELECT id,length FROM auxiliary_values"))
        using (var r = cmd.ExecuteReader()) while (r.Read()) values.Add(r.GetString(0), r.GetInt32(1));
        var usedChunks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, length) in values)
        {
            var parts = ValueParts(c, t, id); using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); int offset = 0;
            foreach (var chunk in parts.Chunks)
            {
                if (!chunks.TryGetValue(chunk, out int size) || size != Math.Min(ChunkBytes, length - offset)) throw Invalid();
                hasher.AppendData(ReadChunk(c, t, chunk)); usedChunks.Add(chunk); offset += size;
            }
            if (offset != length || Convert.ToHexString(hasher.GetHashAndReset()) != id) throw Invalid();
        }
        if (usedChunks.Count != chunks.Count) throw Invalid();
        var sets = new Dictionary<string, (string Area, int Count)>(StringComparer.Ordinal);
        using (var cmd = Command(c, t, "SELECT id,area,count FROM auxiliary_sets"))
        using (var r = cmd.ExecuteReader()) while (r.Read()) sets.Add(r.GetString(0), (r.GetString(1), r.GetInt32(2)));
        var usedValues = new HashSet<string>(StringComparer.Ordinal); var booksBySet = new Dictionary<string, IReadOnlyList<ArchiveBook>>(StringComparer.Ordinal);
        foreach (var (id, set) in sets)
        {
            if (!Areas.Contains(set.Area, StringComparer.Ordinal) || set.Count <= 0) throw Invalid();
            var entries = ReadSet(c, t, id);
            if (entries.Count != set.Count || Hash(Encode(new { Area = set.Area, Entries = entries.Select(p => new SetEntry(p.Key, p.Value)).ToArray() })) != id) throw Invalid();
            int index = 0; var books = new List<ArchiveBook>();
            foreach (var (key, value) in entries)
            {
                if (!values.ContainsKey(value)) throw Invalid(); usedValues.Add(value);
                if (set.Area is "accounts" or "staff" or "books")
                {
                    if (key != Ordinal(index++)) throw Invalid(); var bytes = ReadValue(c, t, value);
                    if (set.Area == "accounts") CheckAccount(JsonSerializer.Deserialize<ExecutionAccount>(bytes) ?? throw Invalid());
                    if (set.Area == "staff") CheckStaff(JsonSerializer.Deserialize<StaffMember>(bytes) ?? throw Invalid());
                    if (set.Area == "books") { var b = JsonSerializer.Deserialize<ArchiveBook>(bytes) ?? throw Invalid(); CheckBook(b); books.Add(b); }
                }
                else if (set.Area == "settings") { if (string.IsNullOrEmpty(key) || key.Length > 1024 || values[value] > MaximumRowBytes) throw Invalid(); }
                else if (set.Area == "attachments") CheckAttachmentKey(key);
                else if (set.Area == "meta")
                {
                    var bytes = ReadValue(c, t, value);
                    if (key == "sourceVersion") { if (JsonSerializer.Deserialize<int>(bytes) is < 1 or > 10) throw Invalid(); }
                    else if (key == "createdAt") { var date = JsonSerializer.Deserialize<double?>(bytes); if (date is { } v && (!double.IsFinite(v) || Math.Abs(v) >= 4e9)) throw Invalid(); }
                    else throw Invalid();
                }
            }
            if (set.Area == "books") { if (books.Select(b => b.Id).Distinct().Count() != books.Count) throw Invalid(); booksBySet[id] = books; }
            if (set.Area == "accounts" && entries.Count > 100_000 || set.Area == "staff" && entries.Count > 500_000 || set.Area == "books" && entries.Count > 100_000) throw Invalid();
        }
        if (usedValues.Count != values.Count) throw Invalid();
        var revisions = new Dictionary<string, byte[]>(StringComparer.Ordinal); var usedSets = new HashSet<string>(StringComparer.Ordinal); byte[]? previous = null;
        using (var cmd = Command(c, t, "SELECT a.revision,a.state FROM auxiliary_revisions a JOIN revisions r ON r.id=a.revision ORDER BY r.seq"))
        using (var r = cmd.ExecuteReader()) while (r.Read())
        {
            var bytes = (byte[])r[1]; var state = ParseState(bytes);
            if (previous is not null && previous.SequenceEqual(bytes)) throw Invalid(); previous = bytes;
            foreach (var (area, setId) in state) { if (!sets.TryGetValue(setId, out var set) || set.Area != area) throw Invalid(); usedSets.Add(setId); }
            var attachments = ReadSet(c, t, state.GetValueOrDefault("attachments")); var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in attachments) if (!names.TryAdd(a.Key, a.Value)) throw Invalid();
            if (state.TryGetValue("books", out var bookSet)) foreach (var book in booksBySet[bookSet])
            {
                if (!names.TryGetValue($"books/{book.Id:D}.pdf", out var attachment) || values[attachment] is < 5 or > 100_000_000) throw Invalid();
                var parts = ValueParts(c, t, attachment); var first = ReadChunk(c, t, parts.Chunks[0]); if (!first.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw Invalid();
            }
            revisions.Add(r.GetString(0), bytes);
        }
        if (usedSets.Count != sets.Count) throw Invalid(); return revisions;
    }
}

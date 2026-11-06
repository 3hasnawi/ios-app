using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SalaryLocal.Core;

public sealed record LocalAccessColumn(string Name, byte Type, int Number, int VarIndex,
    bool IsFixed, int FixedOffset, int Length, bool IsCalculated);

public sealed record LocalAccessTableInfo(string Name, int DefinitionPage, int DeclaredRowCount,
    IReadOnlyList<LocalAccessColumn> Columns);

public sealed record LocalAccessDiagnostic(int PageNumber, int RowIndex, string Message);

/// <summary>نتيجة مراجعة فقط؛ لا يجوز حفظها قبل EnsureComplete.</summary>
public sealed class LocalAccessTableData
{
    public LocalAccessTableInfo Table { get; }
    public IReadOnlyList<LocalAccessColumn> Columns => Table.Columns;
    public int DeclaredRowCount => Table.DeclaredRowCount;
    public IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows { get; }
    public IReadOnlyList<LocalAccessDiagnostic> Diagnostics { get; }
    public int RejectedRowCount { get; }
    public int DeletedRowCount { get; }
    public int ForwardedRowCount { get; }
    public int DiagnosticCount { get; }
    public bool IsComplete => RejectedRowCount == 0 && DiagnosticCount == 0 && Rows.Count == DeclaredRowCount;

    internal LocalAccessTableData(LocalAccessTableInfo table, List<IReadOnlyDictionary<string, string?>> rows,
        List<LocalAccessDiagnostic> diagnostics, int rejected, int deleted, int forwarded, int diagnosticCount)
    {
        Table = table; Rows = rows.AsReadOnly(); Diagnostics = diagnostics.AsReadOnly();
        RejectedRowCount = rejected; DeletedRowCount = deleted; ForwardedRowCount = forwarded; DiagnosticCount = diagnosticCount;
    }

    public void EnsureComplete()
    {
        if (!IsComplete)
            throw new InvalidDataException($"تعذّرت قراءة جدول Access كاملاً. العدد المعلن: {DeclaredRowCount}، المقروء: {Rows.Count}، الصفوف المرفوضة: {RejectedRowCount}، المحذوفة المستبعدة: {DeletedRowCount}. " +
                (Diagnostics.FirstOrDefault()?.Message ?? "عدد السجلات غير مطابق. لا يمكن استيراد نتيجة جزئية."));
    }
}

/// <summary>
/// قارئ محلي للقراءة فقط، مستمد من SalaryApp/AccessReader.swift، دون ACE أو Office.
/// يدعم صفحات Jet4 وACE التقليدية وأنواع Swift البسيطة؛ يرفض الأنواع المحسوبة/المركبة والملفات المشفرة.
/// لا ينفذ الاستعلامات أو وحدات الماكرو ولا يفتح الجداول المرتبطة. لا يدعم الاستخدام المتزامن.
/// </summary>
public sealed class LocalAccessReader : IDisposable
{
    public const int PageSize = 4096;
    public const long MaximumDatabaseBytes = 512L * 1024 * 1024;
    public const int MaximumTableRows = 500_000;
    public const int MaximumMemoBytes = 8 * 1024 * 1024;
    private const long MaximumCells = 10_000_000;
    private const long MaximumTextCharacters = 64L * 1024 * 1024;
    private const long MaximumLongValueWork = 128L * 1024 * 1024;
    private const int MaximumPhysicalRows = 2_000_000;
    private const int MaximumColumns = 255;
    private const int MaximumDefinitionPages = 256;
    private const int MaximumDiagnosticSamples = 100;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    private readonly FileStream stream;
    private readonly long sourceLength;
    private readonly Dictionary<int, List<int>> dataPages = [];
    private readonly Dictionary<int, Definition> definitions = [];
    private IReadOnlyList<LocalAccessTableInfo>? userTables;
    private long longValueWork;
    private bool disposed;
    public int PageCount { get; }
    public byte FormatVersion { get; }
    public string SourceSha256 { get; }

    private sealed record Definition(LocalAccessTableInfo Info, int StoredColumnCount, int VariableColumnCount);
    private readonly record struct RowAddress(int Page, int Index);
    private readonly record struct RowSlice(byte[] Bytes, int Flags);

    private LocalAccessReader(FileStream source)
    {
        stream = source; sourceLength = source.Length;
        if (sourceLength < PageSize * 3 || sourceLength % PageSize != 0 || sourceLength > MaximumDatabaseBytes)
            throw Invalid("حجم قاعدة Access غير صالح أو يتجاوز حد القراءة المحلية (512 ميغابايت).");
        PageCount = checked((int)(sourceLength / PageSize));
        var header = ReadPage(0);
        FormatVersion = ValidateHeader(header);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var page = new byte[PageSize];
        source.Position = 0;
        for (var n = 0; n < PageCount; n++)
        {
            source.ReadExactly(page); hash.AppendData(page);
            if (page[0] != 1) continue;
            var owner = unchecked((int)U32(page, 4));
            if (!dataPages.TryGetValue(owner, out var list)) dataPages[owner] = list = [];
            list.Add(n);
        }
        SourceSha256 = Convert.ToHexString(hash.GetHashAndReset());
    }

    public static LocalAccessReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, PageSize, FileOptions.RandomAccess);
        try { return new LocalAccessReader(source); }
        catch { source.Dispose(); throw; }
    }

    /// <summary>يُستدعى بعد المراجعة، ويجب فحص البصمة المحفوظة ثانية قبل الحفظ المحلي.</summary>
    public void VerifyUnchanged()
    {
        ThrowIfDisposed();
        if (stream.Length != sourceLength) throw Invalid("تغيّر ملف Access أثناء القراءة. أعد المراجعة.");
        stream.Position = 0;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), SourceSha256, StringComparison.Ordinal))
            throw Invalid("تغيّر ملف Access أثناء القراءة. أعد المراجعة.");
    }

    public IReadOnlyList<LocalAccessTableInfo> UserTables()
    {
        ThrowIfDisposed();
        if (userTables is not null) return userTables;
        var system = ReadDefinition(2, "MSysObjects");
        var required = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
            ["Id"] = [4], ["Name"] = [10], ["Type"] = [2, 3, 4], ["Flags"] = [4]
        };
        foreach (var (name, types) in required)
        {
            var column = system.Info.Columns.SingleOrDefault(c => c.Name == name);
            if (column is null || !types.Contains(column.Type) || column.IsCalculated)
                throw Invalid("تعريف فهرس جداول Access غير مدعوم أو تالف.");
        }
        // حقول النظام الثنائية لا تُستورد؛ لا نقرأ إلا أعمدة الفهرس الأربعة المعروفة.
        var catalog = ReadRows(system, required.Keys.ToHashSet(StringComparer.Ordinal));
        catalog.EnsureComplete();
        var tables = new List<LocalAccessTableInfo>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new HashSet<int>();
        foreach (var row in catalog.Rows)
        {
            if (!int.TryParse(row["Type"], NumberStyles.Integer, Invariant, out var type))
                throw Invalid("نوع أحد عناصر فهرس Access مفقود أو غير صالح.");
            if (type != 1) continue;
            var name = row["Name"];
            if (string.IsNullOrEmpty(name)) throw Invalid("اسم جدول Access مفقود.");
            if (name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(row["Flags"], NumberStyles.Integer, Invariant, out var flags))
                throw Invalid("أعلام جدول Access مفقودة أو غير صالحة.");
            if ((unchecked((uint)flags) & 0x80000002) != 0) continue;
            if (!int.TryParse(row["Id"], NumberStyles.Integer, Invariant, out var id))
                throw Invalid("معرّف جدول Access مفقود أو غير صالح.");
            var page = id & 0x00ffffff;
            if (page <= 2) throw Invalid("مرجع جدول المستخدم يشير إلى صفحة نظام.");
            if (!names.Add(name) || !pages.Add(page)) throw Invalid("فهرس Access يحتوي أسماء أو معرّفات جداول مكررة.");
            if (tables.Count >= 1024) throw Invalid("عدد جداول Access يتجاوز حد القراءة المحلية.");
            tables.Add(ReadDefinition(page, name).Info);
        }
        userTables = tables.AsReadOnly();
        return userTables;
    }

    /// <summary>المسار المخصص للاستيراد: لا يعيد أية نتيجة جزئية.</summary>
    public LocalAccessTableData ReadTable(string tableName)
    {
        var result = InspectTable(tableName);
        result.EnsureComplete();
        return result;
    }

    /// <summary>يعيد التشخيص والصفوف السليمة للمراجعة فقط؛ يجب منع الحفظ عند !IsComplete.</summary>
    public LocalAccessTableData InspectTable(string tableName)
    {
        var table = UserTables().SingleOrDefault(t => string.Equals(t.Name, tableName, StringComparison.Ordinal));
        if (table is null) throw Invalid("لم يُعثر على جدول Access المحدد.");
        return ReadRows(definitions[table.DefinitionPage], null);
    }

    private Definition ReadDefinition(int number, string name)
    {
        if (definitions.TryGetValue(number, out var cached)) return cached;
        if (number <= 0 || number >= PageCount) throw Invalid("مرجع تعريف جدول Access خارج الملف.");
        var first = ReadPage(number);
        if (first[0] != 2) throw Invalid("صفحة تعريف جدول Access غير صالحة.");
        using var buffer = new MemoryStream();
        buffer.Write(first);
        var next = U32(first, 4);
        var visited = new HashSet<int> { number };
        while (next != 0)
        {
            if (next >= PageCount || !visited.Add((int)next) || visited.Count > MaximumDefinitionPages)
                throw Invalid("سلسلة تعريف جدول Access تالفة أو أطول من الحد المدعوم.");
            var continuation = ReadPage((int)next);
            if (continuation[0] != 2) throw Invalid("صفحة متابعة تعريف جدول Access غير صالحة.");
            buffer.Write(continuation, 8, PageSize - 8);
            next = U32(continuation, 4);
        }
        var bytes = buffer.ToArray();
        var rowCount = U32(bytes, 0x10);
        var storedColumns = U16(bytes, 0x29);
        var variableColumns = U16(bytes, 0x2b);
        var columnCount = U16(bytes, 0x2d);
        var indexCount = U32(bytes, 0x33);
        if (rowCount > MaximumTableRows || storedColumns > 1024 || columnCount is 0 or > MaximumColumns ||
            storedColumns < columnCount || variableColumns > storedColumns || indexCount > (bytes.Length - 0x3f) / 12)
            throw Invalid("تعريف جدول Access غير مدعوم أو يتجاوز حدود القراءة المحلية.");
        var offset = 0x3f + checked((int)indexCount * 12);
        if (columnCount > (bytes.Length - offset) / 25) throw Invalid("تعريف أعمدة Access مبتور.");
        var descriptors = offset;
        offset += columnCount * 25;
        var columns = new List<LocalAccessColumn>(columnCount);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var numbers = new HashSet<int>();
        var variableIndexes = new HashSet<int>();
        for (var i = 0; i < columnCount; i++)
        {
            var at = descriptors + i * 25;
            var length = U16(bytes, offset); offset += 2;
            if (length == 0 || length > 128 || length % 2 != 0) throw Invalid("اسم عمود Access غير صالح.");
            var columnName = DecodeUnicode(Slice(bytes, offset, length)); offset += length;
            var column = new LocalAccessColumn(columnName, bytes[at], U16(bytes, at + 5), U16(bytes, at + 7),
                (bytes[at + 15] & 1) != 0, U16(bytes, at + 21), U16(bytes, at + 23), (bytes[at + 16] & 0xc0) != 0);
            if (!names.Add(column.Name) || !numbers.Add(column.Number) || column.Number >= storedColumns || column.Name.Contains('\0'))
                throw Invalid("أسماء أو أرقام أعمدة Access مكررة أو غير صالحة.");
            if (!column.IsFixed && (column.VarIndex >= variableColumns || !variableIndexes.Add(column.VarIndex)))
                throw Invalid("فهرس الأعمدة المتغيرة في Access غير صالح.");
            if (column.IsFixed && column.FixedOffset + column.Length > PageSize - 2)
                throw Invalid("موضع عمود Access الثابت غير صالح.");
            columns.Add(column);
        }
        var definition = new Definition(new LocalAccessTableInfo(name, number, (int)rowCount, columns.AsReadOnly()), storedColumns, variableColumns);
        definitions.Add(number, definition);
        return definition;
    }

    private LocalAccessTableData ReadRows(Definition table, HashSet<string>? projection)
    {
        ThrowIfDisposed();
        var columns = table.Info.Columns.Where(c => projection is null || projection.Contains(c.Name)).ToArray();
        foreach (var column in columns)
            if (column.IsCalculated || !IsSupportedType(column.Type))
                throw Invalid($"نوع عمود Access غير مدعوم (رقم العمود {column.Number + 1}، النوع {column.Type}). لا يمكن إسقاط العمود واستيراد بقية السجل.");
        if ((long)table.Info.DeclaredRowCount * columns.Length > MaximumCells)
            throw Invalid("عدد خلايا جدول Access يتجاوز حد القراءة المحلية.");
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        var diagnostics = new List<LocalAccessDiagnostic>();
        var rejected = 0; var deleted = 0; var forwarded = 0; var diagnosticCount = 0; var physicalRows = 0;
        long characters = 0;
        longValueWork = 0;
        var claimedTargets = new HashSet<RowAddress>();
        var deletedAddresses = new HashSet<RowAddress>();
        void Diagnose(int page, int index, string message)
        {
            diagnosticCount++;
            if (diagnostics.Count < MaximumDiagnosticSamples) diagnostics.Add(new LocalAccessDiagnostic(page, index, message));
        }
        foreach (var pageNumber in dataPages.GetValueOrDefault(table.Info.DefinitionPage) ?? [])
        {
            var page = ReadPage(pageNumber);
            var count = U16(page, 12);
            if (count > (PageSize - 14) / 2) { Diagnose(pageNumber, -1, "عدد الصفوف في صفحة Access غير صالح."); continue; }
            physicalRows += count;
            if (physicalRows > MaximumPhysicalRows) throw Invalid("عدد الصفوف المادية يتجاوز حد القراءة المحلية.");
            for (var i = 0; i < count; i++)
            {
                var flags = U16(page, 14 + i * 2);
                if ((flags & 0x8000) != 0) { deletedAddresses.Add(new RowAddress(pageNumber, i)); continue; }
                IReadOnlyDictionary<string, string?> values;
                try
                {
                    var raw = SliceRow(page, i);
                    var visited = new HashSet<RowAddress> { new(pageNumber, i) };
                    var moved = false;
                    while ((raw.Flags & 0x4000) != 0)
                    {
                        if (raw.Bytes.Length < 4) throw Invalid("مؤشر نقل صف Access مبتور.");
                        var target = new RowAddress(U24(raw.Bytes, 1), raw.Bytes[0]);
                        if (!visited.Add(target) || visited.Count > 64 || !claimedTargets.Add(target))
                            throw Invalid("مؤشر نقل صف Access دائري أو مكرر أو أطول من الحد المدعوم.");
                        var targetPage = ReadReferencedPage(target.Page);
                        if (targetPage[0] != 1 || U32(targetPage, 4) != table.Info.DefinitionPage)
                            throw Invalid("مؤشر نقل صف Access يشير إلى جدول مختلف أو صفحة غير صالحة.");
                        raw = SliceRow(targetPage, target.Index);
                        if ((raw.Flags & 0x8000) == 0) throw Invalid("الصف المنقول غير معلّم كصف داخلي؛ يمنع استيراده مرتين.");
                        moved = true;
                    }
                    values = ParseRow(raw.Bytes, table, columns);
                    if (moved) forwarded++;
                }
                catch (InvalidDataException ex)
                {
                    if (longValueWork > MaximumLongValueWork) throw;
                    rejected++; Diagnose(pageNumber, i, ex.Message); continue;
                }
                characters += values.Values.Sum(v => (long)(v?.Length ?? 0));
                if (characters > MaximumTextCharacters || rows.Count >= MaximumTableRows || (long)(rows.Count + 1) * columns.Length > MaximumCells)
                    throw Invalid("حجم بيانات جدول Access المقروءة يتجاوز حد القراءة المحلية.");
                rows.Add(values);
            }
        }
        // الصف الداخلي المنقول ليس سجلاً محذوفاً من بيانات المستخدم.
        deleted = deletedAddresses.Count(a => !claimedTargets.Contains(a));
        if (rows.Count != table.Info.DeclaredRowCount)
            Diagnose(table.Info.DefinitionPage, -1, "عدد الصفوف السليمة لا يطابق العدد المعلن في الجدول؛ لا يمكن اعتماد استيراد جزئي.");
        return new LocalAccessTableData(table.Info, rows, diagnostics, rejected, deleted, forwarded, diagnosticCount);
    }

    private IReadOnlyDictionary<string, string?> ParseRow(byte[] row, Definition table, LocalAccessColumn[] columns)
    {
        var count = U16(row, 0);
        if (count == 0 || count > table.StoredColumnCount) throw Invalid("عدد أعمدة صف Access غير صالح.");
        var maskSize = (count + 7) / 8;
        var maskStart = row.Length - maskSize;
        if (maskStart < 2) throw Invalid("قناع القيم الفارغة في صف Access مبتور.");
        var dataEnd = maskStart;
        int[] offsets = [];
        if (table.VariableColumnCount > 0)
        {
            var varCount = U16(row, maskStart - 2);
            if (varCount > table.VariableColumnCount) throw Invalid("عدد الحقول المتغيرة في صف Access غير صالح.");
            var footerStart = maskStart - 2 - 2 * (varCount + 1);
            if (footerStart < 2) throw Invalid("جدول مواضع حقول Access مبتور.");
            offsets = new int[varCount + 1];
            for (var i = 0; i <= varCount; i++)
            {
                offsets[i] = U16(row, maskStart - 4 - 2 * i);
                if (offsets[i] < 2 || offsets[i] > footerStart || (i > 0 && offsets[i] < offsets[i - 1]))
                    throw Invalid("مواضع حقول Access متداخلة أو خارج الصف.");
            }
            dataEnd = offsets[0];
        }
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (column.Number >= count) { values.Add(column.Name, null); continue; }
            var present = (row[maskStart + column.Number / 8] & (1 << (column.Number % 8))) != 0;
            if (column.Type == 1) { values.Add(column.Name, present ? "true" : "false"); continue; }
            if (!present) { values.Add(column.Name, null); continue; }
            ReadOnlySpan<byte> field;
            if (column.IsFixed)
            {
                var start = 2 + column.FixedOffset;
                if (start + column.Length > dataEnd) throw Invalid("حقل Access الثابت يتجاوز مساحة بيانات الصف.");
                field = Slice(row, start, column.Length);
            }
            else
            {
                if (column.VarIndex >= offsets.Length - 1) throw Invalid("موضع حقل Access غير موجود رغم وجود قيمته.");
                field = Slice(row, offsets[column.VarIndex], offsets[column.VarIndex + 1] - offsets[column.VarIndex]);
            }
            values.Add(column.Name, ReadValue(column.Type, field));
        }
        return new ReadOnlyDictionary<string, string?>(values);
    }

    private string ReadValue(byte type, ReadOnlySpan<byte> bytes)
    {
        var length = type switch { 2 => 1, 3 => 2, 4 or 6 => 4, 5 or 7 or 8 => 8, _ => -1 };
        if (length >= 0 && bytes.Length != length) throw Invalid("طول قيمة عددية في Access غير صالح.");
        switch (type)
        {
            case 2: return bytes[0].ToString(Invariant);
            case 3: return BinaryPrimitives.ReadInt16LittleEndian(bytes).ToString(Invariant);
            case 4: return BinaryPrimitives.ReadInt32LittleEndian(bytes).ToString(Invariant);
            case 5: return (BinaryPrimitives.ReadInt64LittleEndian(bytes) / 10000m).ToString("0.####", Invariant);
            case 6:
                var single = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
                if (!float.IsFinite(single)) throw Invalid("قيمة عددية غير منتهية في Access.");
                return single.ToString("R", Invariant);
            case 7:
                var number = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes));
                if (!double.IsFinite(number)) throw Invalid("قيمة عددية غير منتهية في Access.");
                return number.ToString("R", Invariant);
            case 8:
                var days = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes));
                if (!double.IsFinite(days) || Math.Abs(days) >= 200_000) throw Invalid("تاريخ Access خارج النطاق المدعوم.");
                // تاريخ محلي بلا منطقة زمنية؛ نفس أساس أيام قارئ Swift، دون إسقاط وقت اليوم.
                var date = new DateTime(1899, 12, 30).AddDays(days);
                return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", Invariant) : date.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", Invariant);
            case 10: return DecodeText(bytes);
            case 12: return ReadMemo(bytes);
            default: throw Invalid("نوع قيمة Access غير مدعوم.");
        }
    }

    private string ReadMemo(ReadOnlySpan<byte> field)
    {
        if (field.Length == 0) return "";
        if (field.Length < 12) throw Invalid("وصف مذكرة Access مبتور.");
        var header = BinaryPrimitives.ReadUInt32LittleEndian(field);
        var kind = header & 0xc0000000;
        var length = checked((int)(header & 0x3fffffff));
        if (length > MaximumMemoBytes) throw Invalid("مذكرة Access تتجاوز حد 8 ميغابايت.");
        if (kind == 0x80000000)
        {
            if (field.Length - 12 != length) throw Invalid("مذكرة Access المضمّنة مبتورة أو بطول غير مطابق.");
            return DecodeText(field[12..]);
        }
        if (kind == 0xc0000000) throw Invalid("نوع تخزين مذكرة Access غير مدعوم.");
        var address = new RowAddress(U24(field, 5), field[4]);
        if (kind == 0x40000000)
        {
            var raw = ReadLongValueRow(address);
            if (raw.Length != length) throw Invalid("مذكرة Access الخارجية مبتورة أو بطول غير مطابق.");
            return DecodeText(raw);
        }
        using var content = new MemoryStream();
        var visited = new HashSet<RowAddress>();
        while (address.Page != 0)
        {
            if (!visited.Add(address) || visited.Count > 10_000) throw Invalid("سلسلة مذكرة Access دائرية أو أطول من الحد المدعوم.");
            var raw = ReadLongValueRow(address);
            if (raw.Length < 4 || content.Length + raw.Length - 4 > length) throw Invalid("سلسلة مذكرة Access بطول غير مطابق.");
            content.Write(raw, 4, raw.Length - 4);
            address = new RowAddress(U24(raw, 1), raw[0]);
        }
        if (content.Length != length) throw Invalid("سلسلة مذكرة Access مبتورة؛ لا تُستورد ملاحظة ناقصة.");
        return DecodeText(content.ToArray());
    }

    private byte[] ReadLongValueRow(RowAddress address)
    {
        var page = ReadReferencedPage(address.Page);
        // صفحات المذكرات تحمل LVAL بدلاً من معرّف جدول؛ نرفض الإشارة إلى سجلات جدول آخر.
        if (page[0] != 1 || !page.AsSpan(4, 4).SequenceEqual("LVAL"u8)) throw Invalid("مرجع مذكرة Access لا يشير إلى صفحة مذكرات.");
        var raw = SliceRow(page, address.Index);
        if ((raw.Flags & 0xc000) != 0) throw Invalid("صف مذكرة Access محذوف أو منقول بصيغة غير مدعومة.");
        return raw.Bytes;
    }

    private byte[] ReadReferencedPage(int number)
    {
        // تُحسب الصفحة كاملة حتى لا تتحول السلاسل ذات المقاطع الصغيرة إلى ملايين القراءات.
        longValueWork += PageSize;
        if (longValueWork > MaximumLongValueWork) throw Invalid("حجم قراءة مراجع الصفوف والمذكرات في Access يتجاوز الحد المحلي.");
        return ReadPage(number);
    }

    /// <summary>يحفظ النص والمسافات والأصفار كما هي، ويرفض الترميز المبتور بدلاً من تعويض حروفه.</summary>
    public static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0xff || bytes[1] != 0xfe) return DecodeUnicode(bytes);
        using var unicode = new MemoryStream();
        var compressed = true;
        for (var i = 2; i < bytes.Length;)
        {
            if (bytes[i] == 0) { compressed = !compressed; i++; continue; }
            unicode.WriteByte(bytes[i++]);
            if (compressed) unicode.WriteByte(0);
            else
            {
                if (i >= bytes.Length) throw Invalid("نص Access المضغوط مبتور.");
                unicode.WriteByte(bytes[i++]);
            }
        }
        return DecodeUnicode(unicode.ToArray());
    }

    private static string DecodeUnicode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 2 != 0) throw Invalid("نص Access ذو طول غير صالح.");
        try { return StrictUnicode.GetString(bytes); }
        catch (DecoderFallbackException) { throw Invalid("ترميز نص Access تالف؛ لم تُستبدل أحرفه أو تُحذف."); }
    }

    private static byte ValidateHeader(byte[] header)
    {
        if (header[0] != 0 || header[1] != 1 || header[2] != 0 || header[3] != 0)
            throw Invalid("الملف ليس قاعدة Access مدعومة.");
        var version = header[0x14];
        if (version is not (1 or 2 or 3) || header[0x15] != 0 || header[0x16] != 0 || header[0x17] != 0)
            throw Invalid("صيغة Access غير مدعومة. يدعم القارئ Jet4 وACE 2007/2010 التقليديين فقط، ولا يدعم Jet3 أو الصيغ الأحدث.");
        var expected = version == 1 ? "Standard Jet DB\0"u8 : "Standard ACE DB\0"u8;
        if (!header.AsSpan(4, 16).SequenceEqual(expected)) throw Invalid("توقيع قاعدة Access غير صالح.");
        // قناع الترويسة ثابت في صيغة الملف، وليس كلمة سر؛ لا نحاول فك تشفير بيانات المستخدم.
        // مرجع حقول الترويسة: https://jackcess.sourceforge.io/xref/com/healthmarketscience/jackcess/impl/JetFormat.html
        // ومرجع قناع كلمة السر: https://jackcess.sourceforge.io/xref/com/healthmarketscience/jackcess/impl/DatabaseImpl.html
        var unmasked = header.ToArray();
        ApplyHeaderMask(unmasked.AsSpan(0x18, 128));
        if (U32(unmasked, 0x3e) != 0) throw Invalid("قاعدة Access مشفرة أو محمية بكلمة سر؛ القراءة المحلية لا تفك الحماية.");
        var stamp = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(unmasked.AsSpan(0x72, 8)));
        if (!double.IsFinite(stamp) || stamp < int.MinValue || stamp > int.MaxValue) throw Invalid("ترويسة Access تالفة.");
        var mask = unchecked((uint)(int)stamp);
        for (var i = 0; i < 40; i++)
            if ((unmasked[0x42 + i] ^ (byte)(mask >> (8 * (i % 4)))) != 0)
                throw Invalid("قاعدة Access محمية بكلمة سر أو ترويستها غير مدعومة؛ لا تُستورد بياناتها.");
        return version;
    }

    private static void ApplyHeaderMask(Span<byte> data)
    {
        // يولّد قناع الصيغة المنشور من مفتاحها الثابت؛ لا تُقرأ أو تُطلب أي أسرار.
        ReadOnlySpan<byte> key = [0xc7, 0xda, 0x39, 0x6b];
        Span<byte> state = stackalloc byte[256];
        for (var i = 0; i < state.Length; i++) state[i] = (byte)i;
        var j = 0;
        for (var i = 0; i < state.Length; i++)
        {
            j = (j + state[i] + key[i % key.Length]) & 255;
            (state[i], state[j]) = (state[j], state[i]);
        }
        var x = 0; j = 0;
        for (var i = 0; i < data.Length; i++)
        {
            x = (x + 1) & 255; j = (j + state[x]) & 255;
            (state[x], state[j]) = (state[j], state[x]);
            data[i] ^= state[(state[x] + state[j]) & 255];
        }
    }

    private byte[] ReadPage(int number)
    {
        ThrowIfDisposed();
        if (number < 0 || number >= PageCount) throw Invalid("مرجع صفحة Access خارج الملف.");
        var page = new byte[PageSize];
        stream.Position = (long)number * PageSize;
        try { stream.ReadExactly(page); }
        catch (EndOfStreamException) { throw Invalid("صفحة Access مبتورة أو تغيّر الملف أثناء القراءة."); }
        return page;
    }

    private static RowSlice SliceRow(byte[] page, int index)
    {
        if (page[0] != 1) throw Invalid("صفحة بيانات Access غير صالحة.");
        var count = U16(page, 12);
        if (count > (PageSize - 14) / 2 || index < 0 || index >= count) throw Invalid("فهرس صف Access خارج الصفحة.");
        var raw = U16(page, 14 + index * 2);
        var start = raw & 0x1fff;
        var end = index == 0 ? PageSize : U16(page, 14 + (index - 1) * 2) & 0x1fff;
        if ((raw & 0x2000) != 0 || start < 14 + count * 2 || start >= end || end > PageSize)
            throw Invalid("موضع صف Access غير صالح.");
        return new RowSlice(page.AsSpan(start, end - start).ToArray(), raw);
    }

    private static bool IsSupportedType(byte type) => type is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 10 or 12;
    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length) throw Invalid("بنية Access مبتورة أو موضعها خارج البيانات.");
        return bytes.Slice(offset, length);
    }
    private static int U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(bytes, offset, 2));
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(bytes, offset, 4));
    private static int U24(ReadOnlySpan<byte> bytes, int offset)
    {
        var value = Slice(bytes, offset, 3);
        return value[0] | value[1] << 8 | value[2] << 16;
    }
    private static InvalidDataException Invalid(string reason) => new(reason);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose() { if (disposed) return; disposed = true; stream.Dispose(); }
}

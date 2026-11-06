using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SalaryLocal.Core;

internal static class LocalAccessReaderTests
{
    private static int checks;
    private const int PageSize = LocalAccessReader.PageSize;
    private const string MainName = "الرئيسي";
    private sealed record Column(string Name, byte Type, int Length, bool Fixed = true, byte ExtraFlags = 0);
    private sealed record StoredRow(byte[] Bytes, int Flags = 0);
    private static readonly Column[] CatalogColumns = [new("Id", 4, 4), new("Type", 3, 2), new("Flags", 4, 4),
        new("Name", 10, 128, false), new("نظام وهمي ثنائي", 9, 40, false)];
    private static readonly Column[] TextColumns = [new("الاسم الكامل", 10, 255, false), new("الرقم الاحصائي", 10, 255, false),
        new("حقل غير معروف", 10, 255, false)];
    private static readonly Column[] MemoColumns = [new("الاسم الكامل", 10, 255, false), new("الملاحظات", 12, 0, false)];
    private static readonly byte[] HeaderMask = Convert.FromHexString(
        "b56f03626108c255eba96772433f009c7a9f90ff809a31c579baed30bcdfcc9d63d9e4c3" +
        "7b42fb8abc4e86fbec375d449cfac65e28e613b68a6054947b36f572dfb177f41343cf" +
        "afb1333461795b92b57c2a05f17c99011b98fd124f4a946c3e60265f95f8d089248567" +
        "c61f2744d2eecf65edff07c746a178160cede92d62d4");

    private static void Check(bool value, string reason)
    {
        checks++;
        if (!value) throw new Exception("Local Access: " + reason);
    }

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "local-access-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var serial = 0;
        string Write(byte[] bytes, string extension = ".accdb")
        {
            var path = Path.Combine(directory, ++serial + extension); File.WriteAllBytes(path, bytes); return path;
        }
        LocalAccessTableData Read(byte[] bytes)
        {
            var path = Write(bytes); var before = SHA256.HashData(bytes);
            using var reader = LocalAccessReader.Open(path);
            var result = reader.ReadTable(MainName); reader.VerifyUnchanged();
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "successful source remains byte-for-byte unchanged");
            return result;
        }
        void Reject(byte[] bytes, string reason, bool inspect = false)
        {
            var path = Write(bytes); var before = SHA256.HashData(bytes);
            try
            {
                using var reader = LocalAccessReader.Open(path);
                if (inspect)
                {
                    var result = reader.InspectTable(MainName);
                    Check(!result.IsComplete && result.DiagnosticCount > 0, reason + " gives explicit incomplete diagnostics");
                    result.EnsureComplete();
                }
                else _ = reader.ReadTable(MainName);
                throw new Exception("Invalid synthetic Access database accepted: " + reason);
            }
            catch (InvalidDataException) { checks++; }
            Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), reason + " leaves rejected source unchanged");
        }
        byte[] Simple(byte version = 2) => Database(TextColumns,
            [new(Row(TextColumns, Text("  موظف وهمي  "), Text("00123"), Text("")))], version);
        try
        {
            foreach (byte version in new byte[] { 1, 2, 3 })
            {
                var bytes = Database(TextColumns,
                    [new(Row(TextColumns, Text("  موظف وهمي  "), Text("00123"), Text(""))),
                     new(Row(TextColumns, Text("ثانٍ وهمي"), null, Text("صفر ٠ ومسافة ")))], version);
                var path = Write(bytes, version == 1 ? ".mdb" : ".accdb");
                using var reader = LocalAccessReader.Open(path);
                Check(reader.FormatVersion == version && reader.PageCount == 10, "correct supported format and page count");
                Check(reader.SourceSha256 == Convert.ToHexString(SHA256.HashData(bytes)), "source fingerprint covers complete input");
                var tables = reader.UserTables();
                Check(tables.Count == 1 && tables[0].Name == MainName && tables[0].DefinitionPage == 4 && tables[0].DeclaredRowCount == 2,
                    "catalog enumeration with unsupported system binary field projected out");
                var result = reader.ReadTable(MainName);
                Check(result.IsComplete && result.RejectedRowCount == 0 && result.Diagnostics.Count == 0 && result.Rows.Count == 2,
                    "all rows counted before complete result");
                Check(result.Rows[0]["الاسم الكامل"] == "  موظف وهمي  " && result.Rows[0]["الرقم الاحصائي"] == "00123", "exact whitespace and leading zeroes");
                Check(result.Rows[0]["حقل غير معروف"] == "" && result.Rows[1]["الرقم الاحصائي"] is null,
                    "empty text is distinct from null; unknown field preserved");
                Check(result.Rows[1]["حقل غير معروف"] == "صفر ٠ ومسافة ", "Arabic digits and trailing space preserved");
                Check(result.Columns.Select(c => c.Name).SequenceEqual(TextColumns.Select(c => c.Name)) && result.Columns.All(c => c.Type == 10), "ordered metadata includes native type");
                reader.VerifyUnchanged();
                Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "catalog and reads never change database");
                try { _ = reader.ReadTable("جدول وهمي مفقود"); throw new Exception("Missing table accepted"); }
                catch (InvalidDataException) { checks++; }

                // Exercise the real reader-to-preview-to-store route, not only model fixtures.
                using var store = new LocalPayrollStore(Path.Combine(directory, "integrated-store-" + version));
                var review = LocalStaffAccessImport.Read(path, MainName, store.GetStatus().RevisionId, reader.SourceSha256);
                Check(review.Problems.Count == 0 && review.Members.Count == 2, "full Access import preview uses every parsed row");
                Check(store.ImportReviewedStaff(review, false).Changed && store.GetStaff().Count == 2, "reviewed Access rows commit atomically");
                var imported = LocalStaff.Fingerprint(store.GetStaff());
                Check(imported == LocalStaff.Fingerprint(review.Members), "stored staff retains full reviewed details and raw columns");
                var repeat = LocalStaffAccessImport.Read(path, MainName, store.GetStatus().RevisionId, reader.SourceSha256);
                Check(!store.ImportReviewedStaff(repeat, true).Changed, "repeated complete Access import remains a no-op");
                var backup = Path.Combine(directory, "integrated-" + version + ".salarybackup");
                var backupInfo = BackupService.Create(store, backup);
                using var restored = new LocalPayrollStore(BackupService.RestoreAsNewWorkspace(backup, Path.Combine(directory, "integrated-restored-" + version), backupInfo.Digest));
                Check(LocalStaff.Fingerprint(restored.GetStaff()) == imported, "full Access staff survives native backup and restore");
                var interchange = Path.Combine(directory, "integrated-" + version + ".zip");
                IphoneInteropService.Export(store, interchange);
                using var exchanged = new LocalPayrollStore(IphoneInteropService.ImportNewWorkspace(interchange, Path.Combine(directory, "integrated-exchanged-" + version), IphoneInteropService.Inspect(interchange).Digest));
                Check(LocalStaff.Fingerprint(exchanged.GetStaff()) == imported, "full Access staff survives synthetic iPhone-format interchange");
                Check(bytes.SequenceEqual(File.ReadAllBytes(path)), "complete import and both backup routes leave Access bytes unchanged");
            }

            var filtered = Simple();
            var catalogRows = new StoredRow[] {
                CatalogRow(MainName, 4), CatalogRow("MSysSynthetic", 999), CatalogRow("مخفي وهمي", 998, flags: 2),
                CatalogRow("نظام وهمي", 997, flags: int.MinValue), CatalogRow("مرتبط وهمي", 996, type: 6),
                CatalogRow("استعلام وهمي", 995, type: 5)
            };
            SetCatalog(filtered, catalogRows);
            Check(Read(filtered).Rows.Count == 1, "system, hidden, linked tables and queries are not enumerated or opened");
            var duplicatedCatalog = Simple(); SetCatalog(duplicatedCatalog, [CatalogRow(MainName, 4), CatalogRow(MainName, 4)]);
            Reject(duplicatedCatalog, "duplicate catalog identities");
            var aliasedCatalog = Simple(); SetCatalog(aliasedCatalog, [CatalogRow(MainName, 4), CatalogRow("اسم جدول ثان وهمي", 4)]);
            Reject(aliasedCatalog, "different catalog names refer to same table");
            var invalidUserPage = Simple(); SetCatalog(invalidUserPage, [CatalogRow(MainName, 2)]);
            Reject(invalidUserPage, "user table points at system definition");

            var numericColumns = new Column[] { new("منطقي", 1, 0), new("بايت", 2, 1), new("قصير", 3, 2), new("طويل", 4, 4),
                new("مبلغ", 5, 8), new("عشري مفرد", 6, 4), new("عشري مزدوج", 7, 8), new("تاريخ", 8, 8), new("نص", 10, 255, false) };
            var stamp = new DateTime(2026, 11, 5, 12, 0, 0);
            var days = (stamp - new DateTime(1899, 12, 30)).TotalDays;
            var numericRow = Row(numericColumns, [1], [255], Int16(short.MinValue), Int32(int.MinValue), Int64(long.MaxValue),
                Int32(BitConverter.SingleToInt32Bits(1.25f)), Double(1.0 / 3), Double(days), Text(" 001 "));
            var numbers = Read(Database(numericColumns, [new(numericRow)])).Rows[0];
            Check(numbers["منطقي"] == "true" && numbers["بايت"] == "255" && numbers["قصير"] == "-32768" && numbers["طويل"] == "-2147483648", "Boolean and signed numeric native types");
            Check(numbers["مبلغ"] == "922337203685477.5807", "Currency retains all 64-bit precision without a double conversion");
            Check(numbers["عشري مفرد"] == "1.25" && numbers["عشري مزدوج"] == "0.3333333333333333", "round-trip invariant floating point format");
            Check(numbers["تاريخ"]!.StartsWith("2026-11-05T12:00:00", StringComparison.Ordinal), "date retains time without timezone shift");
            var fixedColumns = new Column[] { new("رقم", 4, 4), new("صحيح", 1, 0) };
            var fixedData = Read(Database(fixedColumns, [new(Row(fixedColumns, Int32(7), [0])), new(Row(fixedColumns, null, [1]))]));
            Check(fixedData.Rows[0]["رقم"] == "7" && fixedData.Rows[0]["صحيح"] == "false" && fixedData.Rows[1]["رقم"] is null, "fixed-only rows do not invent variable footer");
            var dateColumns = new Column[] { new("يوم", 8, 8) };
            Check(Read(Database(dateColumns, [new(Row(dateColumns, Double(0)))])).Rows[0]["يوم"] == "1899-12-30", "date-only value uses Access epoch");

            var compressed = new List<byte> { 0xff, 0xfe, (byte)' ', (byte)'0', (byte)'0', (byte)'1', (byte)' ', 0 };
            compressed.AddRange(Text("اختبار")); compressed.AddRange([0, (byte)'Z', (byte)' ']);
            Check(LocalAccessReader.DecodeText(compressed.ToArray()) == " 001 اختبارZ ", "compressed text switches between ASCII and Arabic");
            Check(LocalAccessReader.DecodeText(Text("\0  عربي 😀 \0")) == "\0  عربي 😀 \0", "valid UTF16 including NUL and surrogate pair preserved");
            foreach (var badText in new byte[][] { [0x41], [0x00, 0xd8], [0xff, 0xfe, 0, 0x41] })
            {
                try { _ = LocalAccessReader.DecodeText(badText); throw new Exception("Malformed text accepted"); }
                catch (InvalidDataException) { checks++; }
            }
            Check(Read(Database(TextColumns, [new(Row(TextColumns, compressed.ToArray(), Text("0"), null))])).Rows[0]["الاسم الكامل"] == " 001 اختبارZ ", "compressed values travel full reader path");

            var memoText = Text("  مذكرة وهمية\r\nالسطر الثاني 😀  ");
            var inline = Memo(memoText.Length, 0x80000000, extra: memoText);
            Check(Read(Database(MemoColumns, [new(Row(MemoColumns, Text("وهمي"), inline))])).Rows[0]["الملاحظات"] == Encoding.Unicode.GetString(memoText), "inline memo exact text");
            var external = Database(MemoColumns, [new(Row(MemoColumns, Text("وهمي"), Memo(memoText.Length, 0x40000000, 6)))]);
            LongValuePage(external, 6, new(memoText));
            Check(Read(external).Rows[0]["الملاحظات"] == Encoding.Unicode.GetString(memoText), "single-page external memo");
            var chained = Database(MemoColumns, [new(Row(MemoColumns, Text("وهمي"), Memo(memoText.Length, 0, 6)))]);
            var split = 13;
            LongValuePage(chained, 6, new([.. Pointer(7), .. memoText[..split]]));
            LongValuePage(chained, 7, new([.. Pointer(0), .. memoText[split..]]));
            Check(Read(chained).Rows[0]["الملاحظات"] == Encoding.Unicode.GetString(memoText), "memo chained across odd byte boundary decoded only after complete assembly");
            Check(Read(Database(MemoColumns, [new(Row(MemoColumns, Text("وهمي"), []))])).Rows[0]["الملاحظات"] == "", "empty memo distinct from null");

            var normal = Row(TextColumns, Text("موظف وهمي منقول"), Text("001"), null);
            var moved = Database(TextColumns, [new(Pointer(5, 1), 0x4000), new(normal, 0x8000)]);
            var movedData = Read(moved);
            Check(movedData.Rows.Count == 1 && movedData.ForwardedRowCount == 1 && movedData.DeletedRowCount == 0, "forwarded record read once; physical target is not a deleted employee");
            var deleted = Database(TextColumns, [new(normal), new(normal, 0xc000)]);
            var deletedData = Read(deleted);
            Check(deletedData.Rows.Count == 1 && deletedData.DeletedRowCount == 1 && deletedData.IsComplete, "legitimately deleted rows counted and excluded");
            Check(Read(Database(TextColumns, [])).Rows.Count == 0, "empty table is explicit, not missing data");

            var wideColumns = Enumerable.Range(0, 70).Select(i => new Column("حقل وهمي طويل للاختبار " + i, 10, 255, false)).ToArray();
            var wide = Database(wideColumns, [new(Row(wideColumns, wideColumns.Select((_, i) => (byte[]?)Text("قيمة " + i)).ToArray()))]);
            Check(Read(wide).Columns.Count == 70, "multi-page chained table definition");

            foreach (byte version in new byte[] { 0, 4, 5, 6, 255 })
            {
                var bytes = Simple(); bytes[0x14] = version; Reject(bytes, "unsupported format " + version);
            }
            var subversion = Simple(3); subversion[0x15] = 1; Reject(subversion, "unsupported format feature subversion");
            var signature = Simple(); signature[4] = (byte)'X'; Reject(signature, "wrong signature");
            var headerKind = Simple(); headerKind[0] = 1; Reject(headerKind, "wrong root page type");
            Reject(Simple()[..^1], "truncated page");
            Reject(new byte[PageSize * 2], "too short database");
            foreach (byte version in new byte[] { 1, 2, 3 })
            {
                var encrypted = Simple(version); encrypted[0x3e] ^= 1; Reject(encrypted, "encoding key on version " + version);
                var password = Simple(version); password[0x42] ^= 1; Reject(password, "password-protected version " + version);
            }
            var tooMany = Simple(); Put32(tooMany, 4 * PageSize + 0x10, LocalAccessReader.MaximumTableRows + 1); Reject(tooMany, "declared row limit");
            var manyCells = Database(Enumerable.Range(0, 100).Select(i => new Column("c" + i, 4, 4)).ToArray(), [], declared: 100_001);
            Reject(manyCells, "declared cell limit");
            var cycleDefinition = Simple(); Put32(cycleDefinition, 4 * PageSize + 4, 4); Reject(cycleDefinition, "cyclic definition chain");
            var brokenDefinition = Simple(); Put32(brokenDefinition, 4 * PageSize + 4, 9999); Reject(brokenDefinition, "definition page out of range");
            var wrongContinuation = Simple(); Put32(wrongContinuation, 4 * PageSize + 4, 5); Reject(wrongContinuation, "wrong continuation page kind");
            var badIndexes = Simple(); Put32(badIndexes, 4 * PageSize + 0x33, uint.MaxValue); Reject(badIndexes, "unbounded index count");
            var duplicateColumns = new Column[] { new("same", 4, 4), new("SAME", 4, 4) };
            Reject(Database(duplicateColumns, [new(Row(duplicateColumns, Int32(1), Int32(2)))]), "case-insensitive duplicate field names");
            var badColumnName = Simple(); var nameOffset = 4 * PageSize + 0x3f + 12 + TextColumns.Length * 25;
            Put16(badColumnName, nameOffset, 3); Reject(badColumnName, "odd column name byte length");
            foreach (byte type in new byte[] { 9, 11, 15, 16, 18, 255 })
            {
                var unsupported = new Column[] { new("نوع وهمي غير مدعوم", type, 4) };
                Reject(Database(unsupported, [new(Row(unsupported, (byte[]?)null))]), "unsupported native column type " + type + " even if null");
            }
            var calculated = new Column[] { new("محسوب وهمي", 4, 4, true, 0xc0) };
            Reject(Database(calculated, [new(Row(calculated, Int32(4)))]), "calculated column rejected rather than misinterpreted");

            var mismatched = Simple(); Put32(mismatched, 4 * PageSize + 0x10, 2); Reject(mismatched, "declared count exceeds readable count", true);
            var rowCount = Simple(); Put16(rowCount, 5 * PageSize + 12, 65535); Reject(rowCount, "impossible page row count", true);
            var corruptPointer = Simple(); Put16(corruptPointer, 5 * PageSize + 14, 7); Reject(corruptPointer, "row starts in page directory", true);
            var oddValue = Database(TextColumns, [new(Row(TextColumns, [0x41], Text("1"), null))]); Reject(oddValue, "odd UTF16 value", true);
            var badOffsets = Row(TextColumns, Text("وهمي"), Text("1"), null);
            Put16(badOffsets, badOffsets.Length - 1 - 4, 1);
            Reject(Database(TextColumns, [new(badOffsets)]), "variable data overlaps row header", true);
            var missingVariable = Row(TextColumns, Text("وهمي"), Text("1"), null);
            Put16(missingVariable, missingVariable.Length - 3, 65535);
            Reject(Database(TextColumns, [new(missingVariable)]), "unbounded variable count", true);
            var badFixed = Database(fixedColumns, [new(Row(fixedColumns, Int32(1), [0]))]);
            Put16(badFixed, 4 * PageSize + 0x3f + 12 + 21, 4000);
            Reject(badFixed, "fixed field overlaps row footer", true);
            var nonfinite = new Column[] { new("عدد", 7, 8) };
            Reject(Database(nonfinite, [new(Row(nonfinite, Double(double.NaN)))]), "NaN numeric", true);
            Reject(Database(dateColumns, [new(Row(dateColumns, Double(double.PositiveInfinity)))]), "nonfinite date", true);
            Reject(Database(dateColumns, [new(Row(dateColumns, Double(200_000)))]), "date outside supported range", true);

            Reject(Database(TextColumns, [new(Pointer(99), 0x4000)]), "forward target outside database", true);
            Reject(Database(TextColumns, [new(Pointer(3), 0x4000)]), "forward target in other table", true);
            Reject(Database(TextColumns, [new(Pointer(5, 1), 0x4000), new(Pointer(5, 0), 0xc000)]), "cyclic forwarded row", true);
            Reject(Database(TextColumns, [new(Pointer(5, 2), 0x4000), new(Pointer(5, 2), 0x4000), new(normal, 0x8000)]), "duplicate forwarded target", true);
            Reject(Database(TextColumns, [new(Pointer(5, 1), 0x4000), new(normal)]), "forward target is also independently live", true);

            Reject(Database(MemoColumns, [new(Row(MemoColumns, Text("وهمي"), Memo(20, 0x80000000, extra: Text("قصير"))))]), "truncated inline memo", true);
            Reject(Database(MemoColumns, [new(Row(MemoColumns, Text("وهمي"), Memo(LocalAccessReader.MaximumMemoBytes + 1, 0x80000000)))]), "oversized memo", true);
            Reject(Database(MemoColumns, [new(Row(MemoColumns, Text("وهمي"), Memo(0, 0xc0000000)))]), "unsupported memo kind", true);
            var shortMemo = external.ToArray(); LongValuePage(shortMemo, 6, new(Text("قصير"))); Reject(shortMemo, "short external memo", true);
            var wrongMemoPage = external.ToArray(); Put32(wrongMemoPage, 6 * PageSize + 4, 4); Reject(wrongMemoPage, "memo points to employee data page", true);
            var cutChain = chained.ToArray(); LongValuePage(cutChain, 6, new([.. Pointer(0), .. memoText[..split]])); Reject(cutChain, "truncated memo chain", true);
            var cycleMemo = chained.ToArray(); LongValuePage(cycleMemo, 6, new(Pointer(6))); Reject(cycleMemo, "cyclic memo chain", true);
            var invalidCatalog = Simple(); Put32(invalidCatalog, 2 * PageSize + 0x10, 2); Reject(invalidCatalog, "incomplete catalog never enumerates partial tables");

            var mixed = Database(TextColumns, [new(normal), new(Row(TextColumns, [0x41], null, null))]);
            var mixedPath = Write(mixed);
            using (var reader = LocalAccessReader.Open(mixedPath))
            {
                var report = reader.InspectTable(MainName);
                Check(report.Rows.Count == 1 && report.RejectedRowCount == 1 && !report.IsComplete && report.Diagnostics.Any(d => d.PageNumber == 5 && d.RowIndex == 1),
                    "rejected row location and count exposed even with a valid preceding row");
                try { _ = reader.ReadTable(MainName); throw new Exception("Partial read escaped strict path"); }
                catch (InvalidDataException) { checks++; }
            }
            var disposed = LocalAccessReader.Open(Write(Simple())); disposed.Dispose();
            try { disposed.VerifyUnchanged(); throw new Exception("Disposed reader accepted"); }
            catch (ObjectDisposedException) { checks++; }
            var evidenceDirectory = Environment.GetEnvironmentVariable("PAYROLL_ACCESS_EVIDENCE_DIR");
            if (!string.IsNullOrWhiteSpace(evidenceDirectory)) WriteSyntheticEvidence(evidenceDirectory);
            Console.WriteLine($"LocalAccessReaderTests: {checks} assertions passed (synthetic Jet4/ACE pages only).");
        }
        finally { Directory.Delete(directory, true); }
    }

    public static void WriteSyntheticEvidence(string destination)
    {
        Directory.CreateDirectory(destination);
        var normal = Row(TextColumns, Text("  موظف وهمي  "), Text("00123"), null);
        foreach (byte version in new byte[] { 1, 2, 3 })
            File.WriteAllBytes(Path.Combine(destination, "synthetic-version-" + version + (version == 1 ? ".mdb" : ".accdb")),
                Database(TextColumns, [new(normal)], version));
        File.WriteAllBytes(Path.Combine(destination, "synthetic-forwarded.accdb"),
            Database(TextColumns, [new(Pointer(5, 1), 0x4000), new(normal, 0x8000)]));
        var memoText = Text("مذكرة وهمية متسلسلة\r\nلا توجد بيانات حقيقية");
        var memo = Database(MemoColumns, [new(Row(MemoColumns, Text("موظف وهمي"), Memo(memoText.Length, 0, 6)))]);
        LongValuePage(memo, 6, new([.. Pointer(7), .. memoText[..13]]));
        LongValuePage(memo, 7, new([.. Pointer(0), .. memoText[13..]]));
        File.WriteAllBytes(Path.Combine(destination, "synthetic-memo-chain.accdb"), memo);
        var filtered = Database(TextColumns, [new(normal)]);
        SetCatalog(filtered, [CatalogRow(MainName, 4), CatalogRow("MSysSynthetic", 999), CatalogRow("مخفي وهمي", 998, flags: 2),
            CatalogRow("مرتبط وهمي", 997, type: 6)]);
        File.WriteAllBytes(Path.Combine(destination, "synthetic-filtered-catalog.accdb"), filtered);
        var corrupt = Database(TextColumns, [new(normal), new(Row(TextColumns, [0x41], null, null))]);
        File.WriteAllBytes(Path.Combine(destination, "synthetic-rejected-partial.accdb"), corrupt);
        var encrypted = Database(TextColumns, [new(normal)]); encrypted[0x3e] ^= 1;
        File.WriteAllBytes(Path.Combine(destination, "synthetic-rejected-encrypted.accdb"), encrypted);
        File.WriteAllText(Path.Combine(destination, "README.txt"),
            "Synthetic byte-level Jet4/ACE fixtures generated by LocalAccessReaderTests. All names and values are fictional. " +
            "These are parser fixtures, not files validated by Microsoft Access, ACE, mdbtools or Jackcess. " +
            "Tests cover supported page layouts and explicit rejection. No user databases were opened.\n", Encoding.UTF8);
    }

    private static StoredRow CatalogRow(string name, int page, short type = 1, int flags = 0) =>
        new(Row(CatalogColumns, Int32(page), Int16(type), Int32(flags), Text(name), [0xde, 0xad]));
    private static void SetCatalog(byte[] database, StoredRow[] rows)
    {
        Definition(database, 2, CatalogColumns, rows.Length, 9);
        DataPage(database, 3, 2, rows);
    }

    // بُنى بايتات وهمية مستقلة، لا قواعد موظفين فعلية ولا ملفات من المستخدم.
    private static byte[] Database(Column[] columns, StoredRow[] rows, byte version = 2, int? declared = null)
    {
        var bytes = new byte[PageSize * 10];
        bytes[1] = 1; Encoding.ASCII.GetBytes(version == 1 ? "Standard Jet DB\0" : "Standard ACE DB\0").CopyTo(bytes, 4);
        bytes[0x14] = version;
        var date = 45000.5;
        Double(date).CopyTo(bytes, 0x72);
        for (var i = 0; i < 40; i++) bytes[0x42 + i] = (byte)((uint)(int)date >> (8 * (i % 4)));
        for (var i = 0; i < HeaderMask.Length; i++) bytes[0x18 + i] ^= HeaderMask[i];
        Definition(bytes, 2, CatalogColumns, 1, 9);
        DataPage(bytes, 3, 2, [new(Row(CatalogColumns, Int32(4), Int16(1), Int32(0), Text(MainName), [0xde, 0xad]))]);
        Definition(bytes, 4, columns, declared ?? rows.Count(r => (r.Flags & 0x8000) == 0), 8);
        DataPage(bytes, 5, 4, rows);
        return bytes;
    }

    private static void Definition(byte[] database, int page, Column[] columns, int count, int continuation)
    {
        using var buffer = new MemoryStream();
        var prefix = new byte[0x3f + 12];
        prefix[0] = 2; Put32(prefix, 0x10, (uint)count);
        Put16(prefix, 0x29, columns.Length); Put16(prefix, 0x2b, columns.Count(c => !c.Fixed)); Put16(prefix, 0x2d, columns.Length);
        Put32(prefix, 0x33, 1); buffer.Write(prefix);
        var fixedOffset = 0; var varIndex = 0;
        for (var i = 0; i < columns.Length; i++)
        {
            var c = columns[i]; var descriptor = new byte[25];
            descriptor[0] = c.Type; Put16(descriptor, 5, i); Put16(descriptor, 7, c.Fixed ? 0 : varIndex++);
            descriptor[15] = c.Fixed ? (byte)1 : (byte)0; descriptor[16] = c.ExtraFlags;
            Put16(descriptor, 21, c.Fixed ? fixedOffset : 0); Put16(descriptor, 23, c.Length);
            if (c.Fixed) fixedOffset += c.Length;
            buffer.Write(descriptor);
        }
        foreach (var column in columns)
        {
            var name = Text(column.Name); buffer.Write(Int16((short)name.Length)); buffer.Write(name);
        }
        var data = buffer.ToArray();
        if (data.Length <= PageSize) data.CopyTo(database, page * PageSize);
        else
        {
            if (data.Length > 2 * PageSize - 8) throw new Exception("Synthetic definition too large");
            data.AsSpan(0, PageSize).CopyTo(database.AsSpan(page * PageSize));
            Put32(database, page * PageSize + 4, (uint)continuation);
            database[continuation * PageSize] = 2;
            data.AsSpan(PageSize).CopyTo(database.AsSpan(continuation * PageSize + 8));
        }
    }

    private static byte[] Row(Column[] columns, params byte[]?[] values)
    {
        if (values.Length != columns.Length) throw new Exception("Synthetic row value count");
        var mask = new byte[(columns.Length + 7) / 8];
        var fixedLength = columns.Where(c => c.Fixed).Sum(c => c.Length);
        using var buffer = new MemoryStream(); buffer.Write(Int16((short)columns.Length));
        for (var i = 0; i < columns.Length; i++)
        {
            var c = columns[i]; var value = values[i];
            if (value is not null && (c.Type != 1 || value[0] != 0)) mask[i / 8] |= (byte)(1 << (i % 8));
            if (!c.Fixed || c.Type == 1) continue;
            buffer.Write(value ?? new byte[c.Length]);
        }
        var offsets = new List<int> { 2 + fixedLength };
        for (var i = 0; i < columns.Length; i++)
        {
            if (columns[i].Fixed) continue;
            if (values[i] is { } value) buffer.Write(value);
            offsets.Add((int)buffer.Length);
        }
        if (offsets.Count > 1)
        {
            for (var i = offsets.Count - 1; i >= 0; i--) buffer.Write(Int16((short)offsets[i]));
            buffer.Write(Int16((short)(offsets.Count - 1)));
        }
        buffer.Write(mask); return buffer.ToArray();
    }

    private static void DataPage(byte[] database, int pageNumber, uint owner, StoredRow[] rows)
    {
        var page = new byte[PageSize]; page[0] = 1; Put32(page, 4, owner); Put16(page, 12, rows.Length);
        var offset = PageSize;
        for (var i = 0; i < rows.Length; i++)
        {
            offset -= rows[i].Bytes.Length;
            if (offset < 14 + rows.Length * 2) throw new Exception("Synthetic data page full");
            rows[i].Bytes.CopyTo(page, offset); Put16(page, 14 + i * 2, offset | rows[i].Flags);
        }
        page.CopyTo(database, pageNumber * PageSize);
    }
    private static void LongValuePage(byte[] database, int pageNumber, StoredRow row)
    {
        DataPage(database, pageNumber, 0, [row]); "LVAL"u8.CopyTo(database.AsSpan(pageNumber * PageSize + 4));
    }
    private static byte[] Memo(int length, uint kind, int page = 0, int row = 0, byte[]? extra = null)
    {
        var result = new byte[12 + (extra?.Length ?? 0)]; Put32(result, 0, (uint)length | kind);
        Pointer(page, row).CopyTo(result, 4); extra?.CopyTo(result, 12); return result;
    }
    private static byte[] Pointer(int page, int row = 0) => [(byte)row, (byte)page, (byte)(page >> 8), (byte)(page >> 16)];
    private static byte[] Text(string value) => Encoding.Unicode.GetBytes(value);
    private static byte[] Int16(short value) { var result = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(result, value); return result; }
    private static byte[] Int32(int value) { var result = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(result, value); return result; }
    private static byte[] Int64(long value) { var result = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(result, value); return result; }
    private static byte[] Double(double value) => Int64(BitConverter.DoubleToInt64Bits(value));
    private static void Put16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), checked((ushort)value));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}

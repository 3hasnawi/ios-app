namespace SalaryLocal.Core;

public static class LocalStaffAccessImport
{
    public static LocalStaffImportPreview Read(string path, string tableName, string expectedRevision, string expectedSourceDigest)
    {
        using var reader = LocalAccessReader.Open(path);
        if (reader.SourceSha256 != expectedSourceDigest) throw new InvalidDataException("تغيّر ملف Access بعد اختيار الجدول؛ أعد اختياره.");
        var tables = reader.UserTables();
        var data = reader.InspectTable(tableName);
        var diagnostics = data.Diagnostics.Select(d => new LocalStaffProblem(d.RowIndex + 1, $"صفحة {d.PageNumber}: {d.Message}")).ToList();
        LocalStaffTable Convert(LocalAccessTableData table) => new(table.Table.Name, table.Columns.Select(c => new LocalStaffColumn(c.Name, c.Type)).ToArray(), table.Rows, table.DeclaredRowCount, table.IsComplete);
        var lookups = new List<LocalStaffTable>();
        foreach (var column in data.Columns.Where(c => data.IsComplete && c.Type is 2 or 3 or 4))
        {
            string lookup = LocalStaff.LookupAliases.GetValueOrDefault(column.Name, column.Name);
            if (lookup != tableName && tables.Any(t => t.Name == lookup) && lookups.All(t => t.Name != lookup))
            {
                var lookupTable = reader.InspectTable(lookup);
                diagnostics.AddRange(lookupTable.Diagnostics.Select(d => new LocalStaffProblem(d.RowIndex + 1, $"جدول {lookup}، صفحة {d.PageNumber}: {d.Message}")));
                lookups.Add(Convert(lookupTable));
            }
        }
        var preview = LocalStaff.Review(path, reader.SourceSha256, expectedRevision, Convert(data), lookups, diagnostics);
        reader.VerifyUnchanged(); return preview;
    }
}

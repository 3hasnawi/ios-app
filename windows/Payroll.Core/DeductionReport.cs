using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Payroll.Core;

public sealed record DeductionReportRow(string Rank, string Name, decimal Amount, string Note);
public sealed record DeductionReportData(string Month, string Entity, IReadOnlyList<DeductionReportRow> Rows,
    int MissingAmounts, int ZeroAmounts, int StatusRecords, int MissingRanks, decimal Total);

/// <summary>Prepares a single branch's statement using the exact month and branch names in the backup.</summary>
public static class DeductionReport
{
    private static SqliteConnection Open(string directory)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "data.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    public static IReadOnlyList<string> Entities(string directory, string month)
    {
        if (string.IsNullOrWhiteSpace(month)) throw new ArgumentException("اختر الشهر.");
        using var db = Open(directory);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT title FROM deductions WHERE month=$month ORDER BY title";
        cmd.Parameters.AddWithValue("$month", month);
        using var reader = cmd.ExecuteReader();
        var entities = new List<string>();
        while (reader.Read()) entities.Add(reader.GetString(0));
        return entities;
    }

    public static DeductionReportData Create(string directory, string month, string entity)
    {
        if (string.IsNullOrWhiteSpace(month) || string.IsNullOrWhiteSpace(entity)) throw new ArgumentException("اختر الشهر والجهة.");
        using var db = Open(directory);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name,rank,amount,note FROM deductions WHERE month=$month AND title=$entity ORDER BY id";
        cmd.Parameters.AddWithValue("$month", month);
        cmd.Parameters.AddWithValue("$entity", entity);
        using var reader = cmd.ExecuteReader();
        var rows = new List<DeductionReportRow>();
        int missing = 0, zero = 0, status = 0, missingRanks = 0;
        decimal total = 0;
        while (reader.Read())
        {
            var note = reader.GetString(3);
            if (reader.IsDBNull(2))
            {
                if (DeductionComparison.IsStatusOnly(note)) status++; else missing++;
                continue;
            }
            if (!decimal.TryParse(reader.GetString(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                throw new InvalidDataException("مبلغ استقطاع غير صالح؛ لم يُنشأ الكشف.");
            if (amount == 0) { zero++; continue; }
            var rank = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(rank)) missingRanks++;
            total = checked(total + amount);
            rows.Add(new DeductionReportRow(rank, reader.GetString(0), amount, note));
        }
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo("ar-IQ"), true);
        rows.Sort((a, b) =>
        {
            var compare = comparer.Compare(ArabicSearch.Normalize(a.Name), ArabicSearch.Normalize(b.Name));
            return compare != 0 ? compare : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });
        return new DeductionReportData(month, entity, rows, missing, zero, status, missingRanks, total);
    }
}

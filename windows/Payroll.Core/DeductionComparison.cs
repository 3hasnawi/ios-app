using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Payroll.Core;

public sealed record DeductionComparisonRow(string Entity, int PreviousCount, int CurrentCount,
    decimal PreviousAmount, decimal CurrentAmount, int PreviousMissing, int CurrentMissing)
{
    public decimal Difference => CurrentAmount - PreviousAmount;
}

/// <summary>Compares exact entity labels; missing amounts are counted separately, never silently treated as known values.</summary>
public static class DeductionComparison
{
    public static IReadOnlyList<DeductionComparisonRow> Compare(string directory, string previousMonth, string currentMonth)
    {
        if (string.IsNullOrWhiteSpace(previousMonth) || string.IsNullOrWhiteSpace(currentMonth) || previousMonth == currentMonth)
            throw new ArgumentException("اختر شهرين مختلفين للمقارنة.");

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "data.sqlite"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT month, title, amount FROM deductions WHERE month=$previous OR month=$current ORDER BY title";
        cmd.Parameters.AddWithValue("$previous", previousMonth);
        cmd.Parameters.AddWithValue("$current", currentMonth);
        using var reader = cmd.ExecuteReader();
        var groups = new Dictionary<string, Totals>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var month = reader.GetString(0);
            var entity = reader.GetString(1);
            if (!groups.TryGetValue(entity, out var totals)) groups[entity] = totals = new Totals();
            bool current = month == currentMonth;
            if (current) totals.CurrentCount++; else totals.PreviousCount++;
            if (reader.IsDBNull(2))
            {
                if (current) totals.CurrentMissing++; else totals.PreviousMissing++;
                continue;
            }
            if (!decimal.TryParse(reader.GetString(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                throw new InvalidDataException("مبلغ استقطاع غير صالح؛ لم تُحسب المقارنة.");
            if (current) totals.CurrentAmount = checked(totals.CurrentAmount + amount);
            else totals.PreviousAmount = checked(totals.PreviousAmount + amount);
        }
        return groups.OrderBy(x => x.Key, StringComparer.CurrentCulture).Select(x =>
            new DeductionComparisonRow(x.Key, x.Value.PreviousCount, x.Value.CurrentCount, x.Value.PreviousAmount,
                x.Value.CurrentAmount, x.Value.PreviousMissing, x.Value.CurrentMissing)).ToArray();
    }

    private sealed class Totals
    {
        public int PreviousCount, CurrentCount, PreviousMissing, CurrentMissing;
        public decimal PreviousAmount, CurrentAmount;
    }
}

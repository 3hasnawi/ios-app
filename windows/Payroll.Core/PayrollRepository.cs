using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Payroll.Core;

public sealed record PayrollRow(string Month, string Name, string Rank, string Place, string? Amount, string Note);
public sealed record DatasetInfo(int Employees, int Deductions, int Staff, int Accounts, IReadOnlyList<string> Months);
public sealed record PageResult(IReadOnlyList<PayrollRow> Rows, bool HasMore);

/// Read-only access to an imported dataset. The original ZIP is never changed.
public static class PayrollRepository
{
    private static SqliteConnection Open(string datasetDirectory)
    {
        var path = Path.Combine(datasetDirectory, "data.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public static DatasetInfo GetInfo(string datasetDirectory)
    {
        using var db = Open(datasetDirectory);
        int Count(string table)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}"; // fixed internal table names only
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using var months = db.CreateCommand();
        months.CommandText = "SELECT name FROM months";
        using var reader = months.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        names.Sort((a, b) => MonthOrder.SortKey(b).CompareTo(MonthOrder.SortKey(a)));
        return new DatasetInfo(Count("employees"), Count("deductions"), Count("staff"), Count("accounts"), names);
    }

    public static PageResult GetPage(string datasetDirectory, bool deductions, string? month, string? query, int page, int pageSize = 100)
    {
        if (page < 0 || pageSize is < 1 or > 500 || (query?.Length ?? 0) > 200) throw new ArgumentOutOfRangeException(nameof(page));
        using var db = Open(datasetDirectory);
        db.CreateFunction<string?, string>("arabic_search", ArabicSearch.Normalize, isDeterministic: true);
        using var command = db.CreateCommand();
        var table = deductions ? "deductions" : "employees";
        var field = deductions ? "title" : "workplace";
        var amount = deductions ? "amount" : "net";
        var note = deductions ? "note" : "''";
        command.CommandText = $"SELECT month,name,rank,{field},{amount},{note} FROM {table} WHERE ($month='' OR month=$month) AND ($query='' OR instr(arabic_search(name),$query)>0 OR instr(arabic_search({field}),$query)>0) ORDER BY name, id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$month", month ?? "");
        command.Parameters.AddWithValue("$query", ArabicSearch.Normalize(query));
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        command.Parameters.AddWithValue("$offset", checked((long)page * pageSize));
        using var reader = command.ExecuteReader();
        var rows = new List<PayrollRow>(pageSize + 1);
        while (reader.Read()) rows.Add(new PayrollRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new PageResult(rows, hasMore);
    }
}

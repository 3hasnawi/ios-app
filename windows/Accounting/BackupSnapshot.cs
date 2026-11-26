using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace SalaryDesktop.Core;

// Snapshot is read only. The original backup is never extracted or overwritten.
public sealed record PayrollMonth(string Name, int EmployeeCount, int DeductionCount,double ImportedAt=0, byte[]? Summary=null);
public sealed record Employee(string Month, string Name, string Rank, string Workplace, decimal? Net, byte[]? Items, byte[]? Details, byte[]? RawColumns);
public sealed record Deduction(string Month, string Name, string Title, decimal? Amount, string Note, string Rank);
public sealed record ExecutionAccount(string Entity, string Iban, string Branch, string Swift, string? LocalAccount);
/// CreatedAt بثواني الآيفون (منذ ٢٠٠١) كما في books/index.json؛ يُحفظ كما هو عند إعادة كتابة الفهرس.
public sealed record ArchiveBook(Guid Id, string Title, string Month, int Pages, string? Entity, double? CreatedAt = null);
public sealed record BackupSnapshot(string SourcePath, string SourceDigest, int Version, IReadOnlyList<PayrollMonth> Months,
    IReadOnlyList<Employee> Employees, IReadOnlyList<Deduction> Deductions, int StaffCount,
    IReadOnlyList<ExecutionAccount> Accounts, IReadOnlyList<ArchiveBook> Books, IReadOnlyDictionary<string, byte[]> SettingsPlists)
{
    /// كتب أُضيفت أو استُبدل ملفها في ويندوز: المعرّف ← مسار PDF المحلي (من LocalArchiveStore)؛ الباقي يُقرأ من ملف النسخة.
    public IReadOnlyDictionary<Guid, string>? LocalBookFiles { get; init; }
    /// تاريخ إنشاء النسخة من manifest.json بثواني الآيفون (منذ ٢٠٠١ UTC)؛ null إن غاب أو لم يكن رقماً.
    public double? CreatedAt { get; init; }
    public DateTimeOffset? Created => IosDate(CreatedAt);
    public static DateTimeOffset? IosDate(double? seconds) => seconds is { } value && double.IsFinite(value) && Math.Abs(value) < 4e9 ? new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(value) : null;
}


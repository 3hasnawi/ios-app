using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SalaryDesktop.Core;
using SkiaSharp;

namespace SalaryLocal.Core;

public sealed class LocalLetterPreview
{
    private readonly GroupLetterPlan? group;
    internal LocalLetterPreview(BackupSnapshot source, string id, string month, DateOnly date, GroupLetterSettings settings,
        GroupLetterPlan? group, IndividualLetterPlan? payroll, IndividualEntityPlan? entity, NameList? names, GroupIssuanceReview? issuance)
    { SourceRevision = source.SourceDigest; SourceFingerprint = LocalTransferLetters.SourceFingerprint(source, month); Id = id; Month = month; Date = date; Settings = settings; this.group = group; Payroll = payroll; Entity = entity; Names = names; Issuance = issuance; }
    internal string SourceFingerprint { get; }
    public string SourceRevision { get; }
    public string Id { get; }
    public string Month { get; }
    public DateOnly Date { get; }
    public string Title => LocalTransferLetterSettings.Title(Id);
    public long Total => Group?.Total ?? Payroll?.Total ?? Entity!.Total;
    public string Payer => Group?.Payer ?? Payroll?.Payer ?? Entity!.Payer;
    public string PayerAccount => Group?.PayerAccount ?? Payroll?.PayerAccount ?? Entity!.PayerAccount;
    public string? BeneficiaryAccount => Payroll?.BeneficiaryAccount ?? Entity?.BeneficiaryAccount;
    public GroupLetterSettings Settings { get; }
    // النموذج المشترك يعرض CSV كمصفوفة قابلة للتعديل؛ نعيد نسخة مستقلة عند كل قراءة.
    public GroupLetterPlan? Group => group is null ? null : group with { Csv = group.Csv.ToArray() };
    public IndividualLetterPlan? Payroll { get; }
    public IndividualEntityPlan? Entity { get; }
    public NameList? Names { get; }
    public GroupIssuanceReview? Issuance { get; }
}

/// حزمة مسودة محلية؛ لا تُعد إقرار إصدار أو إرسالاً إلى المصرف.
public sealed class LocalLetterPackage
{
    private readonly IReadOnlyDictionary<string, byte[]> files;
    internal LocalLetterPackage(LocalLetterPreview preview, IReadOnlyDictionary<string, byte[]> content)
    {
        Preview = preview; files = new ReadOnlyDictionary<string, byte[]>(content.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal));
        FileNames = Array.AsReadOnly(files.Keys.ToArray());
    }
    public LocalLetterPreview Preview { get; }
    public IReadOnlyList<string> FileNames { get; }
    public byte[] ReadFile(string name) => files.TryGetValue(name, out var bytes) ? bytes.ToArray() : throw new InvalidDataException("ملف المسودة غير موجود.");
    internal byte[] Zip()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var file in files) { var entry = zip.CreateEntry(file.Key, CompressionLevel.Optimal); using var stream = entry.Open(); stream.Write(file.Value); }
            var manifest = new { version = 1, kind = "local-reviewed-draft", sourceRevision = Preview.SourceRevision, book = Preview.Id,
                month = Preview.Month, csvDate = Preview.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), total = Preview.Total,
                files = files.Select(p => new { name = p.Key, bytes = p.Value.Length, sha256 = Convert.ToHexString(SHA256.HashData(p.Value)) }).ToArray() };
            using var index = zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(index, manifest);
        }
        return output.ToArray();
    }
}

public static partial class LocalTransferLetters
{
    internal static string SourceFingerprint(BackupSnapshot source, string month) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
        source.SourceDigest, source.Version, Month = month, Months = source.Months.Where(m => m.Name == month).ToArray(),
        Employees = source.Employees.Where(e => e.Month == month).ToArray(), Deductions = source.Deductions.Where(d => d.Month == month).ToArray(),
        source.Accounts, Settings = source.SettingsPlists.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()
    })));
    private static BackupSnapshot Freeze(BackupSnapshot source) => source with {
        Months = Array.AsReadOnly(source.Months.ToArray()),
        Employees = Array.AsReadOnly(source.Employees.Select(e => e with { Items = e.Items?.ToArray(), Details = e.Details?.ToArray(), RawColumns = e.RawColumns?.ToArray() }).ToArray()),
        Deductions = Array.AsReadOnly(source.Deductions.ToArray()), Accounts = Array.AsReadOnly(source.Accounts.ToArray()), Books = Array.AsReadOnly(source.Books.ToArray()),
        SettingsPlists = new ReadOnlyDictionary<string, byte[]>(source.SettingsPlists.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal))
    };
    public static void CheckSource(BackupSnapshot source, string month)
    {
        if (source.SourceDigest.Length != 64 || source.SourceDigest.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("لقطة البيانات غير مرتبطة بمراجعة محلية صالحة.");
        var key = MonthNames.SortKey(month); var selected = source.Months.Where(m => m.Name == month).ToArray();
        if (key == 0 || selected.Length != 1 || source.Months.Count(m => MonthNames.SortKey(m.Name) == key) != 1 ||
            source.Employees.Count(e => e.Month == month) != selected[0].EmployeeCount || source.Deductions.Count(d => d.Month == month) != selected[0].DeductionCount)
            throw new InvalidDataException("بيانات الشهر ناقصة أو متكررة؛ أعد تحميل الشهر قبل تجهيز الكتاب.");
    }
    public static LocalLetterPreview Prepare(BackupSnapshot source, string month, string id, string? entryId, DateOnly date)
    {
        source = Freeze(source);
        CheckSource(source, month); _ = LocalTransferLetterSettings.Title(id);
        var settings = GroupLetterSettingsReader.Read(source, id); LocalTransferLetterSettings.Validate(id, settings);
        var references = LetterReferencePack.Read(source); var transfer = TransferPreparation.Prepare(source, month);
        // لا تُلغى مطابقة حساب الدفع مع المرجع حتى في الكتب التي لا تحتوي مستفيداً منفرداً.
        _ = LetterReferencePack.IndividualAccount(references, id, transfer.PayerAccount);
        GroupLetterPlan? group = null; IndividualLetterPlan? payroll = null; IndividualEntityPlan? entity = null; NameList? names = null; GroupIssuanceReview? issuance = null;
        if (LocalTransferLetterSettings.IsGroup(id))
        {
            var category = id == "rafidain_group" ? TransferGroup.Rafidain : TransferGroup.OtherBanks;
            group = GroupLetterPreparation.Prepare(transfer, category, date);
            group = group with { Lines = Array.AsReadOnly(group.Lines.ToArray()), Csv = group.Csv.ToArray() };
            issuance = GroupIssuance.Review(source, transfer, category);
            issuance = issuance with { Lines = Array.AsReadOnly(issuance.Lines.ToArray()), Changes = Array.AsReadOnly(issuance.Changes.ToArray()) };
        }
        else if (LocalTransferLetterSettings.IsEntity(id))
        {
            if (string.IsNullOrWhiteSpace(entryId)) throw new InvalidDataException("اختر جهة الاستقطاع لهذا الكتاب صراحةً.");
            entity = IndividualEntityPreparation.Prepare(source, transfer, id, entryId, references);
            entity = entity with { Rows = Array.AsReadOnly(entity.Rows.ToArray()) };
            if (id != "water") names = new(entryId, entity.Entity, month, EntityNameRows(source, month, entity), entity.Total, null);
        }
        else
        {
            var summary = BankStatementCalculator.Calculate(source, month);
            // الاسمي الناقص يمنع كتاب العقود؛ لا تجاوز يسمح بمبلغ جزئي.
            payroll = IndividualLetterPreparation.Payroll(id, summary, transfer.Payer, transfer.PayerAccount, references, false, source);
            payroll = payroll with { Lines = Array.AsReadOnly(payroll.Lines.ToArray()), Notices = Array.AsReadOnly(payroll.Notices.ToArray()) };
            if (id == "cash_salary")
            {
                var cash = CashSalaryListPreparation.Prepare(source, month);
                if (cash.Total != payroll.Total) throw new InvalidDataException("قائمة الكاش لا تطابق الكتاب.");
                names = new("cash", "رواتب الكاش", month, Array.AsReadOnly(cash.Rows.Select((r, i) => new NameListRow(i + 1, r.Name, r.Rank, r.Workplace, r.Net)).ToArray()), cash.Total, null);
            }
        }
        return new(source, id, month, date, settings, group, payroll, entity, names, issuance);
    }
    internal static IReadOnlyList<NameListRow> EntityNameRows(BackupSnapshot source, string month, IndividualEntityPlan entity)
    {
        string Key(string name) => PayrollAnalysis.Normalize(name).Replace(" ", "", StringComparison.Ordinal);
        var lookup = source.Employees.Where(e => e.Month == month).GroupBy(e => Key(e.Name)).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        return Array.AsReadOnly(entity.Rows.Select((row, index) => {
            var matches = lookup.GetValueOrDefault(Key(row.Name)) ?? [];
            if (matches.Length > 1) throw new InvalidDataException("اسم مكرر في رواتب الشهر؛ راجع هوية المنتسب قبل إنشاء قائمة الكتاب.");
            var employee = matches.SingleOrDefault();
            return new NameListRow(index + 1, row.Name, string.IsNullOrWhiteSpace(row.Rank) ? employee?.Rank ?? "" : row.Rank, employee?.Workplace ?? "", row.Amount);
        }).ToArray());
    }
    internal static void VerifySource(BackupSnapshot source, LocalLetterPreview preview)
    {
        CheckSource(source, preview.Month);
        if (source.SourceDigest != preview.SourceRevision || SourceFingerprint(source, preview.Month) != preview.SourceFingerprint)
            throw new InvalidDataException("المصدر الحالي لا يطابق لقطة مراجعة الكتاب؛ أعد تجهيز المعاينة.");
        // إعادة تنفيذ حواجز المراجع والتصنيف والمبالغ، لا الاعتماد على رقم المراجعة وحده.
        var fresh = Prepare(source, preview.Month, preview.Id, preview.Entity is null ? null : TransferPreparation.Prepare(source, preview.Month).Entries
            .SingleOrDefault(e => e.Title == preview.Entity.Entity && e.Group == TransferGroup.Anbar)?.Id, preview.Date);
        if (fresh.Total != preview.Total || fresh.Payer != preview.Payer || fresh.PayerAccount != preview.PayerAccount || fresh.BeneficiaryAccount != preview.BeneficiaryAccount)
            throw new InvalidDataException("بيانات الكتاب لا تطابق مصادرها الحالية.");
    }
    public static LocalLetterPackage Generate(LocalLetterPreview preview, byte[] sakkalBold, byte[] arial, byte[] arialBold, byte[] logo)
    {
        GroupLetterFont.Validate(sakkalBold);
        // تمنع بدائل الخط الصامتة في القالب القديم. هذه الخطوط لا تدخل المستودع.
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (string.IsNullOrWhiteSpace(fonts) || new[] { "times.ttf", "timesbd.ttf", "arial.ttf", "arialbd.ttf" }.Any(f => !File.Exists(Path.Combine(fonts, f))))
            throw new InvalidDataException("يلزم Windows بخطي Arial وTimes New Roman بوزنيهما؛ لم يُستخدم خط بديل.");
        var required = new[] { (File: "times.ttf", Family: "Times New Roman", Bold: false), (File: "timesbd.ttf", Family: "Times New Roman", Bold: true),
            (File: "arial.ttf", Family: "Arial", Bold: false), (File: "arialbd.ttf", Family: "Arial", Bold: true) };
        var fontBytes = required.ToDictionary(f => f.File, f => File.ReadAllBytes(Path.Combine(fonts, f.File)), StringComparer.Ordinal);
        foreach (var requiredFont in required)
        {
            using var data = SKData.CreateCopy(fontBytes[requiredFont.File]); using var face = SKTypeface.FromData(data);
            if (face is null || !string.Equals(face.FamilyName, requiredFont.Family, StringComparison.OrdinalIgnoreCase) || (face.FontStyle.Weight >= 600) != requiredFont.Bold)
                throw new InvalidDataException("أحد خطوط Windows لا يطابق الخط والوزن المعتمدين للكتاب.");
        }
        if (!arial.AsSpan().SequenceEqual(fontBytes["arial.ttf"]) || !arialBold.AsSpan().SequenceEqual(fontBytes["arialbd.ttf"]))
            throw new InvalidDataException("خط قائمة الأسماء لا يطابق خط Arial المثبت.");
        byte[] letter;
        if (preview.Group is { } group) letter = GroupLetterPdf.Generate(group, preview.Settings, sakkalBold, arial, logo);
        else if (preview.Entity is { } entity) letter = IndividualEntityPdf.Generate(entity, preview.Settings, sakkalBold, logo);
        else
        {
            var payroll = preview.Payroll!; CashSalaryList? cash = preview.Names is { Id: "cash" } names
                ? new(preview.Month, names.Rows.Select(r => new CashSalaryPerson(r.Name, r.Rank, r.Workplace, r.Amount)).ToArray(), names.Total) : null;
            letter = IndividualPayrollPdf.Generate(payroll, preview.Settings, sakkalBold, arial, logo, preview.Settings.FoodContractReference, cash);
        }
        var files = new Dictionary<string, byte[]> { ["مسودة الكتاب.pdf"] = letter };
        if (preview.Group is { } csv) { GroupLetterPreparation.Verify(csv); files["ملف التحويل.csv"] = csv.Csv.ToArray(); }
        if (preview.Names is { } list)
        {
            var namesPdf = NameListPdf.Generate(list, arial, arialBold);
            files["مسودة الكتاب.pdf"] = LocalLetterPdfAssembly.AppendNames(letter, namesPdf);
            files["قائمة الأسماء.pdf"] = namesPdf;
        }
        foreach (var font in fontBytes) if (!File.ReadAllBytes(Path.Combine(fonts, font.Key)).AsSpan().SequenceEqual(font.Value))
            throw new InvalidDataException("تغيّر أحد الخطوط أثناء إعداد الكتاب؛ أعد المعاينة.");
        return new(preview, files);
    }
    internal static void SaveNew(byte[] bytes, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(target), ".zip", StringComparison.OrdinalIgnoreCase) || File.Exists(target)) throw new InvalidDataException("اختر اسم ZIP جديداً؛ لا يُستبدل ملف مصدّر موجود.");
        var temp = Path.Combine(Path.GetDirectoryName(target)!, ".letter-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(bytes); output.Flush(true); }
            using (var input = File.OpenRead(temp)) if (!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new InvalidDataException("فشل التحقق من الحزمة المحفوظة.");
            File.Move(temp, target, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SalaryDesktop.Core;
using SalaryLocal.Core;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

internal static class LocalTransferLetterBatchTests
{
    private const string Month = "تشرين الثاني 2026";
    private static readonly DateOnly Date = new(2026, 11, 5);
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception("Batch: " + message); }
    private static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception("Accepted batch: " + message); }
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "letter-batch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Planning(); Saving(root); PdfAssembly(root); Console.WriteLine($"LocalTransferLetterBatchTests: {checks} assertions passed (synthetic assembly only; Windows letter fonts not exercised)."); }
        finally { Directory.Delete(root, true); }
    }
    private static void Planning()
    {
        var source = LocalTransferLettersTests.Fixture();
        var requests = new LocalLetterRequest[] { new("rafidain_group"), new("cash_salary"), new("tax_social"), new("contracts") };
        var batch = LocalTransferLetters.PrepareBatch(source, Month, requests, Date);
        Check(batch.Letters.Count == 4 && batch.Letters[0].Total == 7 && batch.Letters[1].Total == 1000 && batch.Letters[2].Total == 70 && batch.Letters[3].Total == 142, "all source-specific amounts exact");
        Check(batch.SourceRevision == source.SourceDigest && batch.Month == Month && batch.Date == Date, "one stable revision, month, and CSV date");
        requests[0] = new("other_banks_group"); Check(batch.Letters[0].Id == "rafidain_group", "request array does not mutate prepared batch");
        Reject(() => LocalTransferLetters.PrepareBatch(source, Month, [], Date), "empty choice");
        Reject(() => LocalTransferLetters.PrepareBatch(source, Month, [new("cash_salary"), new("cash_salary")], Date), "duplicate template");
        Reject(() => LocalTransferLetters.PrepareBatch(source, Month, [new("cash_salary", "guessed")], Date), "extraneous entity");
        Reject(() => LocalTransferLetters.PrepareBatch(source, Month, [new("unknown")], Date), "unknown template");
        Reject(() => LocalTransferLetters.PrepareBatch(source, Month, [new("water")], Date), "missing explicit entity");
        Reject(() => LocalTransferLetters.PrepareBatch(source, Month, [new("ramadi", "same"), new("housing", "same")], Date), "same source in two letters");
        Reject(() => LocalTransferLetters.PrepareBatch(source, Month, [new("cash_salary"), new("food_company")], Date), "one unready letter blocks entire batch");
        var mutable = batch.Letters[0].Group!.Csv; Array.Fill(mutable, (byte)0); Check(batch.Letters[0].Group!.Csv.Any(b => b != 0), "plans cannot be externally mutated");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); bool cancelled = false;
        try { LocalTransferLetters.GenerateBatch(batch, [], [], [], [], cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancellation precedes generation");
    }
    private static LocalLetterBatchPackage Package(LocalLetterBatchPreview batch) => new(batch, batch.Letters.Select(p => new LocalLetterPackage(p,
        new Dictionary<string, byte[]> { ["مسودة الكتاب.pdf"] = Pdf("Synthetic " + p.Id, 1), ["synthetic.txt"] = Encoding.UTF8.GetBytes(p.Title) })).ToArray());
    private static void Saving(string root)
    {
        using var store = new LocalPayrollStore(Path.Combine(root, "store"));
        store.ImportDataset(LocalTransferLettersTests.Fixture(), [], new Dictionary<string, byte[]>(), store.GetStatus().RevisionId);
        var source = store.GetSnapshot(Month); var batch = LocalTransferLetters.PrepareBatch(source, Month, [new("rafidain_group"), new("contracts")], Date);
        var package = Package(batch); store.VerifyTransferLetterBatch(batch); var path = Path.Combine(root, "batch.zip");
        store.SaveTransferLetterBatch(package, path);
        using (var zip = ZipFile.OpenRead(path))
        {
            Check(zip.Entries.Count == 5 && zip.GetEntry("01-rafidain_group/مسودة الكتاب.pdf") is not null && zip.GetEntry("02-contracts/مسودة الكتاب.pdf") is not null, "files separated by exact template and order");
            using var index = zip.GetEntry("manifest.json")!.Open(); using var manifest = JsonDocument.Parse(index);
            Check(manifest.RootElement.GetProperty("sourceRevision").GetString() == source.SourceDigest && manifest.RootElement.GetProperty("books").GetArrayLength() == 2, "manifest binds every book to reviewed source");
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                using var input = zip.GetEntry(file.GetProperty("name").GetString()!)!.Open(); using var bytes = new MemoryStream(); input.CopyTo(bytes);
                Check(bytes.Length == file.GetProperty("bytes").GetInt64() && Convert.ToHexString(SHA256.HashData(bytes.ToArray())) == file.GetProperty("sha256").GetString(), "each packaged file verified");
            }
        }
        var hash = SHA256.HashData(File.ReadAllBytes(path)); Reject(() => store.SaveTransferLetterBatch(package, path), "no replacing existing package");
        Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "existing package unchanged");
        var copy = package.Packages[0].ReadFile("synthetic.txt"); Array.Fill(copy, (byte)0); Check(package.Packages[0].ReadFile("synthetic.txt").Any(b => b != 0), "package owns immutable content");
        Reject(() => new LocalLetterBatchPackage(batch, package.Packages.Reverse().ToArray()), "out-of-order packages");
        var forged = source with { Employees = [source.Employees[0] with { Net = 1100 }] };
        var fake = LocalTransferLetters.PrepareBatch(forged, Month, [new("contracts")], Date);
        Reject(() => store.SaveTransferLetterBatch(Package(fake), Path.Combine(root, "forged.zip")), "same-revision forged data");
        store.SaveSettings(new Dictionary<string, byte[]> { ["unrelated"] = source.SettingsPlists["bankStatementAccountName"] }, source.SourceDigest, "اختبار وهمي");
        Reject(() => store.VerifyTransferLetterBatch(batch), "stale preview output");
        Reject(() => store.SaveTransferLetterBatch(package, Path.Combine(root, "stale.zip")), "stale batch save");
        Check(!File.Exists(Path.Combine(root, "forged.zip")) && !File.Exists(Path.Combine(root, "stale.zip")), "rejected exports leave no partial targets");
    }
    private static byte[] Pdf(string label, int count)
    {
        var writer = new PdfDocumentBuilder(); var font = writer.AddStandard14Font(Standard14Font.Courier);
        for (int i = 0; i < count; i++)
        {
            var page = writer.AddPage(595, 842); page.AddText(label + " page " + (i + 1), 14, new PdfPoint(40, 790), font);
            page.DrawRectangle(new PdfPoint(30, 30), 535, 780, 1);
        }
        return writer.Build();
    }
    private static void PdfAssembly(string root)
    {
        var letter = Pdf("Letter 123.45", 1); var names = Pdf("Name list Smith Test 123.45", 3);
        var joined = LocalLetterPdfAssembly.AppendNames(letter, names);
        using (var read = PdfDocument.Open(joined))
        {
            Check(read.NumberOfPages == 4, "one letter plus all three name pages");
            Check(read.GetPage(1).Text.Contains("Letter 123.45", StringComparison.Ordinal) && read.GetPage(4).Text.Contains("page 3", StringComparison.Ordinal), "page order and contents remain");
        }
        Reject(() => LocalLetterPdfAssembly.AppendNames(names, letter), "multi-page letter unexpected");
        Reject(() => LocalLetterPdfAssembly.AppendNames([], names), "empty input");
        Reject(() => LocalLetterPdfAssembly.AppendNames([1, 2, 3], names), "malformed input");
        Reject(() => LocalLetterPdfAssembly.AppendNames(letter, Pdf("huge pages", 600)), "page limit");
        using var bitmap = new SKBitmap(80, 120); using (var canvas = new SKCanvas(bitmap)) { canvas.Clear(SKColors.Yellow); using var paint = new SKPaint { Color = SKColors.Blue }; canvas.DrawRect(5, 5, 30, 40, paint); }
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var sourceImage = LocalArchiveImages.FromBytes(encoded.ToArray(), "synthetic.png"); var imagePdf = LocalArchiveImages.CreatePdf([sourceImage]);
        var mergedImage = LocalLetterPdfAssembly.AppendNames(letter, imagePdf);
        using (var read = PdfDocument.Open(mergedImage)) { var embedded = read.GetPage(2).GetImages().Single(); Check(embedded.WidthInSamples == 80 && embedded.HeightInSamples == 120, "embedded source image dimensions preserved"); }
        string? evidence = Environment.GetEnvironmentVariable("SALARY_LETTER_ASSEMBLY_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence); File.WriteAllBytes(Path.Combine(evidence, "before-letter.pdf"), letter); File.WriteAllBytes(Path.Combine(evidence, "before-names.pdf"), names); File.WriteAllBytes(Path.Combine(evidence, "combined.pdf"), joined);
            File.WriteAllBytes(Path.Combine(evidence, "before-image.pdf"), imagePdf); File.WriteAllBytes(Path.Combine(evidence, "combined-image.pdf"), mergedImage);
        }
    }
}

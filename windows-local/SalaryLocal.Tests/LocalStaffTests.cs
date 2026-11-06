using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using SalaryDesktop.Core;
using SalaryLocal.Core;

internal static class LocalStaffTests
{
    private static int checks;
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    private static void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
    private static LocalStaffColumn[] Columns => [new("رقم_معرف", 4), new("الاسم الكامل", 10), new("اسم الام الكامل", 10), new("الرتبة", 4), new("القسم", 10), new("الرقم الاحصائي", 10), new("الرقم الموحد", 10), new("حقل غير معروف", 10), new("فارغ", 10), new("صفر", 4)];
    private static IReadOnlyDictionary<string, string?> Row(string id, string name, string stat, string unified) => new Dictionary<string, string?> {
        ["رقم_معرف"] = id, ["الاسم الكامل"] = name, ["اسم الام الكامل"] = "أم وهمية", ["الرتبة"] = "1", ["القسم"] = "قسم وهمي", ["الرقم الاحصائي"] = stat, ["الرقم الموحد"] = unified,
        ["حقل غير معروف"] = "  نص أصلي \n محفوظ  ", ["فارغ"] = null, ["صفر"] = "0"
    };
    private static LocalStaffTable Table(IReadOnlyList<IReadOnlyDictionary<string, string?>>? rows = null) { rows ??= [Row("71", "موظف تجريبي ألف", "00017", "U001"), Row("72", "موظف تجريبي باء", "00018", "U002")]; return new("الرئيسي", Columns, rows, rows.Count, true); }
    private static LocalStaffTable Lookup => new("الرتبة", [new("id", 4), new("الرتبة", 10)], [new Dictionary<string, string?> { ["id"] = "1", ["الرتبة"] = "مفوض وهمي" }], 1, true);
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "local-staff-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { Mapping(root); Store(root); UnknownFields(root); MotherSemantics(root); Console.WriteLine($"Local staff: {checks} assertions passed."); }
        finally { Directory.Delete(root, true); }
    }
    private static string Source(string root, string name) { string path = Path.Combine(root, name); File.WriteAllText(path, "Synthetic source token for model/store tests only; not an Access database."); return path; }
    private static void Mapping(string root)
    {
        string file = Source(root, "mapping.accdb"); string digest = LocalStaff.FileDigest(file);
        LocalStaffImportPreview Review(LocalStaffTable table, IReadOnlyList<LocalStaffTable>? lookup = null) => LocalStaff.Review(file, digest, "review-revision", table, lookup ?? [Lookup]);
        var preview = Review(Table()); Check(preview.Problems.Count == 0 && preview.Members.Count == 2, "Complete mapping accepted");
        var renamedSource = LocalStaff.Review(Path.Combine(root, "renamed.accdb"), new string('D', 64), "another-revision", Table(), [Lookup]);
        Check(LocalStaff.Fingerprint(preview.Members) == LocalStaff.Fingerprint(renamedSource.Members), "Unchanged rows deduplicate across different file names and source digests");
        var reordered = Review(Table(Table().Rows.Reverse().ToArray()));
        Check(reordered.Problems.Count == 0 && preview.Members[0].Details!.SequenceEqual(reordered.Members[1].Details!) && LocalStaff.Fingerprint(preview.Members[0]) == LocalStaff.Fingerprint(reordered.Members[1]), "Moving a source row to another ordinal preserves exact row bytes");
        var first = preview.Members[0]; Check(first.Name == "موظف تجريبي ألف" && first.Mother == "ام وهميه" && first.Stat == "00017" && first.Rank == "مفوض وهمي" && first.Workplace == "قسم وهمي", "iPhone fields and lookups mapped");
        var detail = JsonNode.Parse(first.Details!)!.AsArray(); var raw = detail.Last()!["localAccessSource"]!["columns"]!.AsArray();
        Check(raw.Single(c => c!["name"]!.GetValue<string>() == "رقم_معرف")!["value"]!.GetValue<string>() == "71", "Original internal ID preserved");
        Check(raw.Single(c => c!["name"]!.GetValue<string>() == "فارغ")!["value"] is null && raw.Single(c => c!["name"]!.GetValue<string>() == "صفر")!["value"]!.GetValue<string>() == "0", "Null and zero preserved");
        Check(raw.Single(c => c!["name"]!.GetValue<string>() == "حقل غير معروف")!["value"]!.GetValue<string>() == "  نص أصلي \n محفوظ  ", "Unknown raw whitespace preserved");
        Check(StaffRecords.Sections(first).Any(s => s.Fields.Any(f => f.Label == "حقل غير معروف")), "Unknown display fields retained");
        Check(Review(Table() with { Columns = Columns.Select(c => c.Name == "حقل غير معروف" ? c with { Type = 11 } : c).ToArray() }).Problems.Count > 0, "Unsupported OLE column cannot be silently dropped");
        Check(Review(Table() with { IsComplete = false }).Problems.Count > 0, "Partial read rejected");
        Check(Review(Table() with { DeclaredRowCount = 3 }).Problems.Count > 0, "Row count mismatch rejected");
        Check(Review(Table([Row("1", "", "1", "1")])).Problems.Count > 0, "Nameless row not skipped");
        Check(Review(Table([Row("1", "ألف", "1", "1"), Row("1", "باء", "2", "2")])).Problems.Count > 0, "Duplicate source IDs blocked");
        Check(Review(Table([Row("1", "ألف", "1", "1"), Row("2", "باء", "1", "2")])).Problems.Count > 0, "Duplicate stat identity blocked");
        Check(Review(Table([Row("1", "ألف", "١٢٣", "1"), Row("2", "باء", "123", "2")])).Problems.Count > 0, "Arabic and Western digits identify duplicate numbers");
        Check(Review(Table([Row("1", "ألف", "1", "1"), Row("2", "ألف", "2", "2")])).Warnings.Any(w => w.Contains("أسماء متكررة")), "Duplicate names stay separate with warning");
        Check(Review(Table() with { Columns = [.. Columns, new("الاسم الكامل ", 10)] }).Problems.Count > 0, "Normalized column collision blocked");
        Check(Review(Table() with { Columns = Columns.Where(c => c.Name != "الاسم الكامل").Append(new("الاسم", 10)).ToArray() }).Problems.Count > 0, "Driver name is not employee name");
        var malformed = new Dictionary<string, string?>(Row("1", "اختبار", "1", "1")); malformed.Remove("صفر");
        Check(Review(Table([malformed])).Problems.Count > 0, "Missing raw fields rejected");
        Check(Review(Table(), [Lookup with { IsComplete = false }]).Problems.Count > 0, "Partial lookup blocked");
        Check(Review(Table(), [Lookup with { Rows = [new Dictionary<string, string?> { ["id"] = "2", ["الرتبة"] = "أخرى" }] }]).Problems.Count > 0, "Unresolved lookup blocked");
        var ambiguous = new LocalStaffTable("الرتبة", [new("keyA", 4), new("keyB", 4), new("الرتبة", 10)], [new Dictionary<string, string?> { ["keyA"] = "1", ["keyB"] = "2", ["الرتبة"] = "ألف" }, new Dictionary<string, string?> { ["keyA"] = "2", ["keyB"] = "1", ["الرتبة"] = "باء" }], 2, true);
        Check(Review(Table(), [ambiguous]).Problems.Count > 0, "Ambiguous lookup identity blocked");
        var zeroRow = new Dictionary<string, string?>(Row("72", "موظف تجريبي باء", "00018", "U002")) { ["الرتبة"] = "0" };
        var withZero = Table([Row("71", "موظف تجريبي ألف", "00017", "U001"), zeroRow]);
        var zeroConflict = new LocalStaffTable("الرتبة", [new("keyA", 4), new("keyB", 4), new("الرتبة", 10)], [
            new Dictionary<string, string?> { ["keyA"] = "1", ["keyB"] = "1", ["الرتبة"] = "ألف" },
            new Dictionary<string, string?> { ["keyA"] = "0", ["keyB"] = "2", ["الرتبة"] = "باء" },
            new Dictionary<string, string?> { ["keyA"] = "2", ["keyB"] = "0", ["الرتبة"] = "جيم" }
        ], 3, true);
        Check(Review(withZero, [zeroConflict]).Problems.Count > 0, "Tied lookup keys with different zero mappings are rejected");
        Check(Review(withZero, [zeroConflict with { Columns = [zeroConflict.Columns[1], zeroConflict.Columns[0], zeroConflict.Columns[2]] }]).Problems.Count > 0, "Zero ambiguity cannot depend on lookup key column order");
        var zeroBlank = Review(withZero, [Lookup]);
        Check(zeroBlank.Problems.Count == 0 && zeroBlank.Members[1].Rank == "", "Optional zero without lookup definition remains a valid blank");
        var allZeroBlank = Review(Table([zeroRow]), [Lookup]);
        Check(allZeroBlank.Problems.Count == 0 && allZeroBlank.Members.Single().Rank == "", "All optional zero values remain valid blanks");
        var definedZero = new LocalStaffTable("الرتبة", [new("id", 4), new("الرتبة", 10)], [
            new Dictionary<string, string?> { ["id"] = "1", ["الرتبة"] = "ألف" },
            new Dictionary<string, string?> { ["id"] = "0", ["الرتبة"] = "قيمة الصفر المحددة" }
        ], 2, true);
        var zeroMeaning = Review(withZero, [definedZero]);
        Check(zeroMeaning.Problems.Count == 0 && zeroMeaning.Members[1].Rank == "قيمة الصفر المحددة", "Unambiguous defined zero keeps the source lookup meaning");
        var edited = LocalStaff.Edit(first, first.Name, first.Mother, first.Stat, first.Unified, "رتبة معدلة", first.Status, first.Workplace);
        Check(StaffRecords.Sections(edited).Any(s => s.Fields.Any(f => f.Label == "الرتبة" && f.Value == "رتبة معدلة")), "Mirrored detail field updated");
        Check(JsonNode.DeepEquals(detail.Last()!["localAccessSource"], JsonNode.Parse(edited.Details!)!.AsArray().Last()!["localAccessSource"]), "Original raw snapshot unchanged by edit");
        var sameWorkplace = first with { Details = Encoding.UTF8.GetBytes("[{\"title\":\"مكان العمل\",\"fields\":[{\"label\":\"القسم\",\"value\":\"قسم وهمي\"},{\"label\":\"المديرية\",\"value\":\"قسم وهمي\"}]}]") };
        var workplaceEdit = LocalStaff.Edit(sameWorkplace, first.Name, first.Mother, first.Stat, first.Unified, first.Rank, first.Status, "قسم جديد");
        Check(StaffRecords.Sections(workplaceEdit).Single().Fields.Single(f => f.Label == "المديرية").Value == "قسم وهمي", "Changing workplace does not overwrite directorate with same text");
        Reject(() => LocalStaff.Edit(first with { Details = Encoding.UTF8.GetBytes("broken") }, "changed", "", "", "", "", "", ""), "Malformed details should block edits");
    }
    private static void Store(string root)
    {
        string source = Source(root, "store.accdb"), digest = LocalStaff.FileDigest(source);
        using var store = new LocalPayrollStore(Path.Combine(root, "store"));
        string Revision() => store.GetStatus().RevisionId;
        LocalStaffImportPreview Preview() => LocalStaff.Review(source, digest, Revision(), Table(), [Lookup]);
        var initial = Preview(); Check(store.ImportReviewedStaff(initial, false).Changed, "First staff import committed");
        var settings = store.GetSnapshot().SettingsPlists;
        Check(PropertyListParser.Parse(settings["staffCount"]) is NSNumber n && n.ToString() == "2" && PropertyListParser.Parse(settings["staffFileName"]) is NSString name && name.Content == "store", "iPhone staff import summary updated atomically");
        var current = store.GetLocalStaff(); Check(current.Rows.Count == 2 && current.RevisionId == Revision(), "Atomic staff snapshot");
        Reject(() => store.ImportReviewedStaff(initial, true), "Stale import revision accepted");
        Reject(() => store.ImportReviewedStaff(Preview(), false), "Replacement requires explicit choice");
        Check(!store.ImportReviewedStaff(Preview(), true).Changed, "Repeated identical import is no-op");
        var stale = current.Rows[0]; var revision = Revision();
        var changed = stale.Member with { Name = "اسم وهمي معدل", Details = Encoding.UTF8.GetBytes("discard this caller supplied data") };
        store.EditStaffMember(stale, changed, revision);
        Check(store.GetStaff()[0].Name == changed.Name && store.GetStaff()[0].Details!.SequenceEqual(stale.Member.Details!), "Caller cannot overwrite details or raw provenance");
        Reject(() => store.DeleteStaffMember(stale, revision), "Stale delete accepted");
        Reject(() => store.EditStaffMember(stale, changed, Revision()), "Changed target hash accepted");
        current = store.GetLocalStaff();
        Reject(() => store.EditStaffMember(current.Rows[0], current.Rows[0].Member with { Stat = current.Rows[1].Member.Stat }, current.RevisionId), "Identity conflict edit accepted");
        Reject(() => store.AddStaffMember(new("جديد", "", current.Rows[0].Member.Stat, "", "", "", "", null), Revision()), "Identity conflict add accepted");
        Reject(() => store.AddStaffMember(new("", "", "", "", "", "", "", null), Revision()), "Blank name added");
        var history = store.GetHistory().Count;
        Check(!store.EditStaffMember(current.Rows[0], current.Rows[0].Member, Revision()).Changed && history == store.GetHistory().Count, "No-op edit preserves history");
        store.AddStaffMember(new("اسم وهمي ثالث", "", "19", "U003", "", "", "", null), Revision());
        Check(store.GetStaff().Last().Details!.SequenceEqual("[]"u8.ToArray()), "Manually created staff has required iPhone-compatible details JSON");
        current = store.GetLocalStaff(); store.DeleteStaffMember(current.Rows[1], current.RevisionId);
        Check(store.GetStaff().Count == 2 && store.GetHistory().Count == history + 2, "Add and recoverable delete history");
        var fileReview = Preview(); File.AppendAllText(source, "changed source");
        var beforeFailure = Revision(); Reject(() => store.ImportReviewedStaff(fileReview, true), "Changed source accepted");
        Check(Revision() == beforeFailure && store.GetStaff().Count == 2, "Source-change failure atomic");
        File.WriteAllText(source, "Synthetic source token for model/store tests only; not an Access database.");
        var tampered = Preview(); tampered.Members[0].Details![0] ^= 1;
        Reject(() => store.ImportReviewedStaff(tampered, true), "Tampered preview accepted");
        Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == Revision(), "Staff history semantic validation");
        var backup = Path.Combine(root, "staff.salarybackup"); var backupInfo = BackupService.Create(store, backup);
        using var restore = new LocalPayrollStore(BackupService.RestoreAsNewWorkspace(backup, Path.Combine(root, "restore"), backupInfo.Digest));
        Check(restore.GetStatus().RevisionId == Revision() && LocalStaff.Fingerprint(restore.GetStaff()) == LocalStaff.Fingerprint(store.GetStaff()), "Native restore retains current staff and history");
        var output = Path.Combine(root, "staff-out.zip"); IphoneInteropService.Export(store, output); Check(IphoneInteropService.Inspect(output).Staff == 2, "Access staff exports through existing interchange");
    }
    private static void UnknownFields(string root)
    {
        var files = InteropTests.Fixture(); var staff = JsonNode.Parse(files["staff.jsonl"])!.AsObject();
        var details = Encoding.UTF8.GetBytes("[{\"title\":\"وظيفة\",\"futureSection\":900719925474099312345,\"fields\":[{\"label\":\"الرتبة\",\"value\":\"عقود\",\"futureField\":{\"enabled\":true}}]}]");
        staff["details"] = Convert.ToBase64String(details); staff["originalId"] = "ORIGINAL-IDENTITY-A";
        var second = (JsonObject)staff.DeepClone(); second["originalId"] = "ORIGINAL-IDENTITY-B";
        files["staff.jsonl"] = Encoding.UTF8.GetBytes(staff.ToJsonString() + "\n" + second.ToJsonString() + "\n");
        var manifest = JsonNode.Parse(files["manifest.json"])!.AsObject(); manifest["staff"] = 2; files["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        string source = Path.Combine(root, "unknown-staff.zip"); InteropTests.WriteZip(source, files); var original = File.ReadAllBytes(source);
        using var store = new LocalPayrollStore(IphoneInteropService.ImportNewWorkspace(source, Path.Combine(root, "unknown-staff"), IphoneInteropService.Inspect(source).Digest));
        var current = store.GetLocalStaff(); var old = current.Rows[1].Member;
        store.EditStaffMember(current.Rows[1], old with { Rank = "مفوض تجريبي" }, current.RevisionId);
        string output = Path.Combine(root, "unknown-staff-out.zip"); IphoneInteropService.Export(store, output);
        using (var zip = ZipFile.OpenRead(output)) using (var reader = new StreamReader(zip.GetEntry("staff.jsonl")!.Open()))
        {
            var a = JsonNode.Parse(reader.ReadLine()!)!; var b = JsonNode.Parse(reader.ReadLine()!)!;
            Check(a["originalId"]!.GetValue<string>() == "ORIGINAL-IDENTITY-A" && b["originalId"]!.GetValue<string>() == "ORIGINAL-IDENTITY-B", "Duplicate row extras mapped by original position not invented identity");
            Check(b["futureStaff"]!.GetValue<string>() == "محفوظ", "Unknown top-level field preserved");
            var content = Encoding.UTF8.GetString(Convert.FromBase64String(b["details"]!.GetValue<string>()));
            Check(content.Contains("900719925474099312345") && JsonNode.Parse(content)![0]!["fields"]![0]!["futureField"]!["enabled"]!.GetValue<bool>(), "Unknown detail metadata and precise integer preserved");
        }
        current = store.GetLocalStaff(); store.DeleteStaffMember(current.Rows[0], current.RevisionId);
        IphoneInteropService.Export(store, Path.Combine(root, "unknown-staff-deleted.zip"));
        Check(store.GetStaff().Single().Rank == "مفوض تجريبي" && File.ReadAllBytes(source).SequenceEqual(original), "Delete is exact and original file immutable");
        string access = Source(root, "replacement.accdb");
        var preview = LocalStaff.Review(access, LocalStaff.FileDigest(access), store.GetStatus().RevisionId, Table(), [Lookup]);
        var priorMetadata = store.ReadAttachment("interop/metadata.json");
        store.ImportReviewedStaff(preview, true);
        var extras = JsonNode.Parse(store.ReadAttachment("interop/metadata.json"))!["extraFields"]!["staff.jsonl"]!.AsObject();
        Check(extras.Count == 0 && !priorMetadata.SequenceEqual(store.ReadAttachment("interop/metadata.json")), "Full replacement does not attach previous dataset IDs to new people");
        IphoneInteropService.Export(store, Path.Combine(root, "unknown-staff-replaced.zip"));
        Check(LocalPayrollStore.ValidateDatabase(store.DatabasePath).RevisionId == store.GetStatus().RevisionId, "Unknown metadata edit/delete/replacement history valid");
    }
    private static void MotherSemantics(string root)
    {
        string access = Source(root, "mother-key.accdb");
        var sourceRow = new Dictionary<string, string?>(Row("81", "منتسب وهمي للأم", "M081", "MU081")) { ["اسم الام الكامل"] = "أمل هادي" };
        var preview = LocalStaff.Review(access, LocalStaff.FileDigest(access), "mother-revision", Table([sourceRow]), [Lookup]);
        var member = preview.Members.Single();
        Check(preview.Problems.Count == 0 && member.Mother == "امل هادي", "Access mother stores the normalized iPhone search key");
        Check(LocalStaff.MotherName(member) == "أمل هادي", "Access mother original spelling remains available for display");
        var sourceDetails = JsonNode.Parse(member.Details!)!.AsArray();
        var raw = sourceDetails.Last()!["localAccessSource"]!.DeepClone();
        Check(raw["columns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "اسم الام الكامل")!["value"]!.GetValue<string>() == "أمل هادي", "Raw Access mother spelling is preserved");
        var normalizedExample = LocalStaff.Edit(new("", "", "", "", "", "", "", null), "اسم وهمي", " أَمَل  هُدى فاطمة ٱمنة ", "", "", "", "", "");
        Check(normalizedExample.Mother == "امل هدي فاطمه امنه", "Mother normalization matches Swift hamza, diacritics, alef maqsura, whitespace and ta marbuta");
        Check(LocalStaff.MotherName(normalizedExample) == "أَمَل  هُدى فاطمة ٱمنة", "Manual mother spelling is retained separately from its key");
        var edited = LocalStaff.Edit(member, member.Name, "إسراء هبة", member.Stat, member.Unified, member.Rank, member.Status, member.Workplace);
        Check(edited.Mother == "اسراء هبه" && LocalStaff.MotherName(edited) == "إسراء هبة", "Normalized iPhone mother matches unnormalized Details during edit");
        Check(JsonNode.DeepEquals(raw, JsonNode.Parse(edited.Details!)!.AsArray().Last()!["localAccessSource"]), "Editing mother never changes raw Access provenance");
        Check(edited.Name == member.Name && edited.Stat == member.Stat && edited.Unified == member.Unified && edited.Rank == member.Rank && edited.Status == member.Status && edited.Workplace == member.Workplace, "Mother normalization does not change other top-level fields");
        var unchanged = LocalStaff.Edit(member, member.Name, member.Mother, member.Stat, member.Unified, member.Rank, member.Status, member.Workplace);
        Check(unchanged.Details!.SequenceEqual(member.Details!), "Unchanged normalized mother key does not rewrite original spelling");
        var rankEdited = LocalStaff.Edit(member, member.Name, member.Mother, member.Stat, member.Unified, "رتبة جديدة", member.Status, member.Workplace);
        Check(LocalStaff.MotherName(rankEdited) == "أمل هادي", "Editing another field retains mother's original spelling");
        var spellingOnly = LocalStaff.Edit(member, member.Name, "إمل هادي", member.Stat, member.Unified, member.Rank, member.Status, member.Workplace);
        Check(spellingOnly.Mother == member.Mother && LocalStaff.MotherName(spellingOnly) == "إمل هادي", "Spelling-only mother edit preserves normalized key and updates display name");
        var plainSpelling = LocalStaff.Edit(member, member.Name, member.Mother, member.Stat, member.Unified, member.Rank, member.Status, member.Workplace, exactMotherSpelling: true);
        Check(LocalStaff.MotherName(plainSpelling) == "امل هادي", "Explicit normalized spelling can replace the display spelling");
        var ambiguous = (JsonArray)sourceDetails.DeepClone();
        ambiguous.Add(new JsonObject { ["title"] = "حقل إضافي", ["fields"] = new JsonArray(new JsonObject { ["label"] = "اسم الوالدة", ["value"] = "امل هادي" }) });
        var ambiguousMember = member with { Details = JsonSerializer.SerializeToUtf8Bytes(ambiguous) };
        Reject(() => LocalStaff.Edit(ambiguousMember, member.Name, "إسراء هبة", member.Stat, member.Unified, member.Rank, member.Status, member.Workplace), "Ambiguous normalized mother fields must block editing");
        var unrelated = (JsonArray)sourceDetails.DeepClone();
        unrelated.Add(new JsonObject { ["title"] = "حقل آخر", ["fields"] = new JsonArray(new JsonObject { ["label"] = "ملاحظة", ["value"] = "أمل هادي" }) });
        var unrelatedMember = member with { Details = JsonSerializer.SerializeToUtf8Bytes(unrelated) };
        var motherEdited = LocalStaff.Edit(unrelatedMember, member.Name, "إسراء هبة", member.Stat, member.Unified, member.Rank, member.Status, member.Workplace);
        Check(StaffRecords.Sections(motherEdited).Last().Fields.Single().Value == "أمل هادي", "Mother matching does not rewrite another field with the same text");
        using (var manual = new LocalPayrollStore(Path.Combine(root, "manual-mother")))
        {
            manual.AddStaffMember(new("اسم يدوي وهمي", "آمنة فاطمة", "MM1", "MMU1", "", "", "", null), manual.GetStatus().RevisionId);
            var saved = manual.GetStaff().Single();
            Check(saved.Mother == "امنه فاطمه" && LocalStaff.MotherName(saved) == "آمنة فاطمة", "Store manual add preserves mother display name and normalized key");
            var snapshot = manual.GetLocalStaff(); var target = snapshot.Rows.Single();
            var draft = LocalStaff.Edit(target.Member, target.Member.Name, "إسراء هبة", target.Member.Stat, target.Member.Unified, target.Member.Rank, target.Member.Status, target.Member.Workplace);
            manual.EditStaffMember(target, draft, snapshot.RevisionId, "إسراء هبة");
            Check(manual.GetStaff().Single().Mother == "اسراء هبه" && LocalStaff.MotherName(manual.GetStaff().Single()) == "إسراء هبة", "Store second save pass retains explicitly supplied mother spelling");
            string exported = Path.Combine(root, "manual-mother.zip"); IphoneInteropService.Export(manual, exported);
            using (var zip = ZipFile.OpenRead(exported)) using (var reader = new StreamReader(zip.GetEntry("staff.jsonl")!.Open()))
            {
                var row = JsonNode.Parse(reader.ReadLine()!)!;
                Check(row["mother"]!.GetValue<string>() == "اسراء هبه" && row["mother"]!.GetValue<string>().Contains(PayrollAnalysis.Normalize("إسراء هبة"), StringComparison.Ordinal), "Exported motherKey supports the Swift normalized-search predicate after direct restoration");
            }
            using var roundTrip = new LocalPayrollStore(IphoneInteropService.ImportNewWorkspace(exported, Path.Combine(root, "mother-roundtrip"), IphoneInteropService.Inspect(exported).Digest));
            Check(roundTrip.GetStaff().Single().Mother == "اسراء هبه" && LocalStaff.MotherName(roundTrip.GetStaff().Single()) == "إسراء هبة", "Mother key and display spelling survive interchange roundtrip");
        }
        var files = InteropTests.Fixture(); var iphoneRow = JsonNode.Parse(files["staff.jsonl"])!.AsObject();
        iphoneRow["mother"] = member.Mother; iphoneRow["details"] = Convert.ToBase64String(member.Details!); iphoneRow["originalId"] = "MOTHER-ORIGINAL-ID";
        files["staff.jsonl"] = Encoding.UTF8.GetBytes(iphoneRow.ToJsonString() + "\n");
        string iphonePath = Path.Combine(root, "iphone-mother.zip"); InteropTests.WriteZip(iphonePath, files);
        using var iphoneStore = new LocalPayrollStore(IphoneInteropService.ImportNewWorkspace(iphonePath, Path.Combine(root, "iphone-mother"), IphoneInteropService.Inspect(iphonePath).Digest));
        var iphoneSnapshot = iphoneStore.GetLocalStaff(); var iphoneTarget = iphoneSnapshot.Rows.Single();
        iphoneStore.EditStaffMember(iphoneTarget, iphoneTarget.Member with { Mother = "فاطمة آمنة" }, iphoneSnapshot.RevisionId);
        Check(iphoneStore.GetStaff().Single().Mother == "فاطمه امنه" && LocalStaff.MotherName(iphoneStore.GetStaff().Single()) == "فاطمة آمنة", "Editing restored iPhone row updates normalized key and original-detail field together");
        string iphoneOutput = Path.Combine(root, "iphone-mother-edited.zip"); IphoneInteropService.Export(iphoneStore, iphoneOutput);
        using (var zip = ZipFile.OpenRead(iphoneOutput)) using (var reader = new StreamReader(zip.GetEntry("staff.jsonl")!.Open()))
        {
            var row = JsonNode.Parse(reader.ReadLine()!)!;
            Check(row["originalId"]!.GetValue<string>() == "MOTHER-ORIGINAL-ID" && row["futureStaff"]!.GetValue<string>() == "محفوظ", "Mother edit preserves iPhone original identity and unknown metadata");
            Check(JsonNode.DeepEquals(raw, JsonNode.Parse(Convert.FromBase64String(row["details"]!.GetValue<string>()))!.AsArray().Last()!["localAccessSource"]), "iPhone mother edit/export preserves original Access values");
        }
    }
}

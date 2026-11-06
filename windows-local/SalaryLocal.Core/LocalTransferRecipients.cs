using System.Text.Json;
using System.Text.Json.Nodes;
using SalaryDesktop.Core;
namespace SalaryLocal.Core;

public sealed record LocalRecipientReview(string Revision, string Month, string Book, string? EntryId, string Source, string Original, string Reference, string Current);
public static class LocalTransferRecipients
{
    private static readonly string[] Fields = ["beneficiaryAccount", "beneficiarySource", "beneficiaryOriginal", "beneficiaryReference"];
    public static LocalRecipientReview Review(BackupSnapshot source, string month, string id, string? entryId)
    {
        LocalTransferLetters.CheckSource(source, month); _ = LocalTransferLetterSettings.Title(id);
        if (LocalTransferLetterSettings.IsGroup(id) || id == "rafidain_anbar") throw new InvalidDataException("هذا الكتاب لا يقبل حساب مستفيد مخصصاً.");
        var transfer = TransferPreparation.Prepare(source, month);
        var reference = LetterReferencePack.IndividualAccount(LetterReferencePack.Read(source), id, transfer.PayerAccount)
            ?? throw new InvalidDataException("حساب المستفيد غير موجود في المرجع.");
        var name = id; var original = reference;
        if (LocalTransferLetterSettings.IsEntity(id))
        {
            var entries = transfer.Entries.Where(e => e.Id == entryId).ToArray();
            if (entries.Length != 1 || entries[0].Group != TransferGroup.Anbar || entries[0].Account is null)
                throw new InvalidDataException("اختر جهة منفردة مرتبطة بحسابها المحفوظ.");
            var entry = entries[0]; name = entry.Title;
            original = id == "ramadi" ? TransferPreparation.NormalizeAccount(entry.Account!.Iban) : TransferPreparation.LocalAccount(entry.Account!) ?? "";
        }
        // قراءة صارمة قبل الحفظ حتى لا يخفي تخصيص جديد تلف البيانات القديمة.
        _ = LocalTransferLetterSettings.ReadObject(source, LocalTransferLetterSettings.SettingsKey);
        var current = IndividualAccountCustomization.Read(source, id)?.Value ?? original;
        return new(source.SourceDigest, month, id, entryId, name, original, reference, current);
    }
    public static byte[] Patch(BackupSnapshot source, LocalRecipientReview reviewed, string? chosen)
    {
        if (source.SourceDigest != reviewed.Revision || Review(source, reviewed.Month, reviewed.Book, reviewed.EntryId) != reviewed)
            throw new InvalidDataException("تغيّرت بيانات الحساب أو المرجع؛ أعد المراجعة.");
        var root = LocalTransferLetterSettings.ReadObject(source, LocalTransferLetterSettings.SettingsKey);
        JsonObject Object(JsonObject parent, string key)
        {
            if (!parent.ContainsKey(key)) parent[key] = new JsonObject();
            return parent[key] as JsonObject ?? throw new InvalidDataException("بنية تخصيص الحساب غير مدعومة.");
        }
        var item = Object(Object(root, "individual"), reviewed.Book);
        // الفراغ الصريح يحجب الحساب المشترك دون حذف تخصيصات الكتب الأخرى.
        foreach (var key in Fields) item[key] = "";
        if (chosen is not null)
        {
            var normalized = TransferPreparation.NormalizeAccount(chosen);
            if (normalized.Length == 0) throw new InvalidDataException("أدخل حساب المستفيد صراحة.");
            if (normalized == TransferPreparation.NormalizeAccount(TransferPreparation.Prepare(source, reviewed.Month).PayerAccount))
                throw new InvalidDataException("حساب المستفيد يطابق حساب الدفع.");
            item[Fields[0]] = normalized; item[Fields[1]] = reviewed.Source; item[Fields[2]] = reviewed.Original; item[Fields[3]] = reviewed.Reference;
        }
        var bytes = LocalTransferLetterSettings.DataPlist(JsonSerializer.SerializeToUtf8Bytes(root));
        var candidate = source with { SettingsPlists = new Dictionary<string, byte[]>(source.SettingsPlists) { [LocalTransferLetterSettings.SettingsKey] = bytes } };
        if (chosen is not null) _ = IndividualAccountCustomization.Resolve(candidate, reviewed.Book, reviewed.Source, reviewed.Original, reviewed.Reference);
        return bytes;
    }
}

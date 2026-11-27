using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AnbarPayroll;

public sealed partial class Store
{
    (string Key,byte[] Data)? SharedRefund(Guid batchId)
    {
        if(Active==null)return null;
        using var zip=ZipFile.OpenRead(Archive);
        (string Key,byte[] Data)? found=null;
        Records(zip,"refunds.jsonl",row=>{
            string key=row.GetProperty("id").GetString()!;
            if(key=="draft")return;
            var data=row.GetProperty("contents").GetBytesFromBase64();
            using var json=System.Text.Json.JsonDocument.Parse(data);
            if(json.RootElement.GetProperty("batchID").GetGuid()!=batchId)return;
            if(found!=null)throw new InvalidDataException("توجد دفعتان بهذا المعرف. راجع السجل.");
            found=(key,data);
        });
        return found;
    }

    public string ReadSharedRefund(Guid batchId) =>
        SharedRefund(batchId) is {} found ? Encoding.UTF8.GetString(found.Data) : "";

    public static void ValidateSharedRefund(Guid batchId,string snapshot)
    {
        var data=JsonNode.Parse(snapshot)!.AsObject();
        var batch=DecodeRefund(data);
        if(Guid.Parse(batch.Id)!=batchId || data["letters"]?["rafidain"]==null || data["letters"]?["otherBanks"]==null)
            throw new InvalidDataException("بيانات الدفعة غير مكتملة.");
        _=batch.Groups();
    }

    public void ApplySharedRefund(Guid batchId,string snapshot,string expected)
    {
        ValidateSharedRefund(batchId,snapshot);
        var revision=Active;
        var found=SharedRefund(batchId);
        var digest=found is {} item ? Convert.ToHexStringLower(SHA256.HashData(item.Data)) : "";
        if(digest!=expected)throw new InvalidDataException("تغيرت الدفعة محلياً. أعد المزامنة.");
        var key=found?.Key ?? Guid.NewGuid().ToString().ToUpperInvariant();
        Rewrite(revision,null,null,refunds:new Dictionary<string,byte[]?>{[key]=Encoding.UTF8.GetBytes(snapshot)});
    }
}

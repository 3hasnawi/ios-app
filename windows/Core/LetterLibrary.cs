using System.Text.Json;
using System.Text.Json.Nodes;
using Claunia.PropertyList;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;
public sealed partial class Store
{
    const string LibraryKey="customTransferTemplatesV1", BuiltinKey="transferLetterCustomizations";
    static JsonObject LetterData(A.BackupSnapshot s,string key){
        if(!s.SettingsPlists.TryGetValue(key,out var bytes))return new();
        if(PropertyListParser.Parse(bytes) is not NSData data)throw new InvalidDataException("تعذرت قراءة إعدادات الكتب.");
        return data.Bytes.Length==0?new():JsonNode.Parse(data.Bytes)?.AsObject()??throw new InvalidDataException("تعذرت قراءة قوالب الكتب.");
    }
    public static List<A.LetterTemplate> LetterLibrary(A.BackupSnapshot s){
        var data=LetterData(s,LibraryKey);
        if(data.Count==0)return [];
        if((int?)data["version"]!=1||data["templates"] is not JsonArray array||array.Count>250)throw new InvalidDataException("إصدار مكتبة الكتب غير مدعوم.");
        var result=array.Deserialize<List<A.LetterTemplate>>(A.LetterTemplate.Json)??throw new InvalidDataException();
        foreach(var t in result)t.Validate(false);
        if(result.Select(t=>t.Id).Distinct().Count()!=result.Count)throw new InvalidDataException("معرّف قالب مكرر.");
        return result;
    }
    public void SaveLetterTemplate(A.BackupSnapshot s,string? book,A.LetterTemplate t,bool remove=false, A.IndividualAccountOverride? account=null){
        var key=book==null?LibraryKey:BuiltinKey;
        var root=LetterData(s,key);
        var node=JsonSerializer.SerializeToNode(t,A.LetterTemplate.Json)!.AsObject();
        if(book==null){
            var existing=LetterLibrary(s);
            root["version"]=1;root["templates"]??=new JsonArray();
            var list=root["templates"]!.AsArray();
            var index=existing.FindIndex(x=>x.Id==t.Id);
            if(remove){if(index<0)throw new InvalidDataException("القالب غير موجود.");list.RemoveAt(index);}
            else {
                t.Validate(false);
                if(index<0){if(list.Count>=250)throw new InvalidDataException("بلغ عدد القوالب ٢٥٠.");list.Add(node);}
                else MergeTemplate(list[index]!.AsObject(),node);
            }
        }else{
            t.Validate();
            if(book.EndsWith("_group"))A.GroupLetterPdf.ValidateBodyTemplate(t.Body);else A.IndividualLetterBody.Validate(book,t.Body);
            root["common"]??=new JsonObject();root["individual"]??=new JsonObject();
            var books=root["individual"]!.AsObject();books[book]??=new JsonObject();var item=books[book]!.AsObject();
            item["letterTemplate"]??=new JsonObject();MergeTemplate(item["letterTemplate"]!.AsObject(),node);
            item["bodyMonth"]=t.BodyMonth??"";item["outgoingMonth"]=t.OutgoingMonth??"";
            if(book is "food_company" or "food_tax"){
                if(string.IsNullOrWhiteSpace(t.FoodContractReference)||t.FoodContractReference.Length>200)throw new InvalidDataException("أدخل رقم عقد الإطعام وتاريخه.");
                root["common"]!["foodContractReference"]=t.FoodContractReference;
            }
            if(account!=null){
                item["beneficiaryAccount"]=account.Value;item["beneficiaryOriginal"]=account.Original;item["beneficiaryReference"]=account.Reference;item["beneficiarySource"]=account.Source;
            }
        }
        byte[] payload=JsonSerializer.SerializeToUtf8Bytes(root);
        if(payload.Length>2_000_000)throw new InvalidDataException("حجم قوالب الكتب يتجاوز الحد المدعوم.");
        var plist=Plist(new NSData(payload));
        Rewrite(Path.GetDirectoryName(s.SourcePath),null,new Dictionary<string,byte[]>{{key,plist}},m=>m["contentVersion"]=Math.Max((int?)m["contentVersion"]??5,book==null?9:10));
    }
    static void MergeTemplate(JsonObject target,JsonObject source){
        foreach(var prop in typeof(A.LetterTemplate).GetProperties()){
            string key=JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            if(source.TryGetPropertyValue(key,out var value))target[key]=value?.DeepClone();else target.Remove(key);
        }
    }
}

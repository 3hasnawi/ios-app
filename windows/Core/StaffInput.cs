using System.Globalization;
using System.Text.Json;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;
public static class StaffInput
{
    public static A.StaffMember Member(IReadOnlyDictionary<string,string> row){
        var fields=row.Where(p=>!string.IsNullOrWhiteSpace(p.Value)).Select(p=>new A.StaffField(p.Key,p.Value.Trim())).ToArray();
        var keyed=fields.ToLookup(p=>Arabic.Key(p.Label));
        string Get(params string[] aliases)=>aliases.SelectMany(k=>keyed[Arabic.Key(k)]).FirstOrDefault()?.Value??"";
        string name=Get("الاسم الكامل","الاسم");if(name.Length==0)throw new InvalidDataException("يوجد سجل ملاك بلا اسم.");
        return new(name,Get("اسم الام الكامل","اسم الام","اسم الوالدة"),Get("الرقم الاحصائي"),Get("الرقم الموحد"),Get("الرتبة"),Get("الحالة الوظيفية"),Get("القسم","المديرية","محل العمل"),
            JsonSerializer.SerializeToUtf8Bytes(new[]{new A.StaffSection("بيانات الملاك",fields)},A.LetterTemplate.Json));
    }
}

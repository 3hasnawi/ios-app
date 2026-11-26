using System.Globalization;
using A=SalaryDesktop.Core;

namespace AnbarPayroll;

[Flags]
public enum ImportData { None=0, Salaries=1, Deductions=2, Staff=4, Trust=8 }

public sealed record ImportWorkbook(ImportData Kind,IReadOnlyList<A.StaffMember> Members)
{
    static ImportData Identify(string[] raw)
    {
        var headers=raw.Select(A.PayrollAnalysis.Normalize).ToArray();
        bool Has(params string[] names)=>names.Any(name=>headers.Contains(A.PayrollAnalysis.Normalize(name)));
        var (_,net,amount)=A.PayrollSpreadsheet.MonthlyColumns(headers);
        if(Has("الاسم الكامل","الاسم")&&Has("الشهر")&&Has("السنة")&&Has("المبلغ","صافي الراتب")&&
            (Has("حالة الصرف","تاريخ الصرف") || !Has("جهة التنفيذ","الفرع","اسم الاستقطاع","الاسمي","طريقة الدفع","IBAN","مبلغ الاستقطاع"))){
            if(Has("حالة الصرف","تاريخ الصرف","الحالة"))return ImportData.Trust;
            throw new InvalidDataException("وجدت أعمدة الاسم والمبلغ والشهر والسنة دون ما يميّز نوعها. راجع عناوين الملف وحالة الصرف للأمانات.");
        }
        if(net>=0)return ImportData.Salaries;
        if(amount>=0)return ImportData.Deductions;
        if(Has("الاسمي","مجموع الاستقطاعات","مجموع استقطاعات المصارف","توقيفات تقاعدية","جهة التنفيذ","رقم الاضبارة"))
            throw new InvalidDataException("توجد أعمدة مالية دون صافي راتب أو مبلغ استقطاع. راجع عنوان عمود المبلغ.");
        if(Has("الاسم الكامل","الاسم") && (Has("الرقم الإحصائي","اسم الأم الكامل","اسم الأم","اسم الوالدة","الحالة الوظيفية","الرقم الموحد") ||
            Has("الرتبة") && Has("القسم","المديرية","محل العمل")))
            return ImportData.Staff;
        return ImportData.None;
    }

    public static ImportWorkbook Inspect(FileStream file)
    {
        var kind=ImportData.None;
        var members=new List<A.StaffMember>();
        int totalRows=0;
        using var reader=A.PayrollSpreadsheet.OpenWorkbook(file);
        do{
            int row=0,staffBefore=members.Count;
            bool nonEmpty=false;
            string[]? staffHeaders=null;
            ImportData sheet=ImportData.None;
            while(reader.Read()){
                if(++row>200_000||++totalRows>500_000||reader.FieldCount>500)throw new InvalidDataException("عدد الصفوف أو الأعمدة يتجاوز الحد المدعوم.");
                var values=Enumerable.Range(0,reader.FieldCount).Select(i=>reader.GetValue(i) switch{
                    DateTime date=>date.ToString("yyyy/MM/dd",CultureInfo.InvariantCulture),
                    var cell=>Convert.ToString(cell,CultureInfo.InvariantCulture)?.Trim()??""
                }).ToArray();
                if(values.All(string.IsNullOrWhiteSpace))continue;
                nonEmpty=true;
                if(staffHeaders==null){
                    if(row>20)break;
                    sheet=Identify(values);
                    if(sheet==ImportData.None)continue;
                    kind|=sheet;
                    if(sheet!=ImportData.Staff)break;
                    staffHeaders=values;
                    var keys=values.Where(x=>x.Length>0).Select(Arabic.Key).ToArray();
                    if(keys.Distinct().Count()!=keys.Length)throw new InvalidDataException($"عناوين مكررة في ورقة الملاك «{reader.Name}».");
                    continue;
                }
                var fields=new Dictionary<string,string>();
                for(int i=0;i<values.Length;i++){
                    if(values[i].Length==0)continue;
                    if(i>=staffHeaders.Length||staffHeaders[i].Length==0)throw new InvalidDataException($"قيمة دون عنوان في «{reader.Name}» صف {row}.");
                    fields.Add(staffHeaders[i],values[i]);
                }
                members.Add(StaffInput.Member(fields));
            }
            if(sheet==ImportData.Staff && members.Count==staffBefore)throw new InvalidDataException($"ورقة الملاك «{reader.Name}» بلا سجلات.");
            if(sheet==ImportData.None&&nonEmpty)throw new InvalidDataException($"تعذّر تحديد نوع البيانات من عناوين ورقة «{reader.Name}».");
        }while(reader.NextResult());
        if(kind==ImportData.None)throw new InvalidDataException("لم يُعثر على بيانات معروفة في الملف.");
        if((kind.HasFlag(ImportData.Staff)&&kind!=ImportData.Staff)||(kind.HasFlag(ImportData.Trust)&&kind!=ImportData.Trust))
            throw new InvalidDataException("يجمع الملف أنواعاً تُستبدل بصورة مستقلة. افصل الملاك والأمانات عن بقية الأوراق. يمكن جمع الرواتب والاستقطاعات معاً.");
        return new(kind,members);
    }
}

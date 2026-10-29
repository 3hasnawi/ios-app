namespace SalaryWindows;
internal sealed record SalaryLine(string Label,double Value,bool IsCount=false);
internal sealed record SalaryBankLine(string Bank,string Branch,double? Amount);
internal sealed record SalaryReportData(string Month,string Name,string Rank,string Agency,string Directorate,string Section,List<SalaryLine> Entitlements,double? EntitlementTotal,List<SalaryLine> Deductions,double? DeductionTotal,double? Net,List<(string Label,string Value)> Info,List<SalaryBankLine> Banks,string Notes,bool MissingDetails,List<string> Mismatches)
{
    public static SalaryReportData From(BackupData data,Employee employee,bool hideZeros,bool hidePersonal)
    {
        var slip=SlipData.From(data,employee); var details=PayrollMath.Decode<string>(employee.Details); var items=PayrollMath.Decode<double>(employee.Items);
        string Text(string key) => details.GetValueOrDefault(key)?.Trim() ?? "";
        double? Read(string key) => PayrollMath.ParseNumber(Text(key)) ?? (items.TryGetValue(key,out var v)?v:null);
        double Num(string key) => Read(key) ?? 0;
        string Titled(string baseName,string key) => Text(key).Length==0?baseName:baseName+" "+Text(key);
        var temporaryName=Num("استقطاع مؤقت")==0?Text("اسم استقطاع المؤقته"):"";
        var entitlements=new List<SalaryLine> {
            new("الراتب الاسمي",Num("الاسمي")),new("عدد العلاوات",Num("عدد العلاوات"),true),new("الخطورة",Num("الخطورة")),
            new("الارزاق",Num("الارزاق")),new("الاستثنائية",Num("الاستثنائية")),new(temporaryName.Length==0?"مخصصات مؤقتة":temporaryName,Num("مؤقتة")),
            new(Titled("الشهادة",Text("اسم الشهدة").Length==0?"اسم الشهادة":"اسم الشهدة"),Num("الشهادة")),new("الاطفال",Num("الاطفال")),
            new("الزوجية",Num("الزوجية")),new(Titled("المنصب","اسم المنصب"),Num("منصب")),new(Titled("مخصصات الصنف","اسم الصنف"),Num("مخصصات صنف")),
            new("الجامعية",Num("جامعية")),new("مخصصات النقل",Num("مخصصات نقل")),new(Titled("اللقب العلمي","اسم اللقب العلمي"),Num("لقب علمي")),
            new(Text("اسم المخصصات الاخرى").Length==0?"مخصصات اخرى":Text("اسم المخصصات الاخرى"),Num("مخصصات اخرى")),new("اضافية",Num("اضافية")) };
        var deductions=new List<SalaryLine> {
            new("التوقيفات التقاعدية",Num("توقيفات تقاعدية")),new("الضريبة",Num("الضريبة")),new("الحماية الاجتماعية",Num("حماية الاجتماعية")),
            new("الضمان الصحي",Num("الرعاية الصحية")),new("النشاط الصحي",Num("نشاط صحي")),new("صندوق الشهداء",Num("صندوق الشهداء")),
            new("النشاط الرياضي",Num("نشاط رياضي")),new("التامين الصحي",Num("تامين صحي")),new("التضمين",Num("تضمين")),new("الايراد النهائي",Num("ايراد نهائي")),
            new("رسم الطابع",Num("رسم طابع")),new(Text("اسم الاستقطاع"),Num("مبلغ الاستقطاع")),new(Text("2اسم الاستقطاع"),Num("2مبلغ الاستقطاع")),
            new(Num("استقطاع مؤقت")==0?"":Text("اسم استقطاع المؤقته"),Num("استقطاع مؤقت")),new("استقطاع نسبة",Num("% استقطاع 3")),new("استقطاعات المصارف",Num("مجموع استقطاعات المصارف")) };
        bool missing=details.Count==0;
        if(missing) { entitlements=[];deductions=items.Where(x=>!x.Key.Contains("مجموع")).OrderBy(x=>x.Key).Select(x=>new SalaryLine(x.Key,x.Value)).ToList(); }
        if(hideZeros) { entitlements.RemoveAll(x=>x.Value==0); deductions.RemoveAll(x=>x.Value==0); }
        string Personal(string text) => hidePersonal && text.Length>4?"•••• "+text[^4..]:text;
        var education=Text("اسم الشهدة").Length==0?Text("اسم الشهادة"):Text("اسم الشهدة");
        var info=new List<(string,string)> { ("التحصيل الدراسي",education),("اسم المنصب",Text("اسم المنصب")),("اسم الصنف",Text("اسم الصنف")),
            ("اسم مصرف التوطين",Text("توطين المصرف")),("IBAN",Personal(Text("IBAN"))),("الرقم الموحد",Personal(Text("الرقم الموحد"))),
            ("الرقم الوظيفي",Personal(Text("الرقم الوظيفي"))),("رقم الضمان الصحي",Personal(Text("رقم الضمان الصحي"))) };
        var banks=slip.Lines.Select(x=> { var segments=x.Title.Split('/',2,StringSplitOptions.TrimEntries);return new SalaryBankLine(segments[0],segments.Length>1?segments[1]:"",x.Amount); }).ToList();
        double? entitlementTotal=PayrollMath.ParseNumber(Text("الاستحقاق"));
        double? deductionTotal=PayrollMath.ParseNumber(Text("مجموع الاستقطاعات")) ?? (items.TryGetValue("مجموع الاستقطاعات",out var total)?total:null);
        if(entitlements.Concat(deductions).Any(x=>!double.IsFinite(x.Value)) || (entitlementTotal.HasValue && !double.IsFinite(entitlementTotal.Value)) ||
            (deductionTotal.HasValue && !double.IsFinite(deductionTotal.Value)) || (employee.Net.HasValue && !double.IsFinite(employee.Net.Value)))
            throw new InvalidDataException("يوجد مبلغ غير صالح؛ لم يُنشأ تقرير الراتب.");
        var mismatch=new List<string>();
        if(!missing && entitlementTotal.HasValue && Math.Abs(entitlements.Where(x=>!x.IsCount).Sum(x=>x.Value)-entitlementTotal.Value)>.5) mismatch.Add("مجموع المخصصات لا يساوي الاستحقاق الكلي.");
        if(!missing && deductionTotal.HasValue && Math.Abs(deductions.Sum(x=>x.Value)-deductionTotal.Value)>.5) mismatch.Add("مجموع الاستقطاعات المفصّلة لا يساوي مجموع الاستقطاعات.");
        var notes=string.Join(" — ",new[]{Text("ملاحظات عامه"),Text("ملاحظات")}.Where(x=>x.Length>0));
        return new(employee.Month,employee.Name,slip.Rank,Text("اسم الوكالة"),Text("اسم المديرية"),Text("NO_markaz").Length==0?slip.Workplace:Text("NO_markaz"),
            entitlements,entitlementTotal,deductions,deductionTotal,employee.Net,info,banks,notes,missing,mismatch);
    }
}

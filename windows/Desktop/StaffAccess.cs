using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using A=SalaryDesktop.Core;
namespace AnbarPayroll.Desktop;

// ADODB is supplied by Windows. ACE opens Access files read-only and resolves their original table structure.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class StaffAccess
{
    static void Release(object value){if(Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
    static string Quote(string table)=>"["+table.Replace("]","]]")+"]";
    static string Str(object? value)=>value switch{null or DBNull=>"",DateTime date=>date.ToString("yyyy/MM/dd"),byte[]=>"",_=>Convert.ToString(value,CultureInfo.InvariantCulture)?.Trim()??""};
    public static IReadOnlyList<A.StaffMember> Read(string file){
        dynamic db=Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Connection",true)!)!;
        try{
            var cs=new DbConnectionStringBuilder{["Provider"]="Microsoft.ACE.OLEDB.12.0",["Data Source"]=Path.GetFullPath(file),["Mode"]="Read"};
            try{db.Open(cs.ConnectionString);}catch(COMException error) when((error.ErrorCode & 65535)==3706){throw new InvalidDataException("قراءة ملفات Access تتطلب Microsoft 365 Access Runtime بنواة 64 بت على هذه الحاسبة.",error);}
            var names=new List<string>();dynamic schema=db.OpenSchema(20);
            try{while(!schema.EOF){if(Str(schema.Fields["TABLE_TYPE"].Value)=="TABLE")names.Add(Str(schema.Fields["TABLE_NAME"].Value));schema.MoveNext();}}finally{schema.Close();Release(schema);}
            List<Dictionary<string,string>> ReadTable(string table,bool schemaOnly=false){
                dynamic rs=db.Execute("SELECT "+(schemaOnly?"TOP 1 ":"")+"* FROM "+Quote(table));var result=new List<Dictionary<string,string>>();
                try{
                    int count=rs.Fields.Count;var labels=new string[count];for(int i=0;i<count;i++)labels[i]=rs.Fields[i].Name;
                    if(schemaOnly){result.Add(labels.ToDictionary(x=>x,_=>""));return result;}
                    while(!rs.EOF){
                        Array values=(Array)rs.GetRows(512);
                        if(values.GetLength(1)==0)break;
                        if(result.Count+values.GetLength(1)>500000)throw new InvalidDataException("عدد سجلات الملاك يتجاوز الحد المدعوم.");
                        for(int r=0;r<values.GetLength(1);r++){
                            var row=new Dictionary<string,string>();for(int i=0;i<count;i++)row[labels[i]]=Str(values.GetValue(i,r));
                            result.Add(row);
                        }
                    }
                    return result;
                }finally{rs.Close();Release(rs);}
            }
            var main=names.FirstOrDefault(n=>n=="الرئيسي");
            if(main==null){
                var possible=names.Where(n=>ReadTable(n,true)[0].ContainsKey("الاسم الكامل")).ToArray();
                long Size(string n){dynamic rs=db.Execute("SELECT COUNT(*) FROM "+Quote(n));try{return Convert.ToInt64(rs.Fields[0].Value);}finally{rs.Close();Release(rs);}}
                main=possible.OrderByDescending(Size).FirstOrDefault()??throw new InvalidDataException("لم يُعثر على جدول الملاك الرئيسي.");
            }
            var rows=ReadTable(main);if(rows.Count==0)throw new InvalidDataException("جدول الملاك فارغ.");
            var aliases=new Dictionary<string,string>{{"محافظة السكن","المحافظة"},{"الوكالة او الهيئة","الوكالة"},{"المديرية العامة","المديرية او الهيئة"},{"المنصب الاداري","المناصب"},{"الجهة المنقول اليها","الدائرة المنقول اليها"},{"هل يعمل ضمن مقر المديرية","هل يعمل ضمن المقر"},{"الشهر","شهر الترقية"}};
            foreach(string label in rows[0].Keys.ToArray()){
                string table=aliases.GetValueOrDefault(label,label);
                if(table==main||!names.Contains(table))continue;
                var used=rows.Select(r=>r[label]).Where(v=>v!="").ToHashSet();
                if(used.Count==0||!used.All(v=>long.TryParse(v,out _)))continue;
                var lookup=ReadTable(table);if(lookup.Count==0)continue;
                var columns=lookup[0].Keys;
                string? key=columns.Where(k=>lookup.All(r=>long.TryParse(r[k],out _))&&lookup.Select(r=>r[k]).Distinct().Count()==lookup.Count)
                    .OrderByDescending(k=>lookup.Count(r=>used.Contains(r[k]))).FirstOrDefault();
                string? text=columns.Where(k=>k!=key&&lookup.Any(r=>r[k].Any(char.IsLetter))).OrderByDescending(k=>k==label||k==table).FirstOrDefault();
                if(key==null||text==null)continue;
                var map=lookup.ToDictionary(r=>r[key],r=>r[text]);
                foreach(var row in rows)if(map.TryGetValue(row[label],out var translated)&&translated.Length>0)row[label]=translated;
            }
            return rows.Select(StaffInput.Member).ToArray();
        }finally{try{if(db.State!=0)db.Close();}finally{Release(db);}}
    }
}

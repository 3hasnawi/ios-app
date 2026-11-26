using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AnbarPayroll.Desktop;

static class AccessImportCheck
{
    [SupportedOSPlatform("windows")]
    public static void Run(string folder,Action<bool,string> check)
    {
        dynamic catalog=Activator.CreateInstance(Type.GetTypeFromProgID("ADOX.Catalog",true)!)!;
        string file=Path.Combine(folder,"fictional-staff.accdb");
        try{
            try{catalog.Create("Provider=Microsoft.ACE.OLEDB.12.0;Data Source="+file);}
            catch(COMException e) when((e.ErrorCode&65535)==3706){Console.WriteLine("SKIP Access file test: x64 ACE provider unavailable");return;}
            dynamic db=catalog.ActiveConnection;
            try{
                db.Execute("CREATE TABLE [الرئيسي] ([الاسم الكامل] TEXT(80), [الرقم الاحصائي] TEXT(30), [الرتبة] INTEGER)");
                db.Execute("CREATE TABLE [الرتبة] ([رمز] INTEGER, [الوصف] TEXT(80))");
                db.Execute("INSERT INTO [الرتبة] VALUES (1,'رتبة تجريبية')");
                db.Execute("INSERT INTO [الرئيسي] VALUES ('اسم تجريبي','00021',1)");
            }finally{db.Close();Marshal.FinalReleaseComObject(db);}
            var row=StaffAccess.Read(file).Single();check(row.Stat=="00021"&&row.Rank=="رتبة تجريبية","Access file imports with code lookup and leading zeros");
        }finally{Marshal.FinalReleaseComObject(catalog);}
    }
}

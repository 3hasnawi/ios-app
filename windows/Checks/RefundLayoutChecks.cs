using AnbarPayroll;
using UglyToad.PdfPig;
using A=SalaryDesktop.Core;

internal static class RefundLayoutChecks
{
    public static void Run(string folder)
    {
        Directory.CreateDirectory(folder);
        string dir=Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var fonts=A.LetterFonts.Resolve(File.ReadAllBytes(Path.Combine(dir,"timesbd.ttf")),
            File.ReadAllBytes(Path.Combine(dir,"arialbd.ttf")),File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"IraqEmblem.png")));
        var people=new List<RefundPayee>();
        foreach(var (code,bank) in new[]{("RAFB","الرافدين"),("RSHD","مصرف الرشيد"),("IRIB","المصرف العراقي الإسلامي"),("NBIQ","المصرف الأهلي العراقي"),("IDBQ","مصرف التنمية الدولي للاستثمار والتمويل")}){
            int index=people.Count+1;
            people.Add(new("منتسب تجريبي "+index,"موظف","قسم تجريبي","تشرين الثاني 2026",AccountingTests.Iban(code,index),bank,code+"IQBA123",1000*index));
        }
        var batch=new RefundBatch(Guid.NewGuid().ToString(),"جهة تنفيذ تجريبية",new(2026,11,1),"قسم الحسابات",AccountingTests.Iban("RAFB",100),people);
        var style=new A.GroupLetterSettings("جمهورية العراق\nوزارة الداخلية\nقيادة شرطة محافظة الأنبار\nقسم الحسابات","١‏/٢٢/ح/",
            "معاون مدير تدقيق\nاسم تجريبي\nالمصروفات","العميد\nاسم تجريبي\nمدير الحسابات","لواء الشرطة\nاسم تجريبي\nقائد الشرطة","نسخة منه إلى",null,null,0,null);
        var groups=batch.Groups();
        void Verify(string file,List<RefundBank> banks,A.GroupLetterSettings format){
            var bytes=batch.Letter(banks,format,fonts);
            using var pdf=PdfDocument.Open(bytes);
            if(pdf.NumberOfPages!=1 || pdf.GetPage(1).Letters.Count==0)throw new InvalidDataException(file);
            var text=string.Concat(pdf.GetPage(1).Letters.Select(l=>l.Value));
            foreach(var bank in banks)if(!text.Contains(bank.Bic,StringComparison.Ordinal))throw new InvalidDataException("Missing bank BIC: "+bank.Code);
            File.WriteAllBytes(Path.Combine(folder,file+".pdf"),bytes);
        }
        Verify("rafidain",groups.Where(g=>g.Code=="RAFB").ToList(),style);
        Verify("one-other",groups.Where(g=>g.Code=="RSHD").ToList(),style);
        Verify("multiple-banks",groups.Where(g=>g.Code!="RAFB").ToList(),style);
        Verify("saved-without-signatures",groups.Where(g=>g.Code!="RAFB").ToList(),style with {First="",Second="",Third=""});
        foreach(var bank in groups){
            var text=System.Text.Encoding.UTF8.GetString(batch.Csv(bank));
            if(!bank.People.All(p=>text.Contains(p.Bic+"|"+p.Name+"|"+p.Iban)))throw new InvalidDataException(bank.Code);
        }
        if (Environment.GetEnvironmentVariable("TRIAL_REFUND_SNAPSHOT") is {} path) {
            batch=Store.DecodeRefund(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(path))!.AsObject());
            var synced=new Store(Path.Combine(folder,"saved-refund-sync-"+Guid.NewGuid().ToString("N")));
            var id=Guid.Parse(batch.Id);
            var json=File.ReadAllText(path);
            synced.ApplySharedRefund(id,json,"");
            if(synced.ReadSharedRefund(id)!=json || synced.Refunds().Single().Id!=batch.Id)
                throw new InvalidDataException("Saved refund archive roundtrip");
            var stale=false;
            try{synced.ApplySharedRefund(id,json,"stale");}catch(InvalidDataException){stale=true;}
            if(!stale || synced.ReadSharedRefund(id)!=json)throw new InvalidDataException("Lost concurrent edit");
            groups=batch.Groups();
            if(batch.People.Count!=2 || groups.Count!=2 || groups.Sum(g=>g.Total)!=40000)
                throw new InvalidDataException("Trial refund totals");
            foreach(var group in groups){
                Verify("trial-"+group.Code,[group],batch.LetterStyle(group.Code=="RAFB",style));
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!,group.Code+".csv"),batch.Csv(group));
            }
        }
        Console.WriteLine("PASS individual CSV files, both refund letters, one or multiple other banks, archived blank committee and single-page rendering");
    }
}

using System.IO;
using System.Text.Json;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    static readonly Dictionary<string,string> PinIds = new() {
        [AgencyPage]="entities", [EmployeePage]="employeeDeductions", [HistoryPage]="deductionSearch",
        ["كشف البنك"]="bankStatement", [TransfersName]="transferFiles", [AuditName]="salaryAudit",
        [TrustName]="salaryTrust", [FundingName]="fundingExpenses", [CommandName]="commandDeductions",
        [HealthName]="health", [FundName]="fund", [FoodName]="food"
    };

    // الأحجام من «عادي» فما فوق. نحتفظ بدرجة ويندوز الدقيقة عندما تتقابل درجتان مع حجم واحد على الآيفون.
    static readonly int[] PhoneSizes = [3,4,4,5,6];
    Dictionary<string,string> workPreferences = new();

    void LoadWorkPreferences()
    {
        var revision = store.Active;
        workPreferences = store.ReadWorkPreferences();
        var migration = new Dictionary<string,string>();
        var oldPins = Path.Combine(store.Root,"pins.json");
        var defaults = Cards.Take(4).Select(c=>c.Name).ToList();
        if(workPreferences.TryGetValue("homePinnedWorkItems",out var saved)){
            pins = Names(saved);
        }else if(workPreferences.TryGetValue("homeHiddenWorkReports",out var hidden)){
            var visible = new[]{"entities","food","fund","health","commandDeductions"}.Except(hidden.Split('|'));
            pins = Names(string.Join("|",visible));
        }else{
            pins = defaults;
            if(File.Exists(oldPins)){
                List<string>? legacy = null;
                try { legacy = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(oldPins)); }
                catch(JsonException) { /* الاحتفاظ بالملف القديم مع عرض الاختصارات الافتراضية. */ }
                if(legacy!=null){
                    pins = legacy.Where(PinIds.ContainsKey).Distinct().ToList();
                    migration["homePinnedWorkItems"] = string.Join("|",pins.Select(p=>PinIds[p]));
                }
            }
        }

        var legacyAppearance = Appearance.Read(store.Root);
        bool hasLegacy = File.Exists(Path.Combine(store.Root,"appearance.json"));
        bool hindi = legacyAppearance.Hindi;
        if(workPreferences.TryGetValue("appNumerals",out var digits)) hindi = digits=="arabicIndic";
        else if(hasLegacy) migration["appNumerals"] = hindi?"arabicIndic":"latin";
        int level = legacyAppearance.Level;
        if(int.TryParse(workPreferences.GetValueOrDefault("appTextSize"),out var phoneSize)){
            if(!int.TryParse(workPreferences.GetValueOrDefault("windowsContentTextLevel"),out level)
                || level<0 || level>=PhoneSizes.Length || PhoneSizes[level]!=phoneSize)
                level = phoneSize>=6?4:phoneSize==5?3:phoneSize==4?1:0;
        }else if(hasLegacy){
            migration["windowsContentTextLevel"] = level.ToString();
            migration["appTextSize"] = PhoneSizes[level].ToString();
        }
        var theme=workPreferences.GetValueOrDefault("appAppearance")??legacyAppearance.Theme;
        if(theme is not ("system" or "dark" or "light"))theme="system";
        if(hasLegacy && !workPreferences.ContainsKey("appAppearance"))migration["appAppearance"]=theme;
        if(revision!=null && migration.Count>0) SavePreferences(revision,migration);
        appearance = new(level,hindi,theme);
        appearance.Apply();
    }

    static List<string> Names(string ids)
    {
        var names = PinIds.ToDictionary(pair=>pair.Value,pair=>pair.Key);
        return ids.Split('|').Where(names.ContainsKey).Distinct().Select(id=>names[id]).ToList();
    }

    void SavePreferences(string revision,Dictionary<string,string> changes)
    {
        store.SaveWorkPreferences(revision,changes);
        foreach(var pair in changes)workPreferences[pair.Key]=pair.Value;
    }

    void SavePins(List<string> selected)
    {
        if(store.Active is {} revision){
            // تبقى اختصارات الآيفون الأخرى محفوظة عند تحرير بطاقات ويندوز.
            string existing = workPreferences.GetValueOrDefault("homePinnedWorkItems") ??
                string.Join("|",new[]{"entities","food","fund","health","commandDeductions"}.Except((workPreferences.GetValueOrDefault("homeHiddenWorkReports")??"").Split('|')));
            var ordered = new Queue<string>(selected.Select(name=>PinIds[name]));
            var ids = new List<string>();
            foreach(string id in existing.Split('|',StringSplitOptions.RemoveEmptyEntries)){
                if(!PinIds.ContainsValue(id)) ids.Add(id);
                else if(ordered.TryDequeue(out var next)) ids.Add(next);
            }
            ids.AddRange(ordered);
            SavePreferences(revision,new(){["homePinnedWorkItems"]=string.Join("|",ids)});
        }else{
            Directory.CreateDirectory(store.Root);
            SalaryDesktop.Core.ReportFiles.Save(JsonSerializer.SerializeToUtf8Bytes(selected),Path.Combine(store.Root,"pins.json"),".json");
        }
        pins=selected;
    }

    void SaveAppearance(Appearance next)
    {
        if(store.Active is not {} revision){next.Save(store.Root);return;}
        var changes=new Dictionary<string,string>();
        if(next.Theme!=appearance.Theme)changes["appAppearance"]=next.Theme;
        if(next.Hindi!=appearance.Hindi)changes["appNumerals"]=next.Hindi?"arabicIndic":"latin";
        if(next.Level!=appearance.Level){
            changes["windowsContentTextLevel"]=next.Level.ToString();
            changes["appTextSize"]=PhoneSizes[next.Level].ToString();
        }
        if(changes.Count>0)SavePreferences(revision,changes);
    }
}

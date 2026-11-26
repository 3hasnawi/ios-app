namespace AnbarPayroll;

public sealed record DeductionReviewLine(string Name,string Agency,string Reference,decimal? Before,decimal After,int Copies=1);
public sealed record DeductionCardData(bool Blocked,Dictionary<string,List<DeductionReviewLine>> Rows);

public sealed partial class Store
{
    public DeductionCardData ReviewDeductionEntries(MonthInfo month,MonthInfo? previous)
    {
        List<Deduction> Load(MonthInfo period)=>Find(new(Year:period.Key/100,Month:period.Key%100))
            .Where(d=>d.Month==period.Name && d.Status=="").ToList();
        var current=Load(month);
        var cards=new Dictionary<string,List<DeductionReviewLine>>();
        void Add(string title,DeductionReviewLine row){
            if(!cards.TryGetValue(title,out var list))cards[title]=list=[];
            list.Add(row);
        }
        static (string Person,string Agency,string Reference) Key(Deduction d)=>(Arabic.Match(d.Name),Arabic.Key(d.Agency),Arabic.Key(d.Reference));
        static bool Valid(Deduction d)=>Key(d).Person.Length>0 && Key(d).Agency.Length>0 && d.Amount is >=0;
        foreach(var group in current.Where(Valid).GroupBy(d=>(Key(d),d.Amount)).Where(g=>g.Count()>1)){
            var row=group.First();
            Add("تكرار محتمل في الاستقطاعات",new(row.Name,row.Agency,row.Reference,null,group.Sum(d=>d.Amount!.Value),group.Count()));
        }
        if(previous==null)return new(false,cards);
        var old=Load(previous);
        if(!month.Verified||!previous.Verified||current.Count==0||old.Count==0||!current.All(Valid)||!old.All(Valid))
            return new(true,cards);
        static Dictionary<(string,string,string),(Deduction Row,decimal Total)> Totals(List<Deduction> rows)=>
            rows.Where(d=>d.Amount>0).GroupBy(Key).ToDictionary(g=>g.Key,g=>(g.First(),g.Sum(d=>d.Amount!.Value)));
        var before=Totals(old);var after=Totals(current);
        foreach(var key in after.Keys.Union(before.Keys)){
            var prior=before.GetValueOrDefault(key);var next=after.GetValueOrDefault(key);
            if(prior.Total==next.Total)continue;
            var row=next.Row??prior.Row;
            string title=prior.Total==0?"الاستقطاعات الجديدة":next.Total==0?"الاستقطاعات المتوقفة":"تغيّر مبلغ الاستقطاع (نفس الجهة والإضبارة)";
            Add(title,new(row.Name,row.Agency,row.Reference,prior.Total,next.Total));
        }
        return new(false,cards);
    }
}

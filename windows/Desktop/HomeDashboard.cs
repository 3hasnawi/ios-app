using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    string? homeMonth;
    int homePinPage, homeReviewPage;
    ReviewSection homeSection;

    static Button HomeAction(string title,Action action,string icon="",bool selected=false)
    {
        var button=ButtonFor(title,action,icon,selected);
        button.Padding=new Thickness(7,3,7,3);
        button.Margin=new Thickness(2);
        return button;
    }

    static DockPanel HomeHeading(string title,UIElement actions)
    {
        var row=new DockPanel();
        DockPanel.SetDock(actions,Dock.Right);
        row.Children.Add(actions);
        row.Children.Add(Text(title,16,true));
        return row;
    }

    static StackPanel HomePager(int page,int pages,string section,Action<int> change)
    {
        var bar=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        if(pages<=1)return bar;
        foreach(int step in new[]{-1,1}){
            if(step==1)bar.Children.Add(Text($"{page+1} / {pages}",12));
            string label=(step<0?"السابق":"التالي")+" · "+section;
            var button=HomeAction(label,()=>change(page+step));
            button.Content=step<0?"❯":"❮";button.ToolTip=label;
            button.IsEnabled=page+step>=0 && page+step<pages;
            bar.Children.Add(button);
        }
        return bar;
    }

    void OpenHomeDirectory(Button anchor)
    {
        var menu=new ContextMenu{PlacementTarget=anchor,Placement=PlacementMode.Bottom,FlowDirection=FlowDirection.RightToLeft};
        var edit=new MenuItem{Header="تخصيص كشوفي المثبتة"};
        edit.Click+=(_,_)=>EditPins();menu.Items.Add(edit);menu.Items.Add(new Separator());
        foreach(var card in Cards){
            var item=new MenuItem{Header=card.Name,ToolTip=card.Desc};
            item.Click+=(_,_)=>Activate(card.Name);menu.Items.Add(item);
        }
        menu.IsOpen=true;
    }

    async Task Home()
    {
        var month=SelectedMonth;
        var revision=store.Active;
        if(homeMonth!=month?.Name){
            homeMonth=month?.Name;homePinPage=homeReviewPage=0;homeSection=ReviewSection.Payroll;
        }
        double scale=1+appearance.Level*.05;
        var board=new Grid();
        board.RowDefinitions.Add(new(){Height=GridLength.Auto});
        board.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        board.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var balance=Text("إجمالي الاستقطاعات　—",15,true);
        var count=Text("عدد القيود　—",14);
        var summary=Bar(balance,count);
        if(revision==null)summary.Children.Add(HomeAction("استيراد نسخة",()=>_=ImportBackup(),"\uE8E5"));
        var banner=Box(summary,"SystemFillColorSuccessBackgroundBrush");banner.Padding=new Thickness(9,3,9,3);
        board.Children.Add(banner);

        var pinArea=new Grid();
        pinArea.RowDefinitions.Add(new(){Height=GridLength.Auto});
        pinArea.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var pinActions=new StackPanel{Orientation=Orientation.Horizontal};
        var directory=HomeAction("دليل الكشوف وتخصيصها",()=>{});
        directory.Click+=(_,_)=>OpenHomeDirectory(directory);
        pinActions.Children.Add(directory);
        pinArea.Children.Add(HomeHeading("كشوفي المثبتة",pinActions));
        var tileArea=new Grid();
        Grid.SetRow(tileArea,1);pinArea.Children.Add(tileArea);
        Grid.SetRow(pinArea,1);board.Children.Add(pinArea);
        var reviews=new StackPanel();
        var footer=Box(reviews);footer.Padding=new Thickness(6,3,6,3);
        Grid.SetRow(footer,2);board.Children.Add(footer);

        IReadOnlyList<HomeReview> all=[];
        string? message=month is {Key:>0}?"جارٍ تحميل حالات الشهر…":"تظهر حالات المراجعة بعد استيراد البيانات.";
        int pinCapacity=0,reviewCapacity=0;bool compact=false;
        void DrawPins(){
            const int columns=2;
            int pages=Math.Max(1,(pins.Count+pinCapacity-1)/pinCapacity);
            homePinPage=Math.Clamp(homePinPage,0,pages-1);
            if(pinActions.Children.Count>1)pinActions.Children.RemoveAt(1);
            pinActions.Children.Add(HomePager(homePinPage,pages,"الكشوف",p=>{homePinPage=p;DrawPins();}));
            tileArea.Children.Clear();
            if(pins.Count==0){tileArea.Children.Add(Text("اختر كشوفك من دليل الكشوف وتخصيصها.",14));return;}
            var tiles=new UniformGrid{Columns=columns,VerticalAlignment=VerticalAlignment.Top};
            foreach(var name in pins.Skip(homePinPage*pinCapacity).Take(pinCapacity)){
                var card=Cards.First(c=>c.Name==name);
                var button=HomeAction(name,()=>Activate(name));
                button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
                var label=Text(name,14,true);
                label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;
                var glyph=Glyph(card.Icon,"#29644E");glyph.Margin=new Thickness(4,0,4,0);glyph.FontSize=16;
                var mark=new Border{Child=glyph,Background=BrushOf("#E5F0E9"),CornerRadius=new CornerRadius(4),Margin=new Thickness(0,0,6,0)};
                var content=new DockPanel();content.Children.Add(mark);content.Children.Add(label);
                button.Content=content;
                button.Height=Math.Min(42*scale,Math.Max(20,tileArea.ActualHeight/Math.Ceiling(pinCapacity/2d)-4));
                button.ToolTip=card.Desc;
                AutomationProperties.SetAutomationId(button,"HomePin-"+PinIds[name]);
                tiles.Children.Add(button);
            }
            tileArea.Children.Add(tiles);
        }
        void DrawReviews(){
            reviews.Children.Clear();
            if(all.Count==0){
                reviews.Children.Add(Text("حالات المراجعة",16,true));
                reviews.Children.Add(Text(message??"لا توجد حالات تستدعي المراجعة لهذا الشهر.",14));
                return;
            }
            var sections=Enum.GetValues<ReviewSection>();
            var active=all.Where(i=>i.Section==homeSection).ToList();
            int pages=Math.Max(1,(active.Count+reviewCapacity-1)/reviewCapacity);
            homeReviewPage=Math.Clamp(homeReviewPage,0,pages-1);
            var pager=HomePager(homeReviewPage,pages,"حالات المراجعة",p=>{homeReviewPage=p;DrawReviews();});
            void Select(ReviewSection section){homeSection=section;homeReviewPage=0;DrawReviews();}
            string Caption(ReviewSection section){
                string name=section switch{ReviewSection.Payroll=>"الرواتب",ReviewSection.Deductions=>"الاستقطاعات",_=>"الجهات"};
                return name+" · "+all.Count(i=>i.Section==section);
            }
            UIElement tabs;
            if(compact){
                var selector=Choices(sections.Select(Caption));
                selector.SelectedIndex=(int)homeSection;
                AutomationProperties.SetName(selector,"قسم حالات المراجعة");
                selector.SelectionChanged+=(_,_)=>{if(selector.SelectedIndex>=0)Select(sections[selector.SelectedIndex]);};
                tabs=selector;
            }else{
                var buttons=new StackPanel{Orientation=Orientation.Horizontal};
                foreach(var section in sections){
                    var tab=HomeAction(Caption(section),()=>Select(section),"",section==homeSection);
                    AutomationProperties.SetAutomationId(tab,"HomeSection-"+section);buttons.Children.Add(tab);
                }
                tabs=buttons;
            }
            var actions=new StackPanel{Orientation=Orientation.Horizontal};
            actions.Children.Add(tabs);actions.Children.Add(pager);
            reviews.Children.Add(HomeHeading("حالات المراجعة",actions));
            if(active.Count==0){reviews.Children.Add(Text("لا توجد حالات في هذا القسم.",14));return;}
            foreach(var item in active.Skip(homeReviewPage*reviewCapacity).Take(reviewCapacity)){
                var strip=HomeAction(item.Title,()=>Guard(item.Open));
                strip.HorizontalContentAlignment=HorizontalAlignment.Stretch;
                strip.BorderThickness=new Thickness(1,1,3,1);
                strip.BorderBrush=BrushOf("#B79A65");
                var row=new DockPanel();
                var detail=Text(item.Detail,12);
                detail.MaxWidth=compact?118:250;
                detail.TextWrapping=TextWrapping.NoWrap;detail.TextTrimming=TextTrimming.CharacterEllipsis;
                DockPanel.SetDock(detail,Dock.Right);row.Children.Add(detail);
                var title=Text(item.Title,14,true);
                title.TextWrapping=TextWrapping.NoWrap;title.TextTrimming=TextTrimming.CharacterEllipsis;
                row.Children.Add(title);strip.Content=row;strip.ToolTip=item.Title+" · "+item.Detail;
                AutomationProperties.SetAutomationId(strip,"HomeReview-"+item.Key);
                AutomationProperties.SetHelpText(strip,item.Detail);
                reviews.Children.Add(strip);
            }
        }
        void FitReviews(){
            int next=board.ActualHeight>=400*scale?3:board.ActualHeight>=300*scale?2:1;
            bool narrow=board.ActualWidth<660*scale;
            if(reviewCapacity==next && compact==narrow)return;
            reviewCapacity=next;compact=narrow;DrawReviews();
        }
        tileArea.SizeChanged+=(_,_)=>{
            int capacity=2*Math.Clamp((int)(tileArea.ActualHeight/(46*scale)),1,4);
            pinCapacity=capacity;DrawPins();
        };
        board.SizeChanged+=(_,_)=>FitReviews();
        FitReviews();CanvasArea.Children.Add(board);
        bool Current()=>CanvasArea.Children.Contains(board) && SelectedMonth?.Name==month?.Name && store.Active==revision;
        if(month is not {Key:>0})return;
        try{
            var summaryData=await Task.Run(()=>store.Summary(new(Year:month.Key/100,Month:month.Key%100)));
            if(!Current())return;
            balance.Text="إجمالي الاستقطاعات　"+Arabic.Money(summaryData.Total)+" د.ع　";
            count.Text="عدد القيود　"+Arabic.Money(summaryData.Count);
            var previous=months.FirstOrDefault(m=>m.Key>0&&m.Key<month.Key);
            var data=await HomeReviewData(revision,month,previous);
            if(!Current())return;
            SalaryDesktop.Core.SalaryReviewResult? audit=null;
            if(data.Salary){
                try{audit=await Audit(month.Name);}
                catch(Exception ex){if(Current())Status.Text=ex.Message;}
            }
            if(!Current())return;
            all=HomeReviewItems(month,data.Alerts,data.Salary,audit,data.Deductions);
            if(all.Count>0 && all.All(i=>i.Section!=homeSection)){homeSection=all[0].Section;homeReviewPage=0;}
            message=null;DrawReviews();
        }catch(Exception ex){
            if(Current()){message="تعذّر تحميل حالات المراجعة.";DrawReviews();Status.Text=ex.Message;}
        }
    }
}

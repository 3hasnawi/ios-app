using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

public partial class MainWindow
{
    string? homeMonth;
    int homePinPage;

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

    static Button HomeIcon(string label,string icon,Action action)
    {
        var button=HomeAction(label,action);
        var glyph=Glyph(icon);
        glyph.FlowDirection=FlowDirection.LeftToRight;
        glyph.FontSize=15;
        glyph.Margin=new Thickness(0);
        button.Content=glyph;
        button.MinWidth=28;
        button.MinHeight=28;
        button.ToolTip=label;
        return button;
    }

    Border ReviewCard(string section,IReadOnlyList<HomeReview> cases,string? empty)
    {
        int index=0;
        var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};
        var frame=new Grid();
        frame.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        frame.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        frame.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var title=Text("",16,true);
        var detail=Text("",13);
        var position=Text("",12);
        var header=new DockPanel();
        DockPanel.SetDock(position,Dock.Right);
        header.Children.Add(position);
        header.Children.Add(Bar(Glyph(section=="الرواتب"?"\uE8C7":"\uE8FD"),Text(section,14,true)));
        var content=Column(title,detail);
        var subject=HomeAction(section,()=>{if(cases.Count>0)Guard(cases[index].Open);});
        subject.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        subject.Background=Brushes.Transparent;
        subject.BorderThickness=new Thickness(0);
        subject.Content=content;
        var viewport=new Border{Child=subject,ClipToBounds=true};
        Grid.SetColumn(viewport,1);
        frame.Children.Add(viewport);
        var body=Column(header,frame);body.VerticalAlignment=VerticalAlignment.Center;
        var card=Box(body,"SolidBackgroundFillColorTertiaryBrush");
        card.Padding=new Thickness(9,6,9,6);
        var previous=HomeIcon("السابق · "+section,"\uE76C",()=>Move(-1));
        var next=HomeIcon("التالي · "+section,"\uE76B",()=>Move(1));
        Grid.SetColumn(next,2);
        frame.Children.Add(previous);frame.Children.Add(next);
        previous.VerticalAlignment=next.VerticalAlignment=VerticalAlignment.Center;
        previous.Visibility=next.Visibility=cases.Count>1?Visibility.Visible:Visibility.Collapsed;

        void Restart(){
            timer.Stop();
            if(cases.Count>1 && card.IsLoaded && !card.IsMouseOver && !card.IsKeyboardFocusWithin)
                timer.Start();
        }
        void Display(){
            if(cases.Count==0){
                subject.IsEnabled=false;title.Text=empty??"لا توجد حالات تستدعي المراجعة.";
                return;
            }
            var item=cases[index];
            title.Text=item.Title;detail.Text=item.Detail;
            position.Text=$"{index+1} / {cases.Count}";
            AutomationProperties.SetName(subject,item.Title);
            AutomationProperties.SetAutomationId(subject,"HomeReview-"+item.Key);
            AutomationProperties.SetHelpText(subject,item.Detail);
            subject.ToolTip=item.Title+" · "+item.Detail;
        }
        void Move(int step){
            if(cases.Count<2)return;
            index=(index+step+cases.Count)%cases.Count;
            Display();
            if(SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast){
                var offset=new TranslateTransform();
                content.RenderTransform=offset;
                var duration=TimeSpan.FromMilliseconds(220);
                var ease=new CubicEase{EasingMode=EasingMode.EaseOut};
                offset.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(step*16,0,duration){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
                content.BeginAnimation(OpacityProperty,new DoubleAnimation(.35,1,duration){EasingFunction=ease,FillBehavior=FillBehavior.Stop});
            }
            Restart();
        }
        timer.Tick+=(_,_)=>{if(IsActive && card.IsVisible && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)Move(1);};
        card.Loaded+=(_,_)=>Restart();
        card.Unloaded+=(_,_)=>timer.Stop();
        card.MouseEnter+=(_,_)=>timer.Stop();
        card.MouseLeave+=(_,_)=>Restart();
        card.IsKeyboardFocusWithinChanged+=(_,_)=>Restart();
        Display();
        return card;
    }

    async Task Home()
    {
        var month=SelectedMonth;
        var revision=store.Active;
        if(homeMonth!=month?.Name){homeMonth=month?.Name;homePinPage=0;}
        var board=new Grid();
        board.RowDefinitions.Add(new(){Height=GridLength.Auto});
        board.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var metrics=new UniformGrid{Columns=3};
        TextBlock Metric(string label,string icon){
            var value=Text("—",20,true);
            var cell=Column(Bar(Glyph(icon),Text(label,14)),value);
            cell.Margin=new Thickness(8,3,8,5);
            metrics.Children.Add(cell);
            return value;
        }
        var deduction=Metric("إجمالي الاستقطاعات","\uE8FD");
        var bank=Metric("مجموع كشف البنك","\uE825");
        var employees=Metric("عدد الموظفين","\uE716");
        var ranks=new UniformGrid{Columns=4};
        string[] labels=["ضباط","موظفون","مراتب","عقود"];
        string[] icons=["\uEA18","\uE821","\uE70D","\uE8A5"];
        var rankMenus=new ContextMenu[labels.Length];
        var counts=labels.Select((label,i)=>{
            var count=Text("—",14,true);
            var menu=new ContextMenu{FlowDirection=FlowDirection.RightToLeft,Placement=PlacementMode.Bottom};
            rankMenus[i]=menu;
            var chip=HomeAction("الرتب · "+label,()=>{if(menu.HasItems)menu.IsOpen=true;});
            chip.Content=Bar(Glyph(icons[i]),Text(label,13),count);
            chip.ToolTip="تفاصيل الرتب · "+label;
            menu.PlacementTarget=chip;
            ranks.Children.Add(chip);
            return count;
        }).ToArray();
        var summary=Column(metrics,ranks);
        if(revision==null)summary.Children.Add(HomeAction("استيراد نسخة",()=>_=ImportBackup(),"\uE8E5"));
        var banner=Box(summary);banner.Padding=new Thickness(10,6,10,6);
        board.Children.Add(banner);

        var panes=new Grid();Grid.SetRow(panes,1);board.Children.Add(panes);
        panes.ColumnDefinitions.Add(new(){Width=new GridLength(1.1,GridUnitType.Star)});
        panes.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var pinArea=new DockPanel();
        var pinActions=new StackPanel{Orientation=Orientation.Horizontal};
        var directory=HomeIcon("دليل الكشوف وتخصيصها","\uE713",()=>{});
        directory.Click+=(_,_)=>OpenHomeDirectory(directory);
        pinActions.Children.Add(directory);
        var heading=HomeHeading("كشوفي المثبتة",pinActions);
        DockPanel.SetDock(heading,Dock.Top);pinArea.Children.Add(heading);
        var tileArea=new Grid();pinArea.Children.Add(tileArea);
        var pinPane=Box(pinArea);pinPane.Padding=new Thickness(9);
        panes.Children.Add(pinPane);
        var reviews=new UniformGrid{Rows=2,Columns=1};
        var reviewLayout=new DockPanel();
        var reviewHeading=Bar(Glyph("\uEA8F"),Text("حالات المراجعة",16,true));
        DockPanel.SetDock(reviewHeading,Dock.Top);reviewLayout.Children.Add(reviewHeading);
        reviewLayout.Children.Add(reviews);
        var reviewPane=Box(reviewLayout);reviewPane.Padding=new Thickness(9);
        Grid.SetColumn(reviewPane,1);panes.Children.Add(reviewPane);

        int pinCapacity=0,columns=0;
        double scale=1+appearance.Level*.05;
        double tileHeight=40*scale;
        panes.VerticalAlignment=VerticalAlignment.Top;
        panes.MaxHeight=356*scale;
        void DrawPins(){
            int pages=Math.Max(1,(pins.Count+pinCapacity-1)/pinCapacity);
            homePinPage=Math.Clamp(homePinPage,0,pages-1);
            if(pinActions.Children.Count>1)pinActions.Children.RemoveAt(1);
            pinActions.Children.Add(HomePager(homePinPage,pages,"الكشوف",p=>{homePinPage=p;DrawPins();}));
            tileArea.Children.Clear();
            if(pins.Count==0){tileArea.Children.Add(Text("اختر كشوفك من زر التخصيص.",14));return;}
            var tiles=new UniformGrid{Columns=columns,VerticalAlignment=VerticalAlignment.Top};
            foreach(var name in pins.Skip(homePinPage*pinCapacity).Take(pinCapacity)){
                var card=Cards.First(c=>c.Name==name);
                var button=HomeAction(name,()=>Activate(name));
                button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
                var label=Text(name,14,true);label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;
                var mark=Box(Glyph(card.Icon),"SystemFillColorSuccessBackgroundBrush");
                mark.Padding=new Thickness(2);mark.Margin=new Thickness(0,0,6,0);mark.BorderThickness=new Thickness(0);
                var row=new DockPanel();row.Children.Add(mark);row.Children.Add(label);
                button.Content=row;button.Height=tileHeight;button.ToolTip=card.Desc;
                AutomationProperties.SetAutomationId(button,"HomePin-"+PinIds[name]);
                tiles.Children.Add(button);
            }
            tileArea.Children.Add(tiles);
        }
        tileArea.SizeChanged+=(_,_)=>{
            int nextColumns=tileArea.ActualWidth>=315*scale?2:1;
            int rows=Math.Clamp((int)(tileArea.ActualHeight/(40*scale+4)),1,4);
            int nextCapacity=nextColumns*rows;
            double height=Math.Clamp(Math.Floor(tileArea.ActualHeight/rows)-4,30*scale,66*scale);
            if(nextCapacity==pinCapacity && nextColumns==columns && height==tileHeight)return;
            pinCapacity=nextCapacity;columns=nextColumns;tileHeight=height;DrawPins();
        };
        void DrawReviews(IReadOnlyList<HomeReview> items,string? empty=null){
            reviews.Children.Clear();
            foreach(var section in new[]{ReviewSection.Deductions,ReviewSection.Payroll})
                reviews.Children.Add(ReviewCard(section==ReviewSection.Payroll?"الرواتب":"الاستقطاعات",
                    items.Where(i=>i.Section==section).ToArray(),empty));
        }
        DrawReviews([],month is {Key:>0}?"جارٍ تحميل الحالات…":"اختر شهراً لعرض الحالات.");
        // تظل اللوحتان قابلتين للوصول حتى عند تصغير نافذة ويندوز وتكبير خط النظام.
        var viewport=new Grid();
        viewport.Children.Add(new Viewbox{Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,Child=board});
        void Fit(){
            board.Width=Math.Max(680*scale,CanvasArea.ActualWidth);
            board.Height=Math.Max(375*scale,CanvasArea.ActualHeight);
            panes.Height=Math.Min(panes.MaxHeight,board.Height*.66);
        }
        viewport.SizeChanged+=(_,_)=>Fit();Fit();CanvasArea.Children.Add(viewport);
        bool Current()=>CanvasArea.Children.Contains(viewport) && SelectedMonth?.Name==month?.Name && store.Active==revision;
        if(month is not {Key:>0} || revision==null)return;
        try{
            var totals=await Task.Run(()=>store.Summary(new(Year:month.Key/100,Month:month.Key%100)));
            if(!Current())return;
            deduction.Text=Arabic.Money(totals.Total)+" د.ع";
            var figures=await MonthFigures(revision,month.Name);
            if(!Current())return;
            if(figures.summary!=null){
                bank.Text=Arabic.Money(figures.summary.BankTotal)+" د.ع";
                var buckets=await Task.Run(()=>{
                    return figures.snapshot.Employees.Where(e=>e.Month==month.Name)
                        .GroupBy(e=>e.Rank.Trim()).Select(g=>new {Rank=g.Key,Count=g.Count(),Category=A.FundReport.CategoryOf(g.Key)})
                        .OrderBy(g=>A.RankOrder.Rank(g.Rank)).ThenBy(g=>g.Rank,StringComparer.Ordinal).ToLookup(g=>g.Category);
                });
                if(!Current())return;
                employees.Text=Arabic.Money(buckets.SelectMany(g=>g).Sum(r=>r.Count));
                for(int i=0;i<counts.Length;i++){
                    counts[i].Text=Arabic.Money(buckets[i].Sum(r=>r.Count));
                    foreach(var row in buckets[i]){
                        var name=row.Rank.Length==0?"رتبة غير مسجلة":row.Rank;
                        rankMenus[i].Items.Add(new MenuItem{Header=Text(name+"　"+Arabic.Money(row.Count)),Focusable=false});
                    }
                }
            }
        }catch(Exception ex){if(Current())Status.Text=ex.Message;}
        if(!Current())return;
        try{
            var previous=months.FirstOrDefault(m=>m.Key>0&&m.Key<month.Key);
            var data=await HomeReviewData(revision,month,previous);
            if(!Current())return;
            A.SalaryReviewResult? audit=null;
            if(data.Salary){
                try{audit=await Audit(month.Name);}
                catch(Exception ex){if(Current())Status.Text=ex.Message;}
            }
            if(Current())DrawReviews(HomeReviewItems(month,data.Alerts,data.Salary,audit,data.Deductions));
        }catch(Exception ex){
            if(Current()){DrawReviews([],"تعذّر تحميل حالات المراجعة.");Status.Text=ex.Message;}
        }
    }
}

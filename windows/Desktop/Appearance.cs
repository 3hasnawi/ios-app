using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using A=SalaryDesktop.Core;

namespace AnbarPayroll.Desktop;

internal sealed record Appearance(int Level=0,bool Hindi=false)
{
    const string FileName="appearance.json";

    public static Appearance Read(string root)
    {
        try { var saved=JsonSerializer.Deserialize<Appearance>(File.ReadAllText(Path.Combine(root,FileName)));return saved is {Level:>=0 and <=4}?saved:new(); }
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){return new();}
    }

    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        A.ReportFiles.Save(JsonSerializer.SerializeToUtf8Bytes(this),Path.Combine(root,FileName),".json");
    }

    public void Apply()
    {
        DisplayNumerals.Hindi=Hindi;
        double scale=1+Level*.05;
        var resources=Application.Current.Resources;
        for(int half=20;half<=64;half++){
            double size=half*.5;
            resources[FontKey(size)]=size*scale;
        }
        resources["TableRowHeight"]=34*scale;
        resources["TableHeaderHeight"]=38*scale;
    }

    public static void InputDigits(object sender,TextChangedEventArgs e)
    {
        if(sender is not TextBox field || !ReferenceEquals(e.OriginalSource,field))return;
        var value=field.Text;
        bool numeric=value.Length>0 && value.All(c=>char.IsDigit(c)||"/.,: -٬٫".Contains(c));
        bool localAccount=value.Length==15 && value.All(char.IsDigit);
        bool hindi=DisplayNumerals.Hindi && numeric && !localAccount;
        System.Windows.Media.NumberSubstitution.SetCultureSource(field,System.Windows.Media.NumberCultureSource.Override);
        System.Windows.Media.NumberSubstitution.SetCultureOverride(field,CultureInfo.GetCultureInfo(hindi?"ar-EG":"en-US"));
        System.Windows.Media.NumberSubstitution.SetSubstitution(field,hindi?System.Windows.Media.NumberSubstitutionMethod.NativeNational:System.Windows.Media.NumberSubstitutionMethod.European);
    }

    public static string FontKey(double size)=>"UiFont"+size.ToString("0.#",CultureInfo.InvariantCulture);
}

public sealed class DisplayNumberConverter:IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>DisplayNumerals.Format(value?.ToString()??"");
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}

public sealed class DisplayText:TextBlock
{
    static DisplayText()=>TextProperty.OverrideMetadata(typeof(DisplayText),new FrameworkPropertyMetadata("",null,(_,raw)=>DisplayNumerals.Format((string?)raw??"")));
}

public partial class MainWindow
{
    Appearance appearance=new();
    Border AppearanceSettings()
    {
        var sizes=Choices(["عادي (100٪)","105٪","110٪","115٪","120٪"]);
        sizes.SelectedIndex=appearance.Level;
        var numbers=new ComboBox{ItemsSource=new[]{"عربية (123)","هندية (١٢٣)"},SelectedIndex=appearance.Hindi?1:0};
        var sample=Text("مثال　12,500 دينار",16,true);
        void Change(){
            var next=new Appearance(sizes.SelectedIndex,numbers.SelectedIndex==1);
            try{
                SaveAppearance(next);appearance=next;appearance.Apply();
                sample.CoerceValue(TextBlock.TextProperty);
                Period.Items.Refresh();
                if(Period.Template.FindName("MonthText",Period) is TextBlock month)month.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
                // يعاد إنشاء النصوص المقروءة فوراً مع إبقاء شهر العمل.
                ShowPage("الإعدادات");
            }catch(Exception ex){Error(ex);}
        }
        sizes.SelectionChanged+=(_,_)=>Change();numbers.SelectionChanged+=(_,_)=>Change();
        return Box(Column(Text("المظهر والأرقام",18,true),FilterBar(("حجم خط المحتوى",sizes,1),("شكل أرقام الواجهة",numbers,1)),sample));
    }
}

using Microsoft.UI.Xaml.Markup;
using System.Xml;
using System.Xml.Linq;
namespace SalaryWindows;
internal static class XamlDiagnostic
{
    public static void Run(string path,Action<string> log)
    {
        try
        {
            var source=XDocument.Load(path,LoadOptions.SetLineInfo);
            var names=new HashSet<string>{"Grid","Border","StackPanel","TextBlock","Button","ScrollViewer","ComboBox","TextBox","ListView"};
            var events=new HashSet<string>{"Click","SelectionChanged","TextChanged","ItemClick"};
            foreach(var original in source.Descendants().Where(x=>names.Contains(x.Name.LocalName)))
            {
                var clone=new XElement(original);
                foreach(var attr in clone.DescendantsAndSelf().Attributes().Where(a=>events.Contains(a.Name.LocalName) || (a.Name.NamespaceName=="http://schemas.microsoft.com/winfx/2006/xaml" && (a.Name.LocalName=="Name" || a.Name.LocalName=="Class"))).ToList()) attr.Remove();
                clone.SetAttributeValue(XNamespace.Xmlns+"x","http://schemas.microsoft.com/winfx/2006/xaml");
                try { _=XamlReader.Load(clone.ToString(SaveOptions.DisableFormatting)); }
                catch(Exception e) { var line=((IXmlLineInfo)original).LineNumber; log($"XAML isolated line {line} {original.Name.LocalName}: {e}"); }
            }
        }
        catch(Exception e) { log("XAML diagnostic error: "+e); }
    }
}

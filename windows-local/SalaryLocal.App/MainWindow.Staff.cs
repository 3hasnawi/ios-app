using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SalaryDesktop.Core;
using SalaryLocal.Core;

namespace SalaryLocal.App;

public partial class MainWindow
{
    private sealed record LocalStaffView(LocalStaffRow Row, string SearchKey)
    {
        public string Name => Row.Member.Name;
        public string Mother => LocalStaff.MotherName(Row.Member);
        public string Rank => Row.Member.Rank;
        public string Workplace => Row.Member.Workplace;
        public string Status => Row.Member.Status;
        public string Stat => Row.Member.Stat;
        public string Unified => Row.Member.Unified;
    }
    private async Task LocalStaffPage()
    {
        var loaded = await Task.Run(() => {
            var staff = workspace.Store.GetLocalStaff();
            return (Snapshot: staff, Rows: staff.Rows.Select(r => new LocalStaffView(r, PayrollAnalysis.Normalize($"{r.Member.Name} {r.Member.Mother} {r.Member.Stat} {r.Member.Unified} {r.Member.Rank} {r.Member.Workplace} {r.Member.Status}"))).ToArray());
        });
        Page.Children.Add(Ui.Text("إدارة الملاك المحلي", 25));
        Page.Children.Add(Ui.Text("استيراد Access بمراجعة الجدول والحقول، أو إضافة وتعديل السجلات محلياً. الاستيراد استبدال كامل دون دمج بالأسماء؛ البيانات السابقة تبقى في التاريخ والنسخ المحلية."));
        var actions = new WrapPanel(); Page.Children.Add(actions);
        actions.Children.Add(Ui.Button("استيراد قاعدة Access", async () => { if (!busy) await Run(() => ImportStaffAccess(loaded.Snapshot)); }));
        actions.Children.Add(Ui.Button("إضافة سجل", async () => { if (!busy) await Run(() => EditLocalStaff(loaded.Snapshot, null)); }));
        var count = Ui.Text($"{loaded.Rows.Length:N0} سجل"); Page.Children.Add(count);
        var search = new TextBox { ToolTip = "الاسم أو الأم أو الرقم الإحصائي أو الموحد أو الرتبة أو الجهة" }; Page.Children.Add(search);
        var grid = RecordsGrid(("الاسم", "Name"), ("الأم", "Mother"), ("الرتبة", "Rank"), ("محل العمل", "Workplace"), ("الحالة", "Status"), ("الإحصائي", "Stat"), ("الموحد", "Unified"));
        grid.ItemsSource = loaded.Rows; Page.Children.Add(grid);
        actions.Children.Add(Ui.Button("تعديل المحدد", async () => {
            if (busy) return;
            if (grid.SelectedItem is not LocalStaffView row) { Ui.Message(this, "اختر سجل ملاك أولاً."); return; }
            await Run(() => EditLocalStaff(loaded.Snapshot, row.Row));
        }));
        actions.Children.Add(Ui.Button("حذف المحدد", async () => {
            if (busy) return;
            if (grid.SelectedItem is not LocalStaffView row) { Ui.Message(this, "اختر سجل ملاك أولاً."); return; }
            if (Ui.Message(this, $"حذف «{row.Name}» من الملاك الحالي فقط؟\nلا تتغير الرواتب أو الاستقطاعات، وتبقى النسخة السابقة في التاريخ المحلي.", true) != MessageBoxResult.Yes) return;
            await Run(async () => { await Task.Run(() => workspace.Store.DeleteStaffMember(row.Row, loaded.Snapshot.RevisionId)); await LoadMonth(); });
        }));
        if (loaded.Rows.Length == 0) Page.Children.Add(Ui.Text("لا يوجد ملاك محفوظ. اختر قاعدة Access أو أضف سجلاً محلياً، أو انقل الملاك من نسخة آيفون."));
        int generation = 0; int pageGeneration = searchGeneration;
        search.TextChanged += async (_, _) => {
            var id = ++generation; var query = PayrollAnalysis.Normalize(search.Text);
            try { await Task.Delay(180); if (id != generation || pageGeneration != searchGeneration) return;
                var found = await Task.Run(() => loaded.Rows.Where(x => x.SearchKey.Contains(query, StringComparison.Ordinal)).ToArray());
                if (id != generation || pageGeneration != searchGeneration) return; grid.ItemsSource = found; count.Text = $"{found.Length:N0} من {loaded.Rows.Length:N0} سجل";
            } catch (Exception ex) { if (pageGeneration == searchGeneration) Ui.Message(this, "تعذّر البحث في الملاك.\n" + ex.Message); }
        };
        grid.MouseDoubleClick += async (_, _) => {
            if (busy || grid.SelectedItem is not LocalStaffView row) return;
            await Run(async () => {
                var fields = await Task.Run(() => StaffDetailRows(row.Row.Member)); RecordDetails(row.Name, fields);
            });
        };
    }
    private static IReadOnlyList<RecordField> StaffDetailRows(StaffMember s)
    {
        var result = new List<RecordField> { new("البيانات الأساسية", "الاسم", s.Name), new("البيانات الأساسية", "الأم", LocalStaff.MotherName(s)), new("البيانات الأساسية", "الإحصائي", s.Stat), new("البيانات الأساسية", "الموحد", s.Unified), new("البيانات الأساسية", "الرتبة", s.Rank), new("البيانات الأساسية", "الحالة", s.Status), new("البيانات الأساسية", "محل العمل", s.Workplace) };
        foreach (var section in StaffRecords.Sections(s)) foreach (var field in section.Fields) result.Add(new(section.Title, field.Label, field.Value));
        if (s.Details is { Length: > 0 } && JsonNode.Parse(s.Details) is JsonArray sourceSections)
            foreach (var section in sourceSections)
                if (section?["localAccessSource"]?["columns"] is JsonArray raw)
                    foreach (var field in raw)
                        result.Add(new("قيم Access الأصلية (قبل أي تعديل محلي)", field?["name"]?.GetValue<string>() ?? "حقل غير معروف",
                            field?["value"] is null ? "⟦NULL: لا توجد قيمة⟧" : field["value"]!.GetValue<string>() is { Length: 0 } ? "⟦نص فارغ⟧" : field["value"]!.GetValue<string>()));
        return result;
    }
    private async Task EditLocalStaff(LocalStaffSnapshot snapshot, LocalStaffRow? row)
    {
        var old = row?.Member ?? new StaffMember("", "", "", "", "", "", "", null);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Ui.Text(row is null ? "إضافة سجل ملاك مستقل، دون ربطه تلقائياً بسجل سابق." : "تعديل الحقول الأساسية فقط. تبقى بقية التفاصيل وبيانات المصدر الأصلية كما هي، وتحفظ نسخة ما قبل التعديل."));
        string displayedMother = LocalStaff.MotherName(old);
        var name = Field(panel, "الاسم الكامل", old.Name); var mother = Field(panel, "اسم الأم", displayedMother);
        var stat = Field(panel, "الرقم الإحصائي", old.Stat); var unified = Field(panel, "الرقم الموحد", old.Unified);
        var rank = Field(panel, "الرتبة", old.Rank); var status = Field(panel, "الحالة الوظيفية", old.Status); var workplace = Field(panel, "محل العمل", old.Workplace);
        var dialog = Ui.Dialog(this, row is null ? "إضافة سجل ملاك" : "تعديل سجل الملاك", new ScrollViewer { Content = panel }, 720, 790);
        StaffMember? approved = null;
        panel.Children.Add(Ui.Button("مراجعة وحفظ", () => {
            try {
                var edited = LocalStaff.Edit(old, name.Text, mother.Text, stat.Text, unified.Text, rank.Text, status.Text, workplace.Text, exactMotherSpelling: mother.Text != displayedMother);
                if (Ui.Message(dialog, $"حفظ سجل «{edited.Name}»؟\nالإحصائي: {edited.Stat}\nالموحد: {edited.Unified}\nالرتبة: {edited.Rank}\nمحل العمل: {edited.Workplace}\nلا تتغير بيانات الرواتب أو الاستقطاعات.", true) != MessageBoxResult.Yes) return;
                approved = edited; dialog.Close();
            } catch (Exception ex) { Ui.Message(dialog, ex.Message); }
        }));
        panel.Children.Add(Ui.Button("إلغاء دون حفظ", dialog.Close)); dialog.ShowDialog(); if (approved is null) return;
        var values = approved; string? motherSpelling = mother.Text != displayedMother ? mother.Text : null;
        await Task.Run(() => row is null ? workspace.Store.AddStaffMember(values with { Mother = motherSpelling ?? values.Mother, Details = null }, snapshot.RevisionId) : workspace.Store.EditStaffMember(row, values, snapshot.RevisionId, motherSpelling));
        await LoadMonth();
    }
    private async Task ImportStaffAccess(LocalStaffSnapshot snapshot)
    {
        var picker = new OpenFileDialog { Filter = "قاعدة ملاك Access (*.accdb;*.mdb)|*.accdb;*.mdb", CheckFileExists = true, Multiselect = false };
        if (Ui.ShowNativeDialog(() => picker.ShowDialog(this)) != true) return;
        var catalog = await Task.Run(() => {
            using var reader = LocalAccessReader.Open(picker.FileName); var tables = reader.UserTables(); reader.VerifyUnchanged();
            return (Digest: reader.SourceSha256, Tables: tables);
        });
        if (catalog.Tables.Count == 0) throw new InvalidDataException("لا توجد جداول مستخدم في قاعدة Access.");
        var selection = new StackPanel { Margin = new Thickness(22) };
        selection.Children.Add(Ui.Text("اختر جدول الموظفين للمراجعة", 23)); selection.Children.Add(Ui.Text(Path.GetFileName(picker.FileName)));
        selection.Children.Add(Ui.Text("يجب أن يحتوي الجدول على «الاسم الكامل». «الاسم» وحده قد يكون اسم سائق الضابط ولا يُعتمد. اختيار جدول لا يحفظ شيئاً."));
        var choices = new ComboBox { ItemsSource = catalog.Tables, DisplayMemberPath = "Name", SelectedIndex = -1 }; selection.Children.Add(choices);
        var info = Ui.Text("اختر جدولاً لإظهار عدد السجلات والأعمدة."); selection.Children.Add(info);
        var columns = RecordsGrid(("عمود المصدر", "Name"), ("نوع Access", "Type")); columns.Height = 260; selection.Children.Add(columns);
        choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is LocalAccessTableInfo table) { info.Text = $"{table.DeclaredRowCount:N0} سجل معلن • {table.Columns.Count:N0} عمود"; columns.ItemsSource = table.Columns; } };
        var tableDialog = Ui.Dialog(this, "اختيار جدول Access", new ScrollViewer { Content = selection }, 850, 670);
        string? selected = null;
        selection.Children.Add(Ui.Button("فحص الجدول ومعاينته", () => { if (choices.SelectedItem is not LocalAccessTableInfo table) { Ui.Message(tableDialog, "اختر جدول الموظفين أولاً."); return; } selected = table.Name; tableDialog.Close(); }));
        selection.Children.Add(Ui.Button("إلغاء", tableDialog.Close)); tableDialog.ShowDialog(); if (selected is null) return;
        var preview = await Task.Run(() => LocalStaffAccessImport.Read(picker.FileName, selected, snapshot.RevisionId, catalog.Digest));
        var dock = new DockPanel { Margin = new Thickness(22) }; var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); dock.Children.Add(header);
        header.Children.Add(Ui.Text("مراجعة استيراد الملاك", 23)); header.Children.Add(Ui.Text($"الجدول: {preview.TableName} • {preview.Members.Count:N0} سجل مقروء • {preview.Problems.Count:N0} أخطاء\nالملاك الحالي: {snapshot.Rows.Count:N0} سجل"));
        foreach (var warning in preview.Warnings) header.Children.Add(Ui.Text(warning));
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer); var tabs = new TabControl(); dock.Children.Add(tabs);
        var employees = RecordsGrid(("الاسم", "Name"), ("الأم", "Mother"), ("الإحصائي", "Stat"), ("الموحد", "Unified"), ("الرتبة", "Rank"), ("محل العمل", "Workplace")); employees.Height = double.NaN; employees.ItemsSource = preview.Members;
        employees.MouseDoubleClick += (_, _) => { if (employees.SelectedItem is StaffMember member) RecordDetails(member.Name, StaffDetailRows(member)); };
        tabs.Items.Add(new TabItem { Header = "جميع السجلات", Content = employees });
        var mapping = RecordsGrid(("العمود المعروف", "Source"), ("حقل الملاك", "Destination")); mapping.Height = double.NaN; mapping.ItemsSource = preview.Mappings; tabs.Items.Add(new TabItem { Header = "تعيين الحقول", Content = mapping });
        var errors = RecordsGrid(("صف المصدر", "Row"), ("الخطأ المانع للحفظ", "Message")); errors.Height = double.NaN; errors.ItemsSource = preview.Problems; tabs.Items.Add(new TabItem { Header = "أخطاء الفحص", Content = errors }); tabs.SelectedIndex = preview.Problems.Count > 0 ? 2 : 0;
        footer.Children.Add(Ui.Text("جميع الصفوف قابلة للمراجعة. انقر مرتين لعرض التفاصيل. الأعمدة غير المعروفة والهوية الأصلية والفراغات محفوظة داخل تفاصيل المصدر؛ لا يُكتب شيء في ملف Access."));
        var dialog = Ui.Dialog(this, "مراجعة الملاك قبل الاستبدال", dock, 1150, 800); bool accepted = false;
        var save = Ui.Button(snapshot.Rows.Count == 0 ? "تأكيد استيراد الملاك" : "تأكيد استبدال الملاك كاملاً", () => {
            if (Ui.Message(dialog, $"{(snapshot.Rows.Count == 0 ? "استيراد" : "استبدال")} الملاك الحالي ({snapshot.Rows.Count:N0} سجل) بـ {preview.Members.Count:N0} سجل من الجدول «{preview.TableName}»؟\nلا تُدمج الهويات أو تُنقل حقول السجلات السابقة للمجموعة الجديدة. السابق محفوظ في التاريخ المحلي، ولا تتغير الرواتب والاستقطاعات.", true) != MessageBoxResult.Yes) return;
            accepted = true; dialog.Close();
        }); save.IsEnabled = preview.Problems.Count == 0 && preview.Members.Count > 0; footer.Children.Add(save); footer.Children.Add(Ui.Button("إلغاء دون تغيير", dialog.Close)); dialog.ShowDialog();
        if (!accepted) return;
        var result = await Task.Run(() => workspace.Store.ImportReviewedStaff(preview, snapshot.Rows.Count > 0));
        await LoadMonth(); Ui.Message(this, result.Changed ? "حُفظ الملاك في معاملة واحدة. ملف Access لم يتغير والتاريخ السابق محفوظ؛ أنشئ نسخة محلية لحماية البيانات." : "الملاك مطابق للمحفوظ؛ لم تُضف مراجعة مكررة.");
    }
}

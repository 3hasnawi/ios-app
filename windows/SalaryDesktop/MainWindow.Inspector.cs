using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SalaryDesktop;

public partial class MainWindow
{
    private void InitializeInspectorEvents()
    {
        if (DataTable != null)
        {
            DataTable.SelectionChanged -= DataTable_InspectorSelectionChanged;
            DataTable.SelectionChanged += DataTable_InspectorSelectionChanged;
        }
    }

    private void DataTable_InspectorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateInspectorFromSelection();
    }

    private void UpdateInspectorFromSelection()
    {
        if (InspectorDrawer == null || DataTable?.SelectedItem == null)
        {
            return;
        }

        var selected = DataTable.SelectedItem;
        var props = ExtractRowProperties(selected);
        if (props.Count == 0)
        {
            return;
        }

        InspectorDrawer.Visibility = Visibility.Visible;

        string title = FindFirstMatch(props, "الاسم", "اسم المنتسب", "الموظف", "Name", "EmployeeName", "AccountName") ?? "سجل محدد";
        string code = FindFirstMatch(props, "الرمز", "الرقم الوظيفي", "ت", "ID", "Code") ?? "#";
        string subtitle = FindFirstMatch(props, "القسم", "الدرجة", "العنوان الوظيفي", "Department", "Grade", "Bank") ?? PageTitle?.Text ?? "";
        string net = FindFirstMatch(props, "الصافي", "صافي الراتب", "المبلغ الصافي", "المبلغ", "Net", "NetSalary", "Amount") ?? "—";

        if (InspectorTitleText != null) InspectorTitleText.Text = title;
        if (InspectorCodeText != null) InspectorCodeText.Text = code;
        if (InspectorSubtitleText != null) InspectorSubtitleText.Text = subtitle;
        if (InspectorNetAmountText != null) InspectorNetAmountText.Text = net;
        if (InspectorAvatarText != null) InspectorAvatarText.Text = GetArabicInitials(title);

        FilterInspectorFields(props);
    }

    private void FilterInspectorFields(List<KeyValuePair<string, string>> allProps)
    {
        if (InspectorFieldsList == null) return;

        IEnumerable<KeyValuePair<string, string>> filtered = allProps;

        if (InspectorTabDeductions?.IsChecked == true)
        {
            filtered = allProps.Where(p =>
                p.Key.Contains("استقطاع") ||
                p.Key.Contains("سلفة") ||
                p.Key.Contains("تقاعد") ||
                p.Key.Contains("ضريبة") ||
                p.Key.Contains("غياب") ||
                p.Key.Contains("Deduction", StringComparison.OrdinalIgnoreCase));
        }
        else if (InspectorTabBank?.IsChecked == true)
        {
            filtered = allProps.Where(p =>
                p.Key.Contains("مصرف") ||
                p.Key.Contains("حساب") ||
                p.Key.Contains("آيبان") ||
                p.Key.Contains("IBAN", StringComparison.OrdinalIgnoreCase) ||
                p.Key.Contains("Bank", StringComparison.OrdinalIgnoreCase) ||
                p.Key.Contains("حالة"));
        }

        var list = filtered.ToList();
        if (list.Count == 0)
        {
            list = allProps;
        }

        InspectorFieldsList.ItemsSource = list;
    }

    private void InspectorTabChanged(object sender, RoutedEventArgs e)
    {
        if (DataTable?.SelectedItem != null)
        {
            UpdateInspectorFromSelection();
        }
    }

    private void CloseInspectorClick(object sender, RoutedEventArgs e)
    {
        if (InspectorDrawer != null)
        {
            InspectorDrawer.Visibility = Visibility.Collapsed;
        }
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.K)
        {
            e.Handled = true;
            if (GlobalSearchButton != null && GlobalSearchButton.IsEnabled)
            {
                OpenGlobalSearch(GlobalSearchButton, new RoutedEventArgs());
            }
            return;
        }

        if (e.Key == Key.Escape && InspectorDrawer?.Visibility == Visibility.Visible)
        {
            InspectorDrawer.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private static List<KeyValuePair<string, string>> ExtractRowProperties(object item)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (item == null) return result;

        if (item is DataRowView drv)
        {
            foreach (DataColumn col in drv.Row.Table.Columns)
            {
                var val = drv[col.ColumnName]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(val))
                {
                    result.Add(new KeyValuePair<string, string>(col.ColumnName, val));
                }
            }
            return result;
        }

        var type = item.GetType();
        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            var valObj = prop.GetValue(item);
            if (valObj == null) continue;

            string val = valObj is IFormattable fmt
                ? fmt.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                : valObj.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(val))
            {
                result.Add(new KeyValuePair<string, string>(prop.Name, val));
            }
        }
        return result;
    }

    private static string? FindFirstMatch(List<KeyValuePair<string, string>> props, params string[] keys)
    {
        foreach (var k in keys)
        {
            var hit = props.FirstOrDefault(p => p.Key.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(hit.Value))
            {
                return hit.Value;
            }
        }
        return null;
    }

    private static string GetArabicInitials(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "م";
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return $"{parts[0][0]}{parts[1][0]}";
        }
        return fullName.Length >= 2 ? fullName.Substring(0, 2) : fullName;
    }
}

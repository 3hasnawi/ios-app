using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SalaryDesktop;

/// دائرة تحميل في وسط الصفحة أثناء احتساب البيانات؛ الدوران يعمل فقط وهي ظاهرة.
public partial class LoadingSpinner : UserControl
{
    public LoadingSpinner()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => Animate(IsVisible);
        Loaded += (_, _) => Animate(IsVisible);
    }
    public string Text { get => Label.Text; set => Label.Text = value; }
    private void Animate(bool on) =>
        Spin.BeginAnimation(RotateTransform.AngleProperty, on ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever } : null);
}

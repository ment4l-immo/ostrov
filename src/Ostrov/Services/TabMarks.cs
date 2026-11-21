using System.Windows;
using System.Windows.Media;

namespace Ostrov;

/// <summary>Метка-точка под значком вкладки (идёт таймер, играет музыка): цвет и степень проявления (плавно 0..1) задаёт окно, рисует шаблон вкладки.</summary>
public static class TabMarks
{
    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached(
        "Brush", typeof(Brush), typeof(TabMarks), new PropertyMetadata(null));

    public static readonly DependencyProperty AmountProperty = DependencyProperty.RegisterAttached(
        "Amount", typeof(double), typeof(TabMarks), new PropertyMetadata(0.0));

    // Шапка: подпись выбранной вкладки выезжает вправо от значка (ширина окошка подписи), под ней проявляется пластина (0..1).
    public static readonly DependencyProperty LabelWidthProperty = DependencyProperty.RegisterAttached(
        "LabelWidth", typeof(double), typeof(TabMarks), new PropertyMetadata(0.0));

    public static readonly DependencyProperty SelectProperty = DependencyProperty.RegisterAttached(
        "Select", typeof(double), typeof(TabMarks), new PropertyMetadata(0.0));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(TabMarks), new PropertyMetadata(""));

    public static double GetLabelWidth(DependencyObject o) => (double)o.GetValue(LabelWidthProperty);
    public static void SetLabelWidth(DependencyObject o, double v) => o.SetValue(LabelWidthProperty, v);
    public static double GetSelect(DependencyObject o) => (double)o.GetValue(SelectProperty);
    public static void SetSelect(DependencyObject o, double v) => o.SetValue(SelectProperty, v);
    public static string GetLabel(DependencyObject o) => (string)o.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject o, string v) => o.SetValue(LabelProperty, v);

    public static Brush? GetBrush(DependencyObject o) => (Brush?)o.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject o, Brush? v) => o.SetValue(BrushProperty, v);
    public static double GetAmount(DependencyObject o) => (double)o.GetValue(AmountProperty);
    public static void SetAmount(DependencyObject o, double v) => o.SetValue(AmountProperty, v);
}

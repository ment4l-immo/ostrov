using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ostrov.Modules;
using Ostrov.Services;

namespace Ostrov.AfterEffects;

/// <summary>
/// Расширенная плашка рендера After Effects (правый клик, М-017): те же данные, что на обычной, но без обрезки и покрупнее.
/// Идёт рендер: «After Effects · рендер», время, название композиции (если известно), бегущая полоска. Готово и ошибка: белый кружок, заголовок и подпись.
/// </summary>
internal sealed class RenderWideView : Grid
{
    public const double ViewWidth = 360, RenderingHeight = 88, ResultHeight = 72;
    private const double Side = 20;

    private readonly StackPanel _progress = new() { Margin = new Thickness(Side, 0, Side, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _elapsed = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _comp = new() { FontSize = 14, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };

    private readonly Grid _result = new() { Margin = new Thickness(Side, 0, Side, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _icon = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black };
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Hues.Ink, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 12, Foreground = Ui.Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };

    public RenderWideView()
    {
        Width = ViewWidth;
        Height = RenderingHeight;

        var header = new Grid();
        header.Children.Add(new TextBlock { Text = "After Effects · рендер", FontSize = 12, Foreground = Ui.Gray, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(_elapsed);
        _progress.Children.Add(header);
        _progress.Children.Add(_comp);
        _progress.Children.Add(new RunningBar(Hues.Ink, ViewWidth - 2 * Side) { Margin = new Thickness(0, 10, 0, 0) });
        Children.Add(_progress);

        _result.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _result.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var circle = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = Hues.Ink,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), Child = _icon
        };
        SetColumn(circle, 0);
        _result.Children.Add(circle);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _detail } };
        SetColumn(texts, 1);
        _result.Children.Add(texts);
        Children.Add(_result);
    }

    public void Update(RenderView view)
    {
        if (view.Phase == RenderPhase.Rendering)
        {
            _progress.Visibility = Visibility.Visible;
            _result.Visibility = Visibility.Collapsed;
            _elapsed.Text = RenderTracker.FormatElapsed(view.Elapsed);
            _comp.Text = view.Detail;
            _comp.Visibility = view.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Height = RenderingHeight;
            return;
        }

        _progress.Visibility = Visibility.Collapsed;
        _result.Visibility = Visibility.Visible;
        if (view.Phase == RenderPhase.Done)
        {
            _icon.FontFamily = Ui.IconFont;
            _icon.FontSize = 17;
            _icon.FontWeight = FontWeights.Normal;
            _icon.Text = "";
        }
        else
        {
            _icon.FontFamily = new FontFamily("Segoe UI");
            _icon.FontSize = 22;
            _icon.FontWeight = FontWeights.Bold;
            _icon.Text = "!";
        }
        _title.Text = view.Title;
        _detail.Text = view.Detail;
        Height = ResultHeight;
    }
}

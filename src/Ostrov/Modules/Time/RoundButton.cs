using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ostrov.Services;

namespace Ostrov.Modules.Time;

internal enum RoundStyle
{
    /// <summary>Тёмно-серая, белый текст («Отмена», «Круг», «Сброс»).</summary>
    Gray,
    /// <summary>С оранжевым оттенком, оранжевый текст («Старт», «Пауза», «Стоп»): главное действие таймера и секундомера.</summary>
    Orange
}

/// <summary>
/// Круглая кнопка как в приложении «Часы» на iPhone. Реакция на нажатие мгновенная (чуть сжимается), наведение подсвечивает.
/// Цвета: белый и серый; оранжевый — только для таймера и секундомера.
/// </summary>
internal sealed class RoundButton : Button
{
    public const double Size = 58;

    private static SolidColorBrush Argb(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public RoundButton(string text, RoundStyle style)
    {
        var orange = style == RoundStyle.Orange;
        var normal = orange ? Argb(0x40, 0xFF, 0x9F, 0x0A) : Argb(0x33, 0xFF, 0xFF, 0xFF);
        var hover = orange ? Argb(0x5C, 0xFF, 0x9F, 0x0A) : Argb(0x4D, 0xFF, 0xFF, 0xFF);

        var bg = new FrameworkElementFactory(typeof(Border), "Bg");
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(Size / 2));
        bg.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        bg.SetValue(RenderTransformOriginProperty, new Point(0.5, 0.5));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(MarginProperty, new Thickness(0, 0, 0, 1));
        bg.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = bg };
        var onHover = new Trigger { Property = IsMouseOverProperty, Value = true };
        onHover.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Bg"));
        template.Triggers.Add(onHover);
        var onPress = new Trigger { Property = IsPressedProperty, Value = true };
        onPress.Setters.Add(new Setter(RenderTransformProperty, new ScaleTransform(0.96, 0.96), "Bg"));
        template.Triggers.Add(onPress);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.35, "Bg"));
        template.Triggers.Add(disabled);

        Template = template;
        Content = text;
        Width = Height = Size;
        FontSize = 13;
        FontWeight = FontWeights.SemiBold;
        Background = normal;
        Foreground = orange ? ClockStyle.Orange : Hues.Ink;
        Cursor = Cursors.Hand;
        Focusable = false;
    }
}

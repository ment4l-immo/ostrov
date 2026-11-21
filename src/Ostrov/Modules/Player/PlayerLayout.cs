using System.Windows;
using System.Windows.Controls;

namespace Ostrov.Modules.Player;

/// <summary>
/// Раскладка плеера с плавным переходом между обычным видом и режимом текста (Blend 0..1, его ведёт пружина).
/// Обычный вид: слева обложка, справа название, исполнитель, кнопки, полоса со временем (позиции не зависят от высоты).
/// Режим текста: слева обложка, под ней исполнитель, под ним название; справа текст песни, под ним полоса со временем,
/// внизу кнопки (привязаны к низу, поэтому едут вниз вместе с растущим островом). Названия справа и слева — разные элементы
/// с быстрой перекрёстной сменой: перенос слов в узкой колонке не «дёргается» во время движения.
/// Слева под обложкой порядок: название, под ним исполнитель, ниже серый блок о треке (альбом, год, жанр, номер).
/// </summary>
internal sealed class PlayerLayout : Panel
{
    public const double CoverSize = 128, RightX = 148, NormalHeight = 154, BottomPad = 22, Gap = 6;

    private readonly UIElement _cover, _titleR, _artistR, _titleL, _artistL, _info, _controls, _progress, _times, _lyrics;
    private double _blend;

    /// <param name="info">Серый блок о треке: виден только в режиме текста.</param>
    /// <param name="lyrics">Текст песни: виден только в режиме текста.</param>
    public PlayerLayout(UIElement cover, UIElement titleRight, UIElement artistRight, UIElement titleLeft, UIElement artistLeft, UIElement info,
        UIElement controls, UIElement progress, UIElement times, UIElement lyrics)
    {
        _cover = cover; _titleR = titleRight; _artistR = artistRight; _titleL = titleLeft; _artistL = artistLeft; _info = info;
        _controls = controls; _progress = progress; _times = times; _lyrics = lyrics;
        foreach (var e in new[] { lyrics, cover, titleRight, artistRight, titleLeft, artistLeft, info, progress, times, controls })
            Children.Add(e);
    }

    /// <summary>0 — обычный вид, 1 — режим текста.</summary>
    public double Blend
    {
        get => _blend;
        set
        {
            if (Math.Abs(_blend - value) < 1e-4) return;
            _blend = value;
            InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? 472 : available.Width;
        var h = double.IsInfinity(available.Height) ? NormalHeight : available.Height;
        var rightW = Math.Max(0, w - RightX);
        _cover.Measure(new Size(CoverSize, CoverSize));
        foreach (var e in new[] { _titleR, _artistR, _controls, _progress, _times })
            e.Measure(new Size(rightW, double.PositiveInfinity));
        _titleL.Measure(new Size(CoverSize, double.PositiveInfinity));
        _artistL.Measure(new Size(CoverSize, double.PositiveInfinity));
        _info.Measure(new Size(CoverSize, double.PositiveInfinity));
        _lyrics.Measure(new Size(rightW, Math.Max(0, h)));
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double w = final.Width, h = final.Height, t = _blend, rightW = Math.Max(0, w - RightX);
        double hTitle = _titleR.DesiredSize.Height, hArtist = _artistR.DesiredSize.Height, hControls = _controls.DesiredSize.Height;
        double hProgress = _progress.DesiredSize.Height, hTimes = _times.DesiredSize.Height;

        // Обычный вид: блок справа по центру воображаемой высоты NormalHeight (не зависит от реальной высоты).
        var stackH = hTitle + hArtist + Gap + hControls + hProgress + hTimes;
        var top = (NormalHeight - stackH) / 2;
        double nTitleY = top, nArtistY = top + hTitle, nControlsY = nArtistY + hArtist + Gap;
        double nProgressY = nControlsY + hControls, nTimesY = nProgressY + hProgress;
        var nCoverY = (NormalHeight - CoverSize) / 2;

        // Режим текста: кнопки у нижнего края, над ними время и полоса, над ними текст.
        var lControlsY = h - BottomPad - hControls;
        var lTimesY = lControlsY - Gap - hTimes;
        var lProgressY = lTimesY - hProgress;
        var lyricsHeight = Math.Max(0, lProgressY - 10);
        var lTitleY = CoverSize + 10;
        var lArtistY = lTitleY + _titleL.DesiredSize.Height + 3;
        var lInfoY = lArtistY + _artistL.DesiredSize.Height + 14;

        _cover.Arrange(new Rect(0, Lerp(nCoverY, 0, t), CoverSize, CoverSize));
        _titleR.Arrange(new Rect(RightX, nTitleY, rightW, hTitle));
        _artistR.Arrange(new Rect(RightX, nArtistY, rightW, hArtist));
        _artistL.Arrange(new Rect(0, lArtistY, CoverSize, _artistL.DesiredSize.Height));
        _titleL.Arrange(new Rect(0, lTitleY, CoverSize, _titleL.DesiredSize.Height));
        _info.Arrange(new Rect(0, lInfoY, CoverSize, _info.DesiredSize.Height));
        _controls.Arrange(new Rect(RightX, Lerp(nControlsY, lControlsY, t), rightW, hControls));
        _progress.Arrange(new Rect(RightX, Lerp(nProgressY, lProgressY, t), rightW, hProgress));
        _times.Arrange(new Rect(RightX, Lerp(nTimesY, lTimesY, t), rightW, hTimes));
        _lyrics.Arrange(new Rect(RightX, 0, rightW, lyricsHeight));

        // Быстрая перекрёстная смена подписей и появление текста во второй половине движения.
        var right = 1 - Smooth(t / 0.4);
        var left = Smooth((t - 0.5) / 0.5);
        var text = Smooth((t - 0.45) / 0.55);
        // Полоса со временем и кнопки меняются местами по вертикали и в середине движения прошли бы друг через друга:
        // полоса и время к середине гаснут и проявляются в конце.
        _progress.Opacity = _times.Opacity = Smooth(Math.Abs(2 * t - 1) * 1.25);
        _titleR.Opacity = _artistR.Opacity = right;
        _titleL.Opacity = _artistL.Opacity = _info.Opacity = left;
        _lyrics.Opacity = text;
        _titleR.IsHitTestVisible = _artistR.IsHitTestVisible = right > 0.5;
        _lyrics.IsHitTestVisible = t > 0.9;
        var lyricsVisibility = t > 0.02 ? Visibility.Visible : Visibility.Collapsed;
        if (_lyrics.Visibility != lyricsVisibility) _lyrics.Visibility = lyricsVisibility;
        var leftVisibility = t > 0.02 ? Visibility.Visible : Visibility.Hidden;
        if (_titleL.Visibility != leftVisibility) _titleL.Visibility = _artistL.Visibility = _info.Visibility = leftVisibility;
        return final;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}

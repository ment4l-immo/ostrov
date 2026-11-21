using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Ostrov.Backdrop;

/// <summary>
/// Размытие фона под островом (решение пользователя 2026-11-20: остров чуть просвечивает, фон за ним размыт).
/// Окно острова рисуется «по пикселям» (AllowsTransparency), а такие окна системное размытие не поддерживают. Поэтому размытие делает отдельное
/// безрамочное окно прямо под островом: оно не рисует ничего своего, Windows размывает то, что лежит под ним (ACCENT_ENABLE_BLURBEHIND).
/// Проверено на этом компьютере (2026-11-21): акриловое (State 4) и обычное (State 3) размытие закрывают весь прямоугольник окна и область окна (SetWindowRgn)
/// игнорируют (с системным фоном DWMWA_SYSTEMBACKDROP_TYPE область работает, но у неактивного окна вместо размытия серая заглушка). Поэтому остров размывают
/// несколько прямоугольных окон «лесенкой» по дуге нижних скруглений (<see cref="SetBounds"/>, раскладка в MainWindow.Blur.cs): размытие идёт от края до края,
/// а над дугой остаётся лишь зазор в пару пикселей. Окно не берёт фокус и пропускает клики.
/// </summary>
internal sealed class BlurBehind : Window
{
    private IntPtr _hwnd;
    private (int X, int Y, int W, int H) _bounds;

    public BlurBehind()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Background = Brushes.Transparent;
        Left = -32000; Top = -32000; Width = 8; Height = 8; // до первого SetBounds окно не должно никому мешать
        SourceInitialized += (_, _) => Setup();
    }

    /// <summary>Положение и размер в физических пикселях экрана. Тот же прямоугольник подряд повторно не применяется.</summary>
    public void SetBounds(int x, int y, int w, int h)
    {
        if (_hwnd == IntPtr.Zero || (x, y, w, h) == _bounds) return;
        _bounds = (x, y, w, h);
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, Math.Max(1, w), Math.Max(1, h), SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Спрятать (остров свёрнут до нуля) без закрытия окна.</summary>
    public void Park() => SetBounds(-32000, -32000, 8, 8);

    /// <summary>Поставить окно сразу под окном острова (оба «поверх всех», размытие должно лежать прямо под островом).</summary>
    public void PutBelow(IntPtr aboveWindow)
    {
        if (_hwnd == IntPtr.Zero || aboveWindow == IntPtr.Zero) return;
        SetWindowPos(_hwnd, aboveWindow, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void Setup()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);

        // Окно без «пикселей»: фон рисуется системой. Прозрачный фон и стекло на всё окно, углы системой не скругляются.
        HwndSource.FromHwnd(_hwnd)!.CompositionTarget.BackgroundColor = Colors.Transparent;
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        var noRound = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(_hwnd, 33, ref noRound, sizeof(int));

        ApplyAccent();
    }

    // Акриловое размытие того, что лежит под окном. Цвет почти прозрачный (альфа 1, серый): у акрила он подмешивается к размытию, но при такой альфе не закрашивает его.
    private void ApplyAccent()
    {
        if (_hwnd == IntPtr.Zero) return;
        var accent = new AccentPolicy { State = 4, GradientColor = unchecked((int)0x01808080) };
        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, Size = size };
            SetWindowCompositionAttribute(_hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int Size; }

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}

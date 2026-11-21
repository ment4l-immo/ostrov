using System.Runtime.InteropServices;

namespace Ostrov.Services;

/// <summary>
/// Средняя яркость узких участков экрана (физические пиксели). Нужна, чтобы понять, тёмный ли фон вокруг острова.
/// Пиксели читаются только в память и сразу сворачиваются в одно число: ничего не сохраняется и не уходит наружу.
/// Окно острова прозрачное, поэтому участки рядом с ним показывают настоящий фон, а не сам остров.
/// </summary>
public static class BackdropSampler
{
    /// <summary>Средняя яркость 0..1 (0 — чёрный) по всем участкам вместе; −1, если прочитать не удалось.</summary>
    public static double MeanLuminance(IReadOnlyList<(int X, int Y, int W, int H)> rects)
    {
        var screenW = GetSystemMetrics(SM_CXSCREEN);
        var screenH = GetSystemMetrics(SM_CYSCREEN);
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return -1;
        var memDc = CreateCompatibleDC(screenDc);
        double sum = 0;
        long count = 0;
        try
        {
            foreach (var (rx, ry, rw, rh) in rects)
            {
                // Вне экрана BitBlt отдаёт чёрное и исказил бы результат, поэтому обрезаем по экрану.
                var x0 = Math.Max(rx, 0);
                var y0 = Math.Max(ry, 0);
                var x1 = Math.Min(rx + rw, screenW);
                var y1 = Math.Min(ry + rh, screenH);
                var w = x1 - x0;
                var h = y1 - y0;
                if (w <= 0 || h <= 0) continue;

                var info = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = w,
                        biHeight = -h, // сверху вниз
                        biPlanes = 1,
                        biBitCount = 32
                    }
                };
                var dib = CreateDIBSection(memDc, ref info, 0, out var bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) continue;
                var old = SelectObject(memDc, dib);
                try
                {
                    if (!BitBlt(memDc, 0, 0, w, h, screenDc, x0, y0, SRCCOPY)) continue;
                    var buffer = new byte[w * h * 4];
                    Marshal.Copy(bits, buffer, 0, buffer.Length);
                    // Каждый второй пиксель по обеим осям: для средней яркости хватает с запасом.
                    for (var j = 0; j < h; j += 2)
                    {
                        var row = j * w * 4;
                        for (var i = 0; i < w; i += 2)
                        {
                            var p = row + i * 4; // порядок байт: B, G, R, A
                            sum += (0.0722 * buffer[p] + 0.7152 * buffer[p + 1] + 0.2126 * buffer[p + 2]) / 255.0;
                            count++;
                        }
                    }
                }
                finally
                {
                    SelectObject(memDc, old);
                    DeleteObject(dib);
                }
            }
        }
        finally
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        return count == 0 ? -1 : sum / count;
    }

    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // для 32 бит не используется
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hDc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDc);
}

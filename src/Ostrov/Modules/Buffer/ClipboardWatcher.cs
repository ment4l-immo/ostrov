using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ostrov.Modules.Buffer;

/// <summary>
/// Следит за буфером обмена (скрытое окно-приёмник сообщений Windows) и пишет текст и картинки в историю.
/// Файлы из проводника не берёт. То, что менеджеры паролей помечают секретным, пропускает.
/// </summary>
internal sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxPixels = 40_000_000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly ClipboardHistory _history;
    private HwndSource? _source;
    private int _version;
    private long _selfSetAt;

    public ClipboardWatcher(ClipboardHistory history) => _history = history;

    public void Start()
    {
        if (_source != null) return;
        // Окно только для сообщений: на экране его нет.
        _source = new HwndSource(0, 0, 0, 0, 0, "OstrovClipboard", HwndMessage);
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_source.Handle);
    }

    /// <summary>Остров сам положил запись в буфер: ближайшее изменение буфера игнорируем.</summary>
    public void MarkSelfSet() => _selfSetAt = Environment.TickCount64;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) _ = HandleChangeAsync();
        return IntPtr.Zero;
    }

    private async Task HandleChangeAsync()
    {
        var version = ++_version;
        await Task.Delay(60); // приложения часто меняют буфер несколькими сообщениями подряд
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (version != _version) return; // пришло более свежее изменение
            if (Environment.TickCount64 - _selfSetAt < 1500) return;
            try { Capture(); return; }
            catch (ExternalException) { await Task.Delay(100); } // буфер занят другой программой
            catch { return; }
        }
    }

    private void Capture()
    {
        if (IsSecret()) return;
        if (Clipboard.ContainsText())
        {
            _history.AddText(Clipboard.GetText());
        }
        else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            _ = AddImageAsync(image);
        }
    }

    private static bool IsSecret()
    {
        try
        {
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return true;
            if (Clipboard.GetData("CanIncludeInClipboardHistory") is Stream s)
            {
                var bytes = new byte[4];
                if (s.Read(bytes, 0, 4) == 4 && BitConverter.ToInt32(bytes, 0) == 0) return true;
            }
        }
        catch (Exception ex) when (ex is not ExternalException) { }
        return false;
    }

    private async Task AddImageAsync(BitmapSource image)
    {
        // Пиксели читаем в потоке интерфейса (картинка из буфера принадлежит ему), тяжёлое — в фоне.
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) return;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var hash = await Task.Run(() => Convert.ToHexString(SHA256.HashData(pixels)));
        if (_history.BumpImage(hash)) return;

        var png = await Task.Run(() =>
        {
            var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            frame.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        });
        _history.AddImage(hash, width, height, png);
    }

    public void Dispose()
    {
        if (_source == null) return;
        RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

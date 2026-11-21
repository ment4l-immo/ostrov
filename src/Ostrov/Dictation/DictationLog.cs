using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Ostrov.Dictation;

/// <summary>
/// Короткий журнал диктовки для разбора неполадок: одна строка на запись (длительность, сколько звука ушло, самый громкий момент, сколько фраз
/// вернулось, сколько символов напечатано и в какую программу). Самой речи и распознанного текста в журнале нет. Файл
/// <c>%AppData%\Ostrov\dictation.log</c>, не больше ≈ 100 КБ. Если запущено с переменной окружения <c>OSTROV_DEBUG_DICTATION=1</c>,
/// последняя запись звука ещё сохраняется в <c>dictation-last.wav</c> (перезаписывается каждый раз; только на этом компьютере).
/// </summary>
internal static class DictationLog
{
    public static readonly bool DumpAudio = Environment.GetEnvironmentVariable("OSTROV_DEBUG_DICTATION") == "1";

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov");

    public static void Line(string text)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, "dictation.log");
            if (File.Exists(path) && new FileInfo(path).Length > 100_000)
            {
                var keep = File.ReadAllLines(path, Encoding.UTF8);
                File.WriteAllLines(path, keep[(keep.Length / 2)..], Encoding.UTF8);
            }
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}\r\n", Encoding.UTF8);
        }
        catch { /* журнал не должен мешать диктовке */ }
    }

    /// <summary>Имя программы, в которой сейчас фокус (только имя процесса).</summary>
    public static string ForegroundApp()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "?";
            GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return "?"; }
    }

    /// <summary>Сохранить звук как WAV (PCM16, 24 кГц, моно). Только в режиме отладки.</summary>
    public static void SaveWav(byte[] pcm)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            using var f = File.Create(Path.Combine(Dir, "dictation-last.wav"));
            using var w = new BinaryWriter(f);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(MicCapture.SampleRate); w.Write(MicCapture.SampleRate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}

using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Ostrov.Services;

namespace Ostrov.Modules.Buffer;

/// <summary>
/// Всё, что работает независимо от открытой вкладки «Буфер»: слежение за буфером обмена и приём файлов на полку.
/// Запускается при старте острова (<see cref="Start"/>), вкладка только показывает данные.
/// </summary>
internal sealed class ClipboardService : IDisposable
{
    public static ClipboardService Instance { get; } = new(new ClipboardHistory(), new ShelfStore());

    public ClipboardHistory History { get; }
    public ShelfStore Shelf { get; }

    private readonly ClipboardWatcher _watcher;
    private bool _started;

    /// <summary>Хранилища можно подменить (для проверки без настоящих файлов пользователя).</summary>
    internal ClipboardService(ClipboardHistory history, ShelfStore shelf)
    {
        History = history;
        Shelf = shelf;
        _watcher = new ClipboardWatcher(history);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _watcher.Start();
        IslandBus.FilesDropped += paths => Shelf.Add(paths);
        IslandBus.SecretCopied += Forget;
    }

    // Секрет (ключ API), который вставили в остров: убираем его из истории и, если он ещё лежит в буфере обмена, очищаем буфер.
    private void Forget(string text)
    {
        History.RemoveText(text);
        try
        {
            _watcher.MarkSelfSet();
            if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear();
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* буфер занят: история уже очищена */ }
    }

    /// <summary>
    /// Положить запись обратно в буфер обмена и поднять её наверх истории. Если буфер на миг занят другой программой,
    /// пробуем ещё несколько раз. false — не вышло (или файл картинки пропал).
    /// </summary>
    public bool Restore(ClipEntry entry)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _watcher.MarkSelfSet();
                if (entry.Kind == ClipKind.Text)
                {
                    Clipboard.SetText(entry.Text ?? "");
                }
                else
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(History.ImagePath(entry));
                    image.EndInit();
                    image.Freeze();
                    Clipboard.SetImage(image);
                }
                break;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException or NotSupportedException)
            {
                if (attempt >= 3) return false;
                Thread.Sleep(60);
            }
        }
        _watcher.MarkSelfSet();
        History.MoveToTop(entry);
        return true;
    }

    public void Dispose() => _watcher.Dispose();
}

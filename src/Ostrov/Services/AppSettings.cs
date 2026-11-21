using System.IO;
using System.Text.Json;

namespace Ostrov.Services;

/// <summary>Настройки острова. Лежат в %AppData%\Ostrov\settings.json. Паролей и токенов здесь нет.</summary>
public sealed class AppSettings
{
    /// <summary>Автозапуск уже включали по умолчанию при первом запуске (дальше решает пользователь).</summary>
    public bool AutostartInitialized { get; set; }

    /// <summary>Город для погоды (выбирается в поиске во вкладке «Погода»).</summary>
    public string? WeatherCity { get; set; }
    public double WeatherLatitude { get; set; }
    public double WeatherLongitude { get; set; }

    /// <summary>Список проектов (папок) для быстрого открытия в VS Code.</summary>
    public List<ProjectEntry> Projects { get; set; } = [];

    /// <summary>Модель OpenAI для ответов в быстром поиске (если не задана — gpt-5-mini). Ключ здесь не хранится: он лежит отдельно, в зашифрованном виде.</summary>
    public string? OpenAiModel { get; set; }

    /// <summary>Полка для файлов (вкладка «Буфер»): пути, новые сверху. Сами файлы не копируются.</summary>
    public List<string> Shelf { get; set; } = [];

    /// <summary>Через сколько секунд полной неподвижности мыши раскрытый остров сворачивается сам; 0 — не сворачивать сам (по умолчанию 3, М-010).</summary>
    public int AutoCollapseSeconds { get; set; } = 3;

    /// <summary>Ночной режим: с 22:00 до 07:00 фоны слабее, белое мягче (≈ 80 %). По умолчанию включён.</summary>
    public bool NightMode { get; set; } = true;

    /// <summary>Насколько остров просвечивает: 0 — слабо (88 % непрозрачности), 1 — средне (80 %), 2 — сильно (72 %).</summary>
    public int GlassLevel { get; set; } = 1;

    /// <summary>Размывать фон под островом (отдельное окно под островом, см. BlurBehind).</summary>
    public bool BlurBackdrop { get; set; } = true;

    /// <summary>Восход и закат города (часы от полуночи), запомненные с последней загрузки погоды: «свет дня» на плашке при запуске берёт их, не дожидаясь сети.</summary>
    public double? LastSunRise { get; set; }
    public double? LastSunSet { get; set; }

    /// <summary>Свои слова и выражения для диктовки (через запятую): уходят подсказкой модели распознавания, чтобы жаргон записывался правильно.</summary>
    public string? DictationWords { get; set; }

    /// <summary>Порог предупреждения о нехватке места на диске, ГБ (меньше этого свободно на любом локальном диске: плашка на острове).</summary>
    public int DiskWarnGb { get; set; } = 10;

    /// <summary>Тригонометрия в калькуляторе Alt+Q считает в градусах (true) или в радианах (false).</summary>
    public bool TrigInDegrees { get; set; } = true;

    /// <summary>Горячая клавиша быстрого вопроса в виде «Alt+Q»; null — по умолчанию Alt+Q.</summary>
    public string? SearchHotkey { get; set; }

    /// <summary>Программы, над которыми свёрнутый остров сжимается в нотч (раздел «Программы» в настройках, М-006); null — по умолчанию браузеры.</summary>
    public List<AppListEntry>? ShrinkApps { get; set; }

    /// <summary>Приложения телефона, о которых остров говорит «Новое сообщение в X» (вкладка «Телефон»); null/нет записи — по умолчанию мессенджеры.</summary>
    public List<PhoneAppRule>? PhoneApps { get; set; }

    /// <summary>Тоннель до своего сервера для запросов к OpenAI без VPN (раздел «Сервер» в настройках, см. <see cref="ServerTunnel"/>). Секретов тут нет: приватный ключ лежит отдельным файлом.</summary>
    public bool TunnelEnabled { get; set; }
    public string? TunnelHost { get; set; }
    public string? TunnelUser { get; set; }
    public int TunnelPort { get; set; } = 22;

    /// <summary>Вспышки про телефон на свёрнутом острове (подключился, низкий заряд, новое сообщение); выключатель в шапке вкладки «Телефон». По умолчанию включены.</summary>
    public bool PhoneFlashes { get; set; } = true;

    /// <summary>Голосовая диктовка (зажми правый Alt и говори): включена ли. По умолчанию да.</summary>
    public bool DictationEnabled { get; set; } = true;

    /// <summary>Клавиша диктовки: код клавиши Windows (по умолчанию правый Alt, 0xA5); см. <c>DictationKeys</c>.</summary>
    public int DictationKey { get; set; } = 0xA5;

    /// <summary>Вести локальный журнал дня для «итогов дня» (программы в фокусе, музыка, Claude). По умолчанию да.</summary>
    public bool DayJournalEnabled { get; set; } = true;

    /// <summary>Микрофон для диктовки: идентификатор устройства Windows; null — микрофон по умолчанию в Windows.</summary>
    public string? DictationMic { get; set; }

    /// <summary>Модель распознавания речи для диктовки; null — по умолчанию (gpt-4o-mini-transcribe).</summary>
    public string? DictationModel { get; set; }

    /// <summary>Когда в «Телефоне» нажали «Очистить»: уведомления старше этого момента во вкладке не показываются.</summary>
    public DateTime? PhoneClearedAt { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov", "settings.json");

    // Файл настроек читают из десятков мест (в том числе по таймерам): текст помним, пока файл не изменился (время записи и размер),
    // а объект каждый раз собираем заново, потому что вызывающие меняют его и сохраняют.
    private static string? _cachedText;
    private static DateTime _cachedStamp;
    private static long _cachedLength = -1;
    private static readonly object CacheLock = new();

    public static AppSettings Load()
    {
        try
        {
            var info = new FileInfo(FilePath);
            string text;
            lock (CacheLock)
            {
                if (_cachedText == null || !info.Exists || info.LastWriteTimeUtc != _cachedStamp || info.Length != _cachedLength)
                {
                    _cachedText = File.ReadAllText(FilePath);
                    _cachedStamp = info.LastWriteTimeUtc;
                    _cachedLength = info.Length;
                }
                text = _cachedText;
            }
            return JsonSerializer.Deserialize<AppSettings>(text) ?? new();
        }
        catch
        {
            // Файла нет (первый запуск): настройки по умолчанию. Файл есть, но не прочитался (его в этот момент заменяют или он повреждён):
            // отдаём последние прочитанные, иначе следующий Save() затёр бы всё настоящее значениями по умолчанию.
            try
            {
                if (!File.Exists(FilePath)) return new();
                string? last;
                lock (CacheLock) last = _cachedText;
                if (last != null) return JsonSerializer.Deserialize<AppSettings>(last) ?? new();
            }
            catch { /* ниже: настройки по умолчанию */ }
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Через временный файл: сбой посреди записи не обрезает настоящий settings.json.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            // Читатель может держать файл долю секунды: пробуем ещё пару раз.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, FilePath, overwrite: true); break; }
                catch (IOException) when (attempt < 4) { Thread.Sleep(25); }
            }
        }
        catch { /* не удалось сохранить — не критично, остров продолжит работать */ }
    }
}

/// <summary>Правило для приложения телефона: название (как в уведомлении) и говорит ли о нём остров.</summary>
public sealed class PhoneAppRule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
}

/// <summary>Одна папка проекта в списке. Порядок в списке — по последнему открытию (или по добавлению, если ещё не открывали).</summary>
public sealed class ProjectEntry
{
    public string Path { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? LastOpened { get; set; }

    /// <summary>Закреплён: всегда выше остальных (внутри закреплённых порядок прежний, по недавности).</summary>
    public bool Pinned { get; set; }
}

/// <summary>Программа в списке настроек: имя процесса (без .exe), название для показа и включена ли она.</summary>
public sealed class AppListEntry
{
    public string Process { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Полный путь к exe (нужен только ради значка в списке); у стандартных программ пуст.</summary>
    public string? Path { get; set; }
}

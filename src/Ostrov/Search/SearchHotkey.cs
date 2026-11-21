using System.Windows.Input;

namespace Ostrov.Search;

/// <summary>Сочетание клавиш для быстрого вопроса: модификаторы Windows (<c>RegisterHotKey</c>) и код клавиши. По умолчанию Alt+Q.</summary>
internal readonly record struct SearchHotkey(uint Modifiers, uint Vk)
{
    public const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    public static SearchHotkey Default { get; } = new(ModAlt, 0x51); // Alt+Q

    /// <summary>Из записи в настройках («Alt+Q», «Ctrl+Alt+Space»); при ошибке — null.</summary>
    public static SearchHotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0;
        Key? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "alt": mods |= ModAlt; break;
                case "ctrl" or "control": mods |= ModControl; break;
                case "shift": mods |= ModShift; break;
                case "win": mods |= ModWin; break;
                default:
                    if (key != null || !Enum.TryParse<Key>(part, true, out var parsed)) return null;
                    key = parsed;
                    break;
            }
        }
        if (key == null || !HasMainModifier(mods)) return null;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return vk == 0 ? null : new SearchHotkey(mods, vk);
    }

    /// <summary>Из нажатия в окне настроек. Null — это ещё не сочетание (нет клавиши или нет Alt/Ctrl/Win).</summary>
    public static SearchHotkey? FromKeyboard(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None or Key.DeadCharProcessed or Key.ImeProcessed) return null;
        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= ModWin;
        if (!HasMainModifier(mods)) return null; // одного Shift или голой клавиши мало: перехватили бы обычный ввод
        if (!IsAllowed(key, mods)) return null;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk == 0 ? null : new SearchHotkey(mods, vk);
    }

    private static bool HasMainModifier(uint mods) => (mods & (ModAlt | ModControl | ModWin)) != 0;

    /// <summary>
    /// Нельзя отнимать у программ привычные сочетания: Ctrl+C, Ctrl+V и подобное (Ctrl без Alt и Win), а также Alt+F4, Alt+Tab, Alt+Esc.
    /// Ctrl вместе с функциональной клавишей F1–F12 оставлен: им почти никто не пользуется.
    /// </summary>
    public static bool IsAllowed(Key key, ModifierKeys modifiers)
    {
        uint mods = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= ModWin;
        return IsAllowed(key, mods);
    }

    private static bool IsAllowed(Key key, uint mods)
    {
        var hasAltOrWin = (mods & (ModAlt | ModWin)) != 0;
        if (!hasAltOrWin) return key is >= Key.F1 and <= Key.F12;
        if ((mods & ModAlt) != 0 && (mods & ModControl) == 0 && (mods & ModWin) == 0 && key is Key.F4 or Key.Tab or Key.Escape) return false;
        return true;
    }

    /// <summary>Читаемая запись и она же в настройках: «Alt+Q», «Ctrl+Alt+Space».</summary>
    public string Format()
    {
        var parts = new List<string>();
        if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & ModShift) != 0) parts.Add("Shift");
        if ((Modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyInterop.KeyFromVirtualKey((int)Vk).ToString());
        return string.Join("+", parts);
    }
}

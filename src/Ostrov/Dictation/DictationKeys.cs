namespace Ostrov.Dictation;

/// <summary>
/// Какие клавиши подходят для «зажми и говори» и как они называются. Берём клавиши, которые почти не нужны сами по себе:
/// правые Ctrl, Alt и Shift, F1–F24, Pause, Scroll Lock, Insert и клавишу меню. Левые Ctrl, Alt и Shift, буквы и Win не годятся:
/// они нужны для обычной работы (Win ещё и открывает «Пуск» при отпускании).
/// </summary>
internal static class DictationKeys
{
    public const int Default = PushToTalkLogic.VkRMenu; // правый Alt

    public static bool IsAllowed(int vk) => vk switch
    {
        0xA1 or 0xA3 or 0xA5 => true,       // правые Shift, Ctrl, Alt
        0x13 or 0x91 or 0x2D or 0x5D => true, // Pause, Scroll Lock, Insert, Menu
        >= 0x70 and <= 0x87 => true,         // F1–F24
        _ => false
    };

    public static string Name(int vk) => vk switch
    {
        0xA1 => "Правый Shift",
        0xA3 => "Правый Ctrl",
        0xA5 => "Правый Alt",
        0x13 => "Pause",
        0x91 => "Scroll Lock",
        0x2D => "Insert",
        0x5D => "Menu",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => "Клавиша " + vk
    };

    /// <summary>Клавиша из «расширенной» половины клавиатуры: при проигрывании нажатия системе ей нужен соответствующий признак.</summary>
    public static bool IsExtended(int vk) => vk is 0xA3 or 0xA5 or 0x2D or 0x5D;
}

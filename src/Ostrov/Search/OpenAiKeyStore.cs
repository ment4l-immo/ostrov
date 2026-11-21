using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Ostrov.Search;

/// <summary>
/// Ключ OpenAI. Лежит отдельно от настроек в %AppData%\Ostrov\openai.key, зашифрованный средствами Windows (DPAPI, только для этого
/// пользователя этого компьютера): другой пользователь или другой компьютер его не прочтут. В коде, в git и в логах ключа нет.
/// Переменная окружения OPENAI_API_KEY, если задана, имеет приоритет (ничего не сохраняется).
/// </summary>
internal static class OpenAiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Ostrov.OpenAI.v1");

    /// <summary>Другой путь к файлу ключа. Только для проверок: чтобы они не трогали настоящий ключ пользователя.</summary>
    internal static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ostrov", "openai.key");

    /// <summary>Простая проверка, что строка похожа на ключ OpenAI (не обращается к сети).</summary>
    public static bool LooksLikeKey(string? text) =>
        text is { Length: >= 20 } t && t.StartsWith("sk-", StringComparison.Ordinal) && !t.Any(char.IsWhiteSpace);

    public static string? Load()
    {
        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plain).Trim();
            return key.Length > 0 ? key : null;
        }
        catch { return null; }
    }

    public static bool Save(string key)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch { return false; }
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }
}

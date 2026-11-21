# Хук PermissionRequest для Claude Code: передаёт запрос разрешения на остров и ждёт ответа «разрешить/запретить».
# Если остров не запущен (канал недоступен) или ответа нет, хук молча завершается: решает обычное окно VS Code.
# Кириллицу в этот файл не вставлять: PowerShell 5.1 читает .ps1 без BOM как ANSI. Текст ответов приходит от острова.
#
# Протокол (именованный канал ostrov-claude-bridge, UTF-8, по строкам):
#   хук -> остров: одна строка, ввод хука (JSON);
#   остров -> хук: одна строка, готовый вывод хука (JSON) или пустая («не решать»).
# Если ответили в окне VS Code, остров находит в файле переписки результат этого вызова, отпускает хук пустой строкой
# и убирает карточку. Если хук убили (таймаут) или он отключился, остров по закрытию связи тоже убирает карточку.
$ErrorActionPreference = 'SilentlyContinue'
$utf8 = New-Object System.Text.UTF8Encoding $false

$stdin = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
$raw = $stdin.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

$pipeName = $env:OSTROV_PIPE   # только для проверок; обычно не задана
if (-not $pipeName) { $pipeName = 'ostrov-claude-bridge' }
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
try { $pipe.Connect(300) } catch { exit 0 }   # остров не запущен

$answer = $null
try {
    $writer = New-Object System.IO.StreamWriter($pipe, $utf8)
    $writer.AutoFlush = $true
    $writer.WriteLine(($raw -replace '[\r\n]+', ' '))

    # Ждём ответ острова. Если ответили в окне VS Code, остров сам заметит это по файлу переписки и отпустит хук пустой строкой.
    $reader = New-Object System.IO.StreamReader($pipe, $utf8)
    $answer = $reader.ReadLine()
} catch {}

if (-not [string]::IsNullOrEmpty($answer)) {
    $bytes = $utf8.GetBytes($answer)
    $out = [Console]::OpenStandardOutput()
    $out.Write($bytes, 0, $bytes.Length)
    $out.Flush()
}
try { $pipe.Dispose() } catch {}
exit 0

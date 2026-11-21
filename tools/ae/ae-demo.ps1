# Показывает на острове состояния рендера After Effects без самого AE (для проверки вида).
# Запуск (политика PowerShell у пользователя запрещает .ps1, поэтому так):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/ae/ae-demo.ps1 cycle
# Режимы:
#   cycle        идёт рендер 8 секунд, потом сам «готово» (вся цепочка одной командой)
#   cycle-error  идёт рендер 8 секунд, потом сам «ошибка»
#   rendering    идёт рендер и так и висит, пока не дашь done / error / off (время считается от запуска команды)
#   done         галочка, error: восклицательный знак, off: убрать
# «Готово» и «ошибка» убираются кликом по острову. Демо-файл старше 30 минут остров игнорирует.
param([ValidateSet('cycle', 'cycle-error', 'rendering', 'done', 'error', 'off')][string]$State = 'cycle')

$dir = Join-Path $env:APPDATA 'Ostrov\ae-events'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$file = Join-Path $dir 'demo.txt'

function Start-Rendering { Set-Content -Path $file -Value ("rendering " + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) -Encoding ascii }

switch ($State) {
    'cycle'       { Start-Rendering; Start-Sleep -Seconds 8; Set-Content -Path $file -Value 'done' -Encoding ascii }
    'cycle-error' { Start-Rendering; Start-Sleep -Seconds 8; Set-Content -Path $file -Value 'error' -Encoding ascii }
    'rendering'   { Start-Rendering }
    'done'        { Set-Content -Path $file -Value 'done' -Encoding ascii }
    'error'       { Set-Content -Path $file -Value 'error' -Encoding ascii }
    'off'         { Remove-Item -Path $file -ErrorAction SilentlyContinue }
}
Write-Host "demo: $State"

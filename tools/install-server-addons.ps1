<#
.SYNOPSIS
    Ставит Metamod:Source и CounterStrikeSharp на установленный CS2-сервер.

.DESCRIPTION
    Распаковывает оба архива в game/csgo и прописывает Metamod в gameinfo.gi.
    Скрипт идемпотентен: повторный запуск не сломает gameinfo.gi и не создаст дублей.

.EXAMPLE
    .\tools\install-server-addons.ps1 -ServerRoot C:\cs2server -MetamodZip .\metamod.zip -CssZip .\css.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerRoot,
    [Parameter(Mandatory)][string]$MetamodZip,
    [Parameter(Mandatory)][string]$CssZip
)

$ErrorActionPreference = 'Stop'

$csgoDir = Join-Path $ServerRoot 'game\csgo'
$gameinfo = Join-Path $csgoDir 'gameinfo.gi'

if (-not (Test-Path $gameinfo)) { throw "Не найден $gameinfo — сервер ещё не установлен." }
foreach ($zip in @($MetamodZip, $CssZip)) {
    if (-not (Test-Path $zip)) { throw "Не найден архив: $zip" }
}

Write-Host 'Распаковка Metamod:Source...' -ForegroundColor Cyan
Expand-Archive -Path $MetamodZip -DestinationPath $csgoDir -Force

Write-Host 'Распаковка CounterStrikeSharp...' -ForegroundColor Cyan
Expand-Archive -Path $CssZip -DestinationPath $csgoDir -Force

# --- Прописываем Metamod в SearchPaths ---
# Движок грузит Metamod только если его путь стоит в gameinfo.gi ВЫШЕ основной папки csgo.
$metamodEntry = "			Game	csgo/addons/metamod"
$lines = Get-Content $gameinfo

if ($lines -match 'csgo/addons/metamod') {
    Write-Host 'gameinfo.gi уже содержит Metamod — пропускаю.' -ForegroundColor Yellow
}
else {
    $backup = "$gameinfo.backup"
    if (-not (Test-Path $backup)) {
        Copy-Item $gameinfo $backup
        Write-Host "Резервная копия: $backup" -ForegroundColor DarkGray
    }

    $result = New-Object System.Collections.Generic.List[string]
    $inserted = $false

    foreach ($line in $lines) {
        # Первая строка вида "Game<пробелы>csgo" — точка вставки.
        if (-not $inserted -and $line -match '^\s*Game\s+csgo\s*$') {
            $result.Add($metamodEntry)
            $inserted = $true
        }
        $result.Add($line)
    }

    if (-not $inserted) { throw 'Не нашёл строку "Game csgo" в gameinfo.gi — вставьте путь вручную.' }

    # Строго без BOM: движок парсит gameinfo.gi как KeyValues, и байты BOM в начале
    # приводят к "FATAL ERROR: unable to load gameinfo.gi". Set-Content -Encoding utf8
    # в Windows PowerShell 5.1 BOM добавляет, поэтому пишем через .NET напрямую.
    [System.IO.File]::WriteAllLines($gameinfo, $result, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host 'gameinfo.gi обновлён (UTF-8 без BOM).' -ForegroundColor Green
}

Write-Host ''
Write-Host 'Готово. Проверка после запуска сервера:' -ForegroundColor Green
Write-Host '  meta list          — должен показать CounterStrikeSharp'
Write-Host '  css_plugins list   — должен показать WarcraftMod'

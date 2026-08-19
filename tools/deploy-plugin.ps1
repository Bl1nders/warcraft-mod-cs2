<#
.SYNOPSIS
    Собирает WarcraftMod в Release и копирует его в папку плагинов CS2-сервера.

.EXAMPLE
    .\tools\deploy-plugin.ps1 -ServerRoot C:\cs2server
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServerRoot,
    [string]$DotnetRoot
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\WarcraftMod\WarcraftMod.csproj'
$pluginDir = Join-Path $ServerRoot 'game\csgo\addons\counterstrikesharp\plugins\WarcraftMod'

if (-not (Test-Path $project)) { throw "Не найден проект: $project" }

$cssRoot = Join-Path $ServerRoot 'game\csgo\addons\counterstrikesharp'
if (-not (Test-Path $cssRoot)) {
    throw "CounterStrikeSharp не установлен в $cssRoot — сначала поставьте Metamod и CSSharp."
}

# Нужен, только если .NET 10 стоит не в системном PATH.
if ($DotnetRoot) {
    $env:DOTNET_ROOT = $DotnetRoot
    $env:PATH = "$DotnetRoot;$env:PATH"
}

Write-Host 'Сборка Release...' -ForegroundColor Cyan
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }

$buildOutput = Join-Path $repoRoot 'src\WarcraftMod\bin\Release\net10.0'

if (-not (Test-Path $pluginDir)) { New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null }

# Конфиг и сейв игроков лежат в этой же папке — их перезаписывать нельзя.
$keep = @('warcraft_config.json', 'warcraft_players.json')

Get-ChildItem $buildOutput -File | Where-Object { $keep -notcontains $_.Name } | ForEach-Object {
    Copy-Item $_.FullName -Destination $pluginDir -Force
}

Write-Host "Плагин скопирован в $pluginDir" -ForegroundColor Green
Write-Host 'В консоли сервера выполните: css_plugins reload WarcraftMod' -ForegroundColor Yellow

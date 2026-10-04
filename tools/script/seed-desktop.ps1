# seed-desktop.ps1
# 把 Modbus TCP 设备 + 点位直接写入**运行中的 WPF 桌面端** SQLite 库
# （桌面端没有 REST API，所以按同一 schema 直连落库）。
# 桌面端 DeviceSnapshotCache TTL=10s，插入后约 10s 内自动加载，无需重启。
#
# 用法：
#   pwsh tools/script/seed-desktop.ps1 -Count 50
#   pwsh tools/script/seed-desktop.ps1 -Count 50 -ReplaceExisting   # 已存在则删后重灌点位
#   pwsh tools/script/seed-desktop.ps1 -Db D:\path\nitrogateway.db -Endpoint 127.0.0.1:15020

param(
    [string]$Db = "$env:LOCALAPPDATA\NitroGateway\nitrogateway.db",
    [string]$CsvDir = $PSScriptRoot,
    [string]$Endpoint = "127.0.0.1:15020",
    [int]$StartUnit = 1,
    [int]$Count = 50,
    [string]$NamePrefix = "LoadDev",
    [string]$SiteId = "",
    [switch]$ReplaceExisting
)

$ErrorActionPreference = "Stop"
$app = Join-Path $PSScriptRoot 'seed-desktop.cs'
if (-not (Test-Path -LiteralPath $app)) { throw "找不到 $app" }
if (-not (Test-Path -LiteralPath $Db)) { throw "桌面数据库不存在: $Db（先启动桌面端让它建库，或用 -Db 指定）" }

$argv = @('--db', $Db, '--csv-dir', $CsvDir, '--endpoint', $Endpoint,
          '--start-unit', $StartUnit, '--count', $Count, '--name-prefix', $NamePrefix)
if ($SiteId) { $argv += @('--site-id', $SiteId) }
if ($ReplaceExisting) { $argv += '--replace' }

& dotnet run $app -- @argv
exit $LASTEXITCODE

# gen-load-points.ps1
# 生成 N 从站 × M 点位的 points-device-NN.csv（NN = UnitId），供 ModbusSlaveSim --csv-dir 使用。
# 复用 tools/factory-test/gen-points-csv.ps1（单一生成器来源），默认输出到本目录。
#
# 用法：
#   pwsh tools/script/gen-load-points.ps1                         # 50 从站 × 100 点位（默认）
#   pwsh tools/script/gen-load-points.ps1 -SlaveCount 10 -PointsPerSlave 50
#   pwsh tools/script/gen-load-points.ps1 -Clean                  # 生成前清掉旧 points-device-*.csv

param(
    [int]$SlaveCount = 50,
    [int]$PointsPerSlave = 100,
    [int]$StartUnit = 1,
    [string]$OutDir = $PSScriptRoot,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$gen = Join-Path $PSScriptRoot '..\factory-test\gen-points-csv.ps1'
if (-not (Test-Path -LiteralPath $gen)) { throw "找不到生成器: $gen" }

if ($Clean) {
    Get-ChildItem -Path $OutDir -Filter 'points-device-*.csv' -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Write-Host "[clean] 已清除 $OutDir\points-device-*.csv"
}

& $gen -OutDir $OutDir -SlaveCount $SlaveCount -PointsPerSlave $PointsPerSlave -StartUnit $StartUnit

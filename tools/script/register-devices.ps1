# register-devices.ps1
# 批量注册 Modbus TCP 设备并导入对应点位 CSV。
#
# 约定：points-device-NN.csv 文件名中的 NN 即 UnitId，设备名 <NamePrefix>-NNN。
# 流程：登录 → 逐台 POST /api/devices（protocol=Modbus/TCP，connection.parameters.UnitId=NN）
#       → 读取 CSV 文本 → POST /api/devices/{id}/points/import（[FromBody] string）。
# 幂等：同名设备默认跳过（-ReplaceExisting 则先删后建，用于重导点位）。
#
# 用法：
#   pwsh tools/script/register-devices.ps1 -Base http://localhost:5100 -Endpoint 127.0.0.1:15020 -Count 50
#   pwsh tools/script/register-devices.ps1 -Count 50 -ReplaceExisting   # 已注册时重导

param(
    [string]$Base = "http://localhost:5100",
    [string]$CsvDir = $PSScriptRoot,
    [string]$Endpoint = "127.0.0.1:15020",
    [int]$StartUnit = 1,
    [int]$Count = 50,
    [string]$NamePrefix = "LoadDev",
    [switch]$ReplaceExisting,
    [string]$User = "admin",
    [string]$Pass = "admin123",
    [int]$TimeoutSec = 30
)

$ErrorActionPreference = "Stop"
$Api = $Base.TrimEnd('/') + "/api"

# 1) 登录
$login = Invoke-RestMethod -Method Post -Uri "$Api/auth/login" -ContentType "application/json" `
    -Body (@{ username = $User; password = $Pass } | ConvertTo-Json) -TimeoutSec $TimeoutSec
$token = $login.data.token
if (-not $token) { throw "登录失败：未取得 token（检查 -User/-Pass 与后端）" }
$H = @{ Authorization = "Bearer $token" }

# 2) 现有设备按名索引（幂等）
$existing = @{}
try {
    $all = Invoke-RestMethod -Method Get -Uri "$Api/devices" -Headers $H -TimeoutSec $TimeoutSec
    foreach ($d in @($all.data)) { if ($d.name) { $existing[$d.name] = $d.id } }
} catch { }

$ok = 0; $skip = 0; $fail = 0
for ($i = 0; $i -lt $Count; $i++) {
    $unit = $StartUnit + $i
    $name = "{0}-{1:D3}" -f $NamePrefix, $unit

    $csvPath = Join-Path $CsvDir ("points-device-{0:D2}.csv" -f $unit)
    if (-not (Test-Path -LiteralPath $csvPath)) {
        $csvPath = Join-Path $CsvDir ("points-device-{0:D3}.csv" -f $unit)  # 兼容 3 位命名
    }
    if (-not (Test-Path -LiteralPath $csvPath)) {
        Write-Host ("[skip] UnitId={0} 缺 CSV: {1}" -f $unit, $csvPath) -ForegroundColor Yellow
        $skip++; continue
    }

    try {
        $deviceId = $existing[$name]
        if ($deviceId -and $ReplaceExisting) {
            Invoke-RestMethod -Method Delete -Uri "$Api/devices/$deviceId" -Headers $H -TimeoutSec $TimeoutSec | Out-Null
            $deviceId = $null
        }

        if (-not $deviceId) {
            $body = @{
                name       = $name
                protocol   = @{ name = "Modbus"; dialect = "TCP" }
                connection = @{ endpoint = $Endpoint; parameters = @{ UnitId = $unit } }
            } | ConvertTo-Json -Depth 6
            $dev = Invoke-RestMethod -Method Post -Uri "$Api/devices" -Headers $H `
                -ContentType "application/json" -Body $body -TimeoutSec $TimeoutSec
            $deviceId = $dev.data.id
        } else {
            Write-Host ("[reuse] {0} 已存在 ({1})" -f $name, $deviceId)
        }

        # [FromBody] string → 发送 JSON 字符串字面量
        $importBody = (Get-Content -LiteralPath $csvPath -Raw -Encoding UTF8) | ConvertTo-Json
        $imp = Invoke-RestMethod -Method Post -Uri "$Api/devices/$deviceId/points/import" -Headers $H `
            -ContentType "application/json" -Body $importBody -TimeoutSec $TimeoutSec
        Write-Host ("[ok] {0} unit={1} device={2} points={3}" -f $name, $unit, $deviceId, $imp.data.count) -ForegroundColor Green
        $ok++
    } catch {
        Write-Host ("[fail] {0} unit={1}: {2}" -f $name, $unit, $_.Exception.Message) -ForegroundColor Red
        $fail++
    }
}

Write-Host ("`n完成：成功 {0} / 跳过 {1} / 失败 {2}（{3}, endpoint={4}）" -f $ok, $skip, $fail, $Api, $Endpoint)
if ($fail -gt 0) { exit 1 }

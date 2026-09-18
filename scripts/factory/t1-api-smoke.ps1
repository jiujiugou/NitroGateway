#t1-api-smoke.ps1
# T1 单设备端到端（API 段）驱动 —— FACTORY-TEST §4 的自动化版。
# 结果按统一 schema 写 JSON（与 run-factory-tests.ps1 对接），退出码 0=通过 1=失败。
# 用法：
#   .\scripts\factory\t1-api-smoke.ps1 [-Base http://localhost:5100] [-RequireData] [-OutFile path]
#   -RequireData: 要求"采集→入库"有真实数据（需 :502 模拟器在线），否则只验 API 契约。
# 依赖：后端已起（:5100）；数据段另需 Modbus 模拟器。

param(
    [string]$Base = "http://localhost:5100",
    [switch]$RequireData,
    [string]$OutFile = ""
)

$ErrorActionPreference = "Stop"
$Api = "$Base/api"

function New-Result($scenarioId, $ok, $steps, $message) {
    return [pscustomobject]@{
        scenarioId = $scenarioId
        result     = if ($ok) { "pass" } else { "fail" }
        steps      = $steps
        message    = $message
        completedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    }
}

$steps = [System.Collections.Generic.List[object]]::new()
function Add-Step($name, $ok, $detail) {
    $steps.Add([pscustomobject]@{ name = $name; ok = $ok; detail = $detail })
}

try {
    # 1 登录（T0.6）
    $token = $null
    try {
        $login = Invoke-RestMethod -Method Post -Uri "$Api/auth/login" -ContentType "application/json" `
            -Body (@{ username = "admin"; password = "admin123" } | ConvertTo-Json)
        $token = $login.data.token
        Add-Step "T0.6 登录 admin" ([bool]$token) "token=$([bool]$token)"
    } catch { Add-Step "T0.6 登录 admin" $false $_.Exception.Message }

    if (-not $token) { throw "登录失败，中止 T1" }
    $H = @{ Authorization = "Bearer $token" }

    # 2 注册设备（T1.1 前半：注册成功即入库）
    $deviceId = $null
    try {
        $name = "PLC-T1-{0:N}" -f ([guid]::NewGuid())
        $dev = Invoke-RestMethod -Method Post -Uri "$Api/devices" -Headers $H -ContentType "application/json" `
            -Body (@{ name = $name; protocol = @{ name = "Modbus"; dialect = "TCP" }; connection = @{ endpoint = "127.0.0.1:502" } } | ConvertTo-Json -Depth 6)
        $deviceId = $dev.data.id
        Add-Step "T1.1 注册设备" ([bool]$deviceId) "id=$deviceId"
    } catch { Add-Step "T1.1 注册设备" $false $_.Exception.Message }

    if (-not $deviceId) { throw "注册失败，中止 T1" }

    # 3 加点位（T1.2 配置面）
    $pointId = $null
    try {
        $pt = Invoke-RestMethod -Method Post -Uri "$Api/devices/$deviceId/points" -Headers $H -ContentType "application/json" `
            -Body (@{ name = "Temp"; address = "40001"; dataType = "Float"; access = "ReadWrite" } | ConvertTo-Json)
        $pointId = $pt.data.id
        Add-Step "T1.2 添加点位 Temp@40001" ([bool]$pointId) "id=$pointId"
    } catch { Add-Step "T1.2 添加点位" $false $_.Exception.Message }

    # 4 历史接口契约（T1.3 的查询面，200 即契约 OK）
    $historyOk = $false; $count = -1
    try {
        $uri = "$Api/measurements/history?deviceId=$deviceId&pointId=$pointId&from=2020-01-01T00:00:00Z&to=2030-01-01T00:00:00Z"
        $hist = Invoke-RestMethod -Method Get -Uri $uri -Headers $H
        $historyOk = $hist.success
        $count = @($hist.data).Count
        Add-Step "T1.3 历史查询" $historyOk "count=$count"
    } catch { Add-Step "T1.3 历史查询" $false $_.Exception.Message }

    # 5 数据面：采集→入库（仅 -RequireData，需模拟器）
    $dataOk = $true
    if ($RequireData) {
        $dataOk = $false
        for ($i = 0; $i -lt 40 -and -not $dataOk; $i++) {
            Start-Sleep -Seconds 1
            try {
                $latest = Invoke-RestMethod -Method Get -Uri "$Api/measurements/latest?deviceId=$deviceId&pointId=$pointId" -Headers $H
                $dataOk = $latest.success -and (@($latest.data).Count -gt 0)
            } catch { }
        }
        Add-Step "T1.2 采集入库(RequireData)" $dataOk "轮询40s"
    }

    # 6 清理
    try { Invoke-RestMethod -Method Delete -Uri "$Api/devices/$deviceId" -Headers $H | Out-Null; Add-Step "清理设备" $true "" } catch { Add-Step "清理设备" $false $_.Exception.Message }

    $overall = (($steps | Where-Object { $_.name -notlike "清理*" } | Where-Object { -not $_.ok }).Count -eq 0)
    $r = New-Result "T1" $overall $steps ("data=$dataOk historyCount=$count")
    $r | ConvertTo-Json -Depth 8 | Out-File -FilePath (if ($OutFile) { $OutFile } else { "t1-result.json" }) -Encoding UTF8
    if ($overall) { Write-Host "[T1] PASS — data=$dataOk historyCount=$count" -ForegroundColor Green; exit 0 }
    else { Write-Host "[T1] FAIL" -ForegroundColor Red; $steps | Where-Object { -not $_.ok } | ForEach-Object { Write-Host "  ✗ $($_.name): $($_.detail)" -ForegroundColor Red }; exit 1 }
}
catch {
    $r = New-Result "T1" $false $steps $_.Exception.Message
    $r | ConvertTo-Json -Depth 8 | Out-File -FilePath (if ($OutFile) { $OutFile } else { "t1-result.json" }) -Encoding UTF8
    Write-Host "[T1] FAIL — $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

#t3-longrun-sampler.ps1
# T3 长时间运行采样器 —— FACTORY-TEST §6 的"自动判定"替身。
# 不再每小时人工抄：本脚本按固定间隔抓 /healthz + /metrics 指标 + SQLite 文件大小，
# 结束后按 FACTORY 口径自动判定：进程存活 / 内存 ≤2×基线 / SQLite 增量 ≤50MB / 写失败不增长。
# 用法：
#   .\scripts\factory\t3-longrun-sampler.ps1 -Minutes 480 -IntervalSeconds 60 [-Base http://localhost:5100] [-OutFile path] [-DbPath ...]
#   示例短验：-Minutes 3 -IntervalSeconds 15

param(
    [int]$Minutes = 480,
    [int]$IntervalSeconds = 60,
    [string]$Base = "http://localhost:5100",
    [string]$OutFile = "t3-result.json",
    [string]$DbPath = "src\NitroGateway.Webapi\nitrogateway.db"
)

$ErrorActionPreference = "Continue"

function Get-Metric($text, [string]$name) {
    $m = [regex]::Match($text, "(?m)^$([regex]::Escape($name))(\{[^}]*\})?\s+([0-9.]+)")
    if ($m.Success) { try { return [double]$m.Groups[3].Value } catch { return $null } }
    return $null
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # repo 根
$dbFull = if ([System.IO.Path]::IsPathRooted($DbPath)) { $DbPath } else { Join-Path $root $DbPath }
$dbStartMb = if (Test-Path -LiteralPath $dbFull) { [math]::Round((Get-Item $dbFull).Length / 1MB, 2) } else { $null }

Write-Host "[T3] 长稳采样：$Minutes 分钟，每 $IntervalSeconds s，目标 $Base" -ForegroundColor Cyan
$samples = [System.Collections.Generic.List[object]]::new()
$deadline = (Get-Date).AddMinutes($Minutes)

while ((Get-Date) -lt $deadline) {
    $s = [ordered]@{ at = (Get-Date).ToUniversalTime().ToString("O") }
    try {
        $resp = Invoke-WebRequest -Uri "$Base/healthz" -TimeoutSec 10 -UseBasicParsing
        $s.healthz = if ($resp.StatusCode -eq 200) { "200" } else { $resp.StatusCode.ToString() }
    } catch { $s.healthz = "ERR" }

    try {
        $metrics = (Invoke-WebRequest -Uri "$Base/metrics" -TimeoutSec 10 -UseBasicParsing).Content
        $s.backlog = Get-Metric $metrics "nitro_buffer_backlog"
        $s.collectionTotal = Get-Metric $metrics "nitro_collection_total"
        $s.storeWriteFailures = Get-Metric $metrics "nitro_store_write_failures_total"
        $s.circuitBreakerState = Get-Metric $metrics "nitro_circuit_breaker_state"
        $s.memoryBytes = Get-Metric $metrics "dotnet_total_memory_bytes"
        $s.mqttState = Get-Metric $metrics "nitro_mqtt_state"
    } catch { $s.metricsErr = $_.Exception.Message }

    $s.dbSizeMb = if (Test-Path -LiteralPath $dbFull) { [math]::Round((Get-Item $dbFull).Length / 1MB, 2) } else { $null }
    $samples.Add([pscustomobject]$s)

    $left = [math]::Max(0, [int]($deadline - (Get-Date)).TotalMinutes)
    Write-Host ("sample {0,3}: health={1} backlog={2} memMB={3} dbMB={4} left={5}m" -f $samples.Count, $s.healthz,
        $s.backlog, $(if ($s.memoryBytes) { [int]($s.memoryBytes / 1MB) } else { "-" }), $s.dbSizeMb, $left)
    if ((Get-Date) -lt $deadline) { Start-Sleep -Seconds $IntervalSeconds }
}

# ── 判定（FACTORY-TEST §2/§6 口径） ──
$n = $samples.Count
$healthAlive = ($samples | Where-Object { $_.healthz -eq "200" }).Count -eq $n -and $n -gt 0
$mem = @($samples | Where-Object { $null -ne $_.memoryBytes } | ForEach-Object { [double]$_.memoryBytes })
$memOk = $true
if ($mem.Count -ge 2) {
    $memPeak = ($mem | Measure-Object -Maximum).Maximum
    $memBase = $mem[0]
    $memOk = $memPeak -le $memBase * 2.0
}
$db = @($samples | Where-Object { $null -ne $_.dbSizeMb } | ForEach-Object { [double]$_.dbSizeMb })
$dbGrowthMb = if ($db.Count -ge 2) { $db[$db.Count - 1] - $db[0] } else { 0 }
$dbOk = $dbGrowthMb -le 50.0
$wf = @($samples | Where-Object { $null -ne $_.storeWriteFailures } | ForEach-Object { [double]$_.storeWriteFailures })
$wfOk = $wf.Count -eq 0 -or ($wf[$wf.Count - 1] -eq $wf[0])
$backlogPeak = if (($samples.backlog | Where-Object { $null -ne $_ }).Count -gt 0) { ($samples.backlog | Where-Object { $null -ne $_ } | Measure-Object -Maximum).Maximum } else { $null }
$backlogOk = $null -eq $backlogPeak -or $backlogPeak -le 100000

$verdicts = [ordered]@{
    processAlive      = $healthAlive
    memoryWithin2x    = $memOk
    dbGrowthMb        = [math]::Round($dbGrowthMb, 2)
    dbWithin50Mb      = $dbOk
    storeWriteFailuresNoGrowth = $wfOk
    backlogPeak       = $backlogPeak
    backlogWithinCap  = $backlogOk
}
$ok = $healthAlive -and $memOk -and $dbOk -and $wfOk -and $backlogOk
$msg = "samples=$n dbStartMb=$dbStartMb dbGrowthMb=$([math]::Round($dbGrowthMb,2)) memoryPeak<=2x=$memOk"
$r = [pscustomobject]@{ scenarioId = "T3"; result = if ($ok) { "pass" } else { "fail" }; samples = $samples; verdicts = $verdicts; message = $msg; completedAtUtc = (Get-Date).ToUniversalTime().ToString("O") }
$r | ConvertTo-Json -Depth 10 | Out-File -FilePath $OutFile -Encoding UTF8
Write-Host "════ T3 判定 ════" -ForegroundColor Cyan
$verdicts.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-28} {1}" -f $_.Key, $_.Value) }
if ($ok) { Write-Host "[T3] PASS" -ForegroundColor Green; exit 0 } else { Write-Host "[T3] FAIL" -ForegroundColor Red; exit 1 }

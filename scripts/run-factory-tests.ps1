#run-factory-tests.ps1
# NitroGateway 出厂门禁（FACTORY-TEST T0~T7）统一指挥层 —— docs/08-测试策略.md L6
# 作用：前置体检 → 按 ID 执行"可自动"场景 → 汇总统一 JSON → 出唯一门禁页 index.html → 退出码判定。
# 判定规则（FACTORY-TEST §0）：P0+P1 全过才放行；本脚本只对"已自动执行"的场景判定，
# 标 manual/skip 的场景不判 fail（需人工/发布前，见 FACTORY-TEST.md）。
# 用法：
#   .\scripts\run-factory-tests.ps1                     # 体检 + 跑 T5(L3 安全 E2E)，其余如实标注
#   .\scripts\run-factory-tests.ps1 -RunT0              # 连自动化回归(T0)一起跑（慢）
#   .\scripts\run-factory-tests.ps1 -T3Minutes 480      # 现场值守 T3 8h 长稳采样并自动判定
# 结果：artifacts/factory-test/index.html

param(
    [string]$Base = "http://localhost:5100",
    [string]$ResultsDir = "artifacts\factory-test",
    [switch]$RunT0,
    [switch]$SkipT5,
    [int]$T3Minutes = 0,
    [string]$T3DbPath = "src\NitroGateway.Webapi\nitrogateway.db"
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$outDir = Join-Path $root $ResultsDir
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# ── 1. 前置体检 ──
function Test-PortOpen([int]$port, [string]$hostName = "127.0.0.1") {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $iar = $c.BeginConnect($hostName, $port, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne(800)
        if ($ok) { $c.EndConnect($iar); $c.Close(); return $true }
        $c.Close(); return $false
    } catch { return $false }
}
$healthz = $false
try { $resp = Invoke-WebRequest -Uri "$Base/healthz" -TimeoutSec 4 -UseBasicParsing; $healthz = $resp.StatusCode -eq 200 } catch { }
$sim502   = Test-PortOpen 502
$mqtt1883 = Test-PortOpen 1883

Write-Host "── 前置体检 ──" -ForegroundColor Cyan
Write-Host ("  backend /healthz ({0}) : {1}" -f $Base, $(if ($healthz) { "OK" } else { "不可达" }))
Write-Host ("  Modbus 模拟器 :502    : {0}" -f $(if ($sim502) { "OK" } else { "缺失" }))
Write-Host ("  MQTT broker :1883     : {0}" -f $(if ($mqtt1883) { "OK" } else { "缺失" }))

$envInfo = [ordered]@{ backendHealthz = $healthz; simulator502 = $sim502; mqtt1883 = $mqtt1883; base = $Base }

# ── 2. 场景表 ──
$scenarios = [System.Collections.Generic.List[object]]::new()
function Add-Scenario($id, $name, $priority, $type, $result, $detail) {
    $scenarios.Add([pscustomobject]@{
        id = $id; name = $name; priority = $priority; type = $type
        result = $result; detail = $detail
    })
}

# T0 自动化回归（L0~L3，已由 run-all-tests/CI 承担）
if ($RunT0) {
    Write-Host "[T0] 跑 run-all-tests.ps1 ..." -ForegroundColor Cyan
    & (Join-Path $root "scripts\run-all-tests.ps1")
    $okT0 = $LASTEXITCODE -eq 0
    Add-Scenario "T0" "构建+单测+集成+E2E" "P0" "auto" $(if ($okT0) { "pass" } else { "fail" }) "run-all-tests exit=$LASTEXITCODE"
} else {
    Add-Scenario "T0" "构建+单测+集成+E2E" "P0" "auto" "skip" "由 scripts/run-all-tests.ps1 / CI 承担（-RunT0 可在此跑）"
}

# T1 单设备端到端（需后端 + :502 模拟器）
if ($healthz -and $sim502) {
    Write-Host "[T1] 跑 t1-api-smoke.ps1 -RequireData ..." -ForegroundColor Cyan
    & (Join-Path $root "scripts\factory\t1-api-smoke.ps1") -Base $Base -RequireData -OutFile (Join-Path $outDir "t1-result.json")
    $okT1 = $LASTEXITCODE -eq 0
    Add-Scenario "T1" "单设备端到端(采集入库+查询)" "P0" "auto" $(if ($okT1) { "pass" } else { "fail" }) "t1-api-smoke exit=$LASTEXITCODE"
} elseif ($healthz) {
    Add-Scenario "T1" "单设备端到端" "P0" "auto" "skip" "缺 Modbus 模拟器(:502) —— 数据面无法验证"
} else {
    Add-Scenario "T1" "单设备端到端" "P0" "auto" "skip" "后端未起，无法执行"
}

# T2 多设备并发/隔离 —— 半自动：起 10 从站可自动化，故障隔离需 fault-injection 值守（FACTORY §5）
Add-Scenario "T2" "10设备并发+隔离" "P0" "semi" "manual" "起站可自动(exp-10slaves.ps1/ModbusSlaveSim)；隔离+自动恢复≤35s 需现场值守，见 FACTORY-TEST §5"

# T3 8h 长稳
if ($T3Minutes -gt 0) {
    Write-Host "[T3] 跑 t3-longrun-sampler.ps1 ($T3Minutes 分钟)..." -ForegroundColor Cyan
    & (Join-Path $root "scripts\factory\t3-longrun-sampler.ps1") -Minutes $T3Minutes -IntervalSeconds 60 -Base $Base -DbPath $T3DbPath -OutFile (Join-Path $outDir "t3-result.json")
    $okT3 = $LASTEXITCODE -eq 0
    Add-Scenario "T3" "长时间运行(内存/积压/SQLite)" "P0" "semi" $(if ($okT3) { "pass" } else { "fail" }) "t3 sampler exit=$LASTEXITCODE"
} else {
    Add-Scenario "T3" "长时间运行(内存/积压/SQLite)" "P0" "semi" "manual" "采样器已就绪：run-factory-tests.ps1 -T3Minutes 480（阈值自动判，替代人工抄表）"
}

# T4 故障恢复 —— 需 clumsy/agent 值守（FACTORY §7）
Add-Scenario "T4" "故障恢复(网络/MQTT/kill/热加载)" "P1" "semi" "manual" "需 clumsy 网络扰动 + 模拟器故障注入值守，见 FACTORY-TEST §7"

# T5 安全与权限 —— 已自动化 = L3 E2E（Auth/RBAC）
if (-not $SkipT5) {
    Write-Host "[T5] dotnet test L3 (AuthFlow + Viewer RBAC E2E) ..." -ForegroundColor Cyan
    & dotnet test "tests\NitroGateway.ApiE2eTests\NitroGateway.ApiE2eTests.csproj" --nologo -v q `
        --filter "FullyQualifiedName~AuthFlowE2eTests|FullyQualifiedName~Viewer_DeleteDevice_Is403"
    $okT5 = $LASTEXITCODE -eq 0
    Add-Scenario "T5" "安全与权限(401/403/登录)" "P1" "auto" $(if ($okT5) { "pass" } else { "fail" }) "L3 E2E auth/RBAC exit=$LASTEXITCODE"
} else {
    Add-Scenario "T5" "安全与权限(401/403/登录)" "P1" "auto" "skip" "-SkipT5"
}

# T6 前端验收 / T7 中心形态 —— 人工（发布前）
Add-Scenario "T6" "前端页面可用" "P2" "manual" "manual" "人工验收清单，见 FACTORY-TEST §9"
Add-Scenario "T7" "中心形态端到端(现场→中心)" "P1" "manual" "manual" "需中心 compose 栈(docker)，见 FACTORY-TEST §10"

# ── 3. 门禁判定（只对已执行场景） ──
$executed = @($scenarios | Where-Object { $_.result -eq "pass" -or $_.result -eq "fail" })
$p0fail = @($executed | Where-Object { $_.priority -eq "P0" -and $_.result -eq "fail" })
$p1fail = @($executed | Where-Object { $_.priority -eq "P1" -and $_.result -eq "fail" })
$verdict = if ($p0fail.Count -gt 0 -or $p1fail.Count -gt 0) { "FAIL — 有 P0/P1 失败" } else { "通过（已执行场景全绿；manual/skip 待发布前完成）" }

# ── 4. 输出 JSON + HTML ──
$summary = [ordered]@{
    runId = (Get-Date).ToString("yyyyMMdd-HHmmss")
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    environment = $envInfo
    scenarios = $scenarios
    gate = [ordered]@{ p0Fail = ($p0fail.Count -gt 0); p1Fail = ($p1fail.Count -gt 0); verdict = $verdict }
}
$jsonPath = Join-Path $outDir "results.json"
$summary | ConvertTo-Json -Depth 10 | Out-File -FilePath $jsonPath -Encoding UTF8

$rows = ""
foreach ($s in $scenarios) {
    $color = switch ($s.result) { "pass" { "#27ae60" } "fail" { "#c0392b" } "skip" { "#bdc3c7" } "manual" { "#d68910" } default { "#7f8c8d" } }
    $icon = switch ($s.result) { "pass" { "PASS" } "fail" { "FAIL" } "skip" { "SKIP" } "manual" { "MANUAL" } default { "?" } }
    $rows += "<tr><td><b>$($s.id)</b></td><td>$($s.name)</td><td>$($s.priority)</td><td>$($s.type)</td>" +
             "<td style='color:$color;font-weight:bold'>$icon</td><td>$($s.detail)</td></tr>`n"
}

$envBackendStr = if ($envInfo.backendHealthz) { "可达" } else { "不可达" }
$envSimStr     = if ($envInfo.simulator502) { "在" } else { "缺" }
$envMqttStr    = if ($envInfo.mqtt1883) { "在" } else { "缺" }
$verdictColor  = if ($p0fail.Count -gt 0 -or $p1fail.Count -gt 0) { "#c0392b" } else { "#27ae60" }

$html = @"
<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>NitroGateway 出厂门禁 (FACTORY-TEST)</title>
<style>
body{font-family:Segoe UI,Microsoft YaHei,sans-serif;margin:32px;background:#f5f6f8;color:#222}
h1{font-size:20px}.card{background:#fff;border:1px solid #e3e6ea;border-radius:8px;padding:20px;margin-top:16px;max-width:1000px}
table{border-collapse:collapse;width:100%;margin-top:8px}th,td{border-bottom:1px solid #eef1f4;padding:8px 10px;text-align:left;font-size:14px}
th{background:#fafbfc;font-size:13px;color:#666}.meta{color:#888;font-size:13px}.env{margin-top:6px;color:#444;font-size:13px}
</style></head><body>
<h1>NitroGateway 出厂门禁（FACTORY-TEST T0~T7）</h1>
<div class="meta">运行 $($summary.runId)｜ 入口 scripts/run-factory-tests.ps1 ｜ 判定口径 FACTORY-TEST.md</div>
<div class="card">
<div class="env">环境: 后端=$envBackendStr ｜ Modbus:502=$envSimStr ｜ MQTT:1883=$envMqttStr</div>
<div style="margin-top:8px">结论: <span style="color:$verdictColor;font-weight:bold">$verdict</span></div>
<table><tr><th>#</th><th>场景</th><th>级别</th><th>方式</th><th>结果</th><th>说明</th></tr>
$rows</table></div>
</body></html>
"@
$index = Join-Path $outDir "index.html"
Set-Content -LiteralPath $index -Value $html -Encoding UTF8

Write-Host "`n════════════════════════════════════════" -ForegroundColor Cyan
$scenarios | ForEach-Object { Write-Host ("  {0,-4} {1,-6} {2}  {3}" -f $_.id, $_.result, $_.priority, $_.name) }
Write-Host "──── 判定: $verdict ────" -ForegroundColor $(if ($p0fail.Count -gt 0 -or $p1fail.Count -gt 0) { 'Red' } else { 'Green' })
Write-Host "门禁页: $index"
if ($p0fail.Count -gt 0 -or $p1fail.Count -gt 0) { exit 1 } else { exit 0 }

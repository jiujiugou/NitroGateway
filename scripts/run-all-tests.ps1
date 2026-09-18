#run-all-tests.ps1
# NitroGateway 统一测试入口（docs/08-测试策略.md §4）
# 作用：唯一全量入口——构建 + L1 单测 + L2 集成 + L3 API E2E，聚合单一 html 报告。
# 用法：
#   .\scripts\run-all-tests.ps1                  # 全量（构建 + 三个测试项目）
#   .\scripts\run-all-tests.ps1 -SkipBuild        # 已构建过，直接跑测试
#   .\scripts\run-all-tests.ps1 -OnlyL3           # 只跑 L3 API E2E（快速反馈）
# 结果：artifacts/test-results/index.html（唯一报告页）；退出码=0 全绿，否则非 0。
# 规矩：新测试放对层（docs/08 §2），不得在此之外另建"第 3 套 runner"。

param(
    [switch]$SkipBuild,
    [switch]$SkipUnit,
    [switch]$SkipIntegration,
    [switch]$SkipE2e,
    [switch]$OnlyL3,
    [string]$ResultsDir = "artifacts\test-results"
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ($OnlyL3) { $SkipUnit = $true; $SkipIntegration = $true }

$projects = [ordered]@{}
if (-not $SkipUnit)        { $projects["L1 单元测试(UnitTests)"] = "tests\NitroGateway.UnitTests\NitroGateway.UnitTests.csproj" }
if (-not $SkipIntegration) { $projects["L2 集成测试(IntegrationTests)"] = "tests\NitroGateway.IntegrationTests\NitroGateway.IntegrationTests.csproj" }
if (-not $SkipE2e)         { $projects["L3 API E2E(ApiE2eTests)"] = "tests\NitroGateway.ApiE2eTests\NitroGateway.ApiE2eTests.csproj" }

$trxDir = Join-Path $root $ResultsDir
New-Item -ItemType Directory -Force -Path $trxDir | Out-Null

# ── L0 构建 ──
if (-not $SkipBuild) {
    Write-Host "`n[L0] dotnet build NitroGateway.slnx ..." -ForegroundColor Cyan
    & dotnet build "NitroGateway.slnx" --nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host "[L0] 构建失败" -ForegroundColor Red }
}

# ── L1/L2/L3 测试 ──
$runResults = [System.Collections.Generic.List[object]]::new()
foreach ($entry in $projects.GetEnumerator()) {
    $name = $entry.Key
    $proj = $entry.Value
    $safe = ([regex]::Replace($name, '[^\w\-]', '_'))
    $trx = Join-Path $trxDir "$safe.trx"

    Write-Host "`n[$name] dotnet test ..." -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet test $proj --nologo -v q --logger "trx;LogFileName=$safe.trx" --results-directory $trxDir 2>&1 | ForEach-Object { $_.ToString() } | Where-Object { $_ -match '已通过|已失败|失败|error|MSB' }
    $exit = $LASTEXITCODE
    $sw.Stop()
    Write-Host "[$name] exit=$exit, $($sw.Elapsed.TotalSeconds.ToString('N0'))s" -ForegroundColor $(if ($exit -eq 0) { 'Green' } else { 'Red' })
    $runResults.Add([pscustomobject]@{ Name = $name; Trx = $trx; Exit = $exit })
}

# ── 解析 trx → 汇总 ──
$rows = ""
$overallFailed = $false
$grandTotal = 0; $grandPassed = 0; $grandFailed = 0; $grandSkipped = 0

foreach ($r in $runResults) {
    $total = $passed = $failed = $skipped = 0
    if (Test-Path -LiteralPath $r.Trx) {
        try {
            [xml]$xml = Get-Content -LiteralPath $r.Trx -Raw
            $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
            $ns.AddNamespace("t", "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
            $counters = $xml.SelectSingleNode("//t:ResultSummary/t:Counters", $ns)
            if ($counters) {
                $total = [int]$counters.total; $passed = [int]$counters.passed
                $failed = [int]$counters.failed + [int]$counters.error + [int]$counters.timeout + [int]$counters.aborted
                $skipped = [int]$counters.total - [int]$counters.executed
            }
        } catch { $total = -1 }
    }
    $color = if ($failed -gt 0 -or $total -lt 0) { '#c0392b' } elseif ($total -eq 0) { '#bdc3c7' } else { '#27ae60' }
    $status = if ($total -lt 0) { "解析失败" } elseif ($failed -gt 0) { "FAIL" } elseif ($total -eq 0) { "空" } else { "PASS" }
    if ($failed -gt 0 -or $r.Exit -ne 0) { $overallFailed = $true }
    $rows += "<tr><td>$($r.Name)</td><td style='color:$color;font-weight:bold'>$status</td>" +
             "<td>$total</td><td>$passed</td><td>$failed</td><td>$skipped</td>" +
             "<td><a href='$([System.IO.Path]::GetFileName($r.Trx))'>trx</a></td></tr>`n"
    $grandTotal += $total; $grandPassed += $passed; $grandFailed += $failed; $grandSkipped += $skipped
}

$verdict = if ($grandTotal -eq 0) { "未执行任何测试" } elseif ($grandFailed -gt 0 -or $overallFailed) { "不通过 — 有失败" } else { "全部通过" }
$verdictColor = if ($grandFailed -gt 0 -or $overallFailed) { '#c0392b' } else { '#27ae60' }
$stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

$html = @"
<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>NitroGateway 测试汇总</title>
<style>
body{font-family:Segoe UI,Microsoft YaHei,sans-serif;margin:32px;background:#f5f6f8;color:#222}
h1{font-size:20px}.card{background:#fff;border:1px solid #e3e6ea;border-radius:8px;padding:20px;margin-top:16px;max-width:900px}
table{border-collapse:collapse;width:100%;margin-top:8px}th,td{border-bottom:1px solid #eef1f4;padding:8px 10px;text-align:left}
th{background:#fafbfc;font-size:13px;color:#666}.verdict{font-size:18px;font-weight:bold;color:$verdictColor}
.meta{color:#888;font-size:13px}
</style></head><body>
<h1>NitroGateway 测试统一报告</h1>
<div class="meta">生成时间 $stamp ｜ 入口 scripts/run-all-tests.ps1 ｜ 详见 docs/08-测试策略.md</div>
<div class="card"><span class="verdict">$verdict</span>
<span class="meta">（总计 $grandTotal，通过 $grandPassed，失败 $grandFailed，跳过 $grandSkipped）</span>
<table><tr><th>项目(层)</th><th>结论</th><th>总数</th><th>通过</th><th>失败</th><th>跳过</th><th>明细</th></tr>
$rows</table></div>
</body></html>
"@

$index = Join-Path $trxDir "index.html"
Set-Content -LiteralPath $index -Value $html -Encoding UTF8
Write-Host "`n════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "报告: $index"
Write-Host "总计 $grandTotal / 通过 $grandPassed / 失败 $grandFailed / 跳过 $grandSkipped"
if ($grandFailed -gt 0 -or $overallFailed) { Write-Host "结论: 不通过" -ForegroundColor Red; exit 1 }
else { Write-Host "结论: 全部通过" -ForegroundColor Green; exit 0 }

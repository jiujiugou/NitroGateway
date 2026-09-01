# ============================================================
# check-notes.ps1 — ADR / worklog 一致性检查（脚手架）
# ------------------------------------------------------------
# 用途：把 AGENTS.md 工作规则 7（记忆沉淀到 notes/）
#       从"人机约定"变成可执行的检查，堵住没人强制时的漏网。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-notes.ps1
#   脚本以非零退出码结束 = 检查失败（供 hook / CI 判红）。
#
# 接入方式（选一个，逻辑写完再启用）：
#   A) pre-commit：复制本脚本到 .git/hooks/pre-commit 并包一层
#      powershell.exe 调用（Windows 下 hooks 不能直接跑 ps1）
#   B) CI：在 .github/workflows/ 加一个 job 跑本脚本
#
# 检查项（对应 AGENTS.md 工作规则 7，逻辑由你实现）：
#   1. worklog     —— 今天存在 notes/worklog/YYYY-MM-DD.md，头部有"当前目标"段
#   2. ADR 编号    —— notes/ADR/** 内编号 NNN 不重复、路径符合 <模块>/ADR-NNN-*
#   3. README 同步 —— 新 ADR 在 notes/ADR/README.md 分组表有对应行，根目录不散落 ADR
#   4. 闭环删条    —— 已修复的 ADR 条目应从 ADR 删除，不留"已修复"残留
# ============================================================

$ErrorActionPreference = 'Stop'

$repoRoot    = Split-Path -Parent $PSScriptRoot   # 仓库根目录
$notesDir    = Join-Path $repoRoot 'notes'
$adrDir      = Join-Path $notesDir 'ADR'
$worklogDir  = Join-Path $notesDir 'worklog'

# ---------- TODO: 以下每个函数由你实现 ----------

function Test-WorklogToday {
    # 工作规则 7：结论与当前目标写 notes/worklog/YYYY-MM-DD.md（最近日期文件头部放"当前目标"段）
    # 返回 ($ok, $message)：$ok=$true 通过；$message 为提示/失败原因
    $ok = $false
    $message = 'TODO: 未实现'
    return @($ok, $message)
}

function Test-AdrNumbering {
    # 工作规则 7：编号 NNN 全局唯一，路径为 notes/ADR/<模块>/ADR-NNN-*.md
    $ok = $false
    $message = 'TODO: 未实现'
    return @($ok, $message)
}

function Test-AdrReadmeSync {
    # README 约定：新 ADR 在分组表有对应行；根目录只放 README.md
    $ok = $false
    $message = 'TODO: 未实现'
    return @($ok, $message)
}

function Test-AdrClosedRemoved {
    # 工作规则 7：修复完的 ADR 从 ADR 删除（避免"已修复"残留假状态）
    $ok = $false
    $message = 'TODO: 未实现'
    return @($ok, $message)
}

# ---------- 入口：汇总四个检查项 ----------

$checks = @(
    @{ Name = 'worklog 今日文件 + 当前目标段'; Fn = ${function:Test-WorklogToday} },
    @{ Name = 'ADR 编号唯一 + 按模块归类';     Fn = ${function:Test-AdrNumbering}  },
    @{ Name = 'README 地图同步';               Fn = ${function:Test-AdrReadmeSync} },
    @{ Name = '已修复条目已从 ADR 删除';        Fn = ${function:Test-AdrClosedRemoved} }
)

$failed = $false
foreach ($c in $checks) {
    $result = & $c.Fn
    $ok = $result[0]; $msg = $result[1]
    if ($ok) {
        Write-Host ("[PASS] {0}" -f $c.Name) -ForegroundColor Green
    } else {
        Write-Host ("[FAIL] {0}: {1}" -f $c.Name, $msg) -ForegroundColor Red
        $failed = $true
    }
}

if ($failed) {
    Write-Host '检查未通过，见上方 [FAIL] 项。' -ForegroundColor Red
    exit 1
}
Write-Host '检查全部通过。' -ForegroundColor Green
exit 0

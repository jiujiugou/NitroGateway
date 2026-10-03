# 独立 Exe 跑批：build → coyote rewrite（就地重写 NitroGateway.* 程序集）→ 运行测试程序。
# 用法（在仓库根或任意位置）：  pwsh tests/NitroGateway.CoyoteTests/run.ps1 [-Iterations 100]
param([int]$Iterations = 100)

$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$proj = Join-Path $PSScriptRoot 'NitroGateway.CoyoteTests.csproj'
$bin = Join-Path $PSScriptRoot 'bin\Debug\net10.0'

Push-Location $root
try {
    Write-Host '== build =='
    dotnet build $proj -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host '== rewrite (in place) =='
    Get-ChildItem -Path $bin -Filter 'NitroGateway.*.dll' | ForEach-Object {
        dotnet coyote rewrite $_.FullName -v error
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    $env:COYOTE_TELEMETRY_OPTOUT = '1'

    Write-Host '== run =='
    dotnet (Join-Path $bin 'NitroGateway.CoyoteTests.dll') $Iterations
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}

# gen-points-csv.ps1 — 为 ModbusSlaveSim 生成多从站 / 多点位 CSV（压力测试用）
#
# 文件名 points-device-NN.csv 中的 NN 即从站 UnitId，供 ModbusSlaveSim 的 --csv-dir 扫描。
# 列格式与 PointSource.LoadCsv 对齐：Name,Address,DataType,Enabled,MinValue,MaxValue
# 地址按类型大小顺次排布，避免多寄存器类型（Int32/Float/Int64/Double）重叠。
#
# 用法:
#   pwsh -File gen-points-csv.ps1 -OutDir D:\tmp\load-csv -SlaveCount 50 -PointsPerSlave 50

param(
    [string]$OutDir = ".",
    [int]$SlaveCount = 50,
    [int]$PointsPerSlave = 50,
    [int]$StartUnit = 1
)

$typeSize = @{
    Float = 2; Int32 = 2; UInt32 = 2; Int16 = 1
    UInt16 = 1; Int64 = 4; UInt64 = 4; Double = 4
}
$cycle = @('Float', 'Int32', 'UInt32', 'Int16', 'UInt16', 'Int64', 'UInt64', 'Double')
$ranges = @{
    Float  = @(0, 100)
    Int32  = @(-1000000, 1000000)
    UInt32 = @(0, 1000000)
    Int16  = @(-1000, 1000)
    UInt16 = @(0, 1000)
    Int64  = @(0, 1000000000)
    UInt64 = @(0, 1000000000)
    Double = @(0, 1000)
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

for ($u = 0; $u -lt $SlaveCount; $u++) {
    $unit = $StartUnit + $u
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("Name,Address,DataType,Enabled,MinValue,MaxValue")

    $hOff = 0
    $iOff = 0
    for ($p = 0; $p -lt $PointsPerSlave; $p++) {
        $t = $cycle[$p % $cycle.Count]
        $size = $typeSize[$t]

        if ($p % 10 -eq 9) {
            # 每 10 个放 1 个到输入寄存器（只读区）
            $addr = 30001 + $iOff
            $iOff += $size
        }
        else {
            $addr = 40001 + $hOff
            $hOff += $size
        }

        $r = $ranges[$t]
        $name = "U{0:D2}_P{1:D3}" -f $unit, $p
        $lines.Add("$name,$addr,$t,TRUE,$($r[0]),$($r[1])")
    }

    # 线圈 / 离散输入各 2 点（Bool）
    $lines.Add(("U{0:D2}_C01,00001,Bool,TRUE,0,1" -f $unit))
    $lines.Add(("U{0:D2}_C02,00002,Bool,TRUE,0,1" -f $unit))
    $lines.Add(("U{0:D2}_D01,10001,Bool,TRUE,0,1" -f $unit))
    $lines.Add(("U{0:D2}_D02,10002,Bool,TRUE,0,1" -f $unit))

    $file = Join-Path $OutDir ("points-device-{0:D2}.csv" -f $unit)
    Set-Content -LiteralPath $file -Value $lines -Encoding UTF8
}

Write-Host "生成完成: $SlaveCount 从站 × $PointsPerSlave 点位（+线圈/离散各 2）→ $OutDir"

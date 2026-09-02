param([string]$Path, [int]$Skip = 0, [int]$Take = 0)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$p = Get-ChildItem -Path $Path -ErrorAction Stop | Select-Object -First 1
$lines = [System.IO.File]::ReadAllLines($p.FullName, [System.Text.Encoding]::UTF8)
if ($Take -gt 0) {
    $lines[$Skip..([Math]::Min($Skip + $Take - 1, $lines.Length - 1))] -join "`n"
} else {
    ($lines | Select-Object -Skip $Skip) -join "`n"
}

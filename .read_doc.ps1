$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$f = Get-ChildItem 'D:\Code\NitroGateway\docs' -Filter '07-*.md' | Select-Object -First 1
$c = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
$lines = $c -split "`n"
$lines[40..($lines.Length-1)] -join "`n"

# Starts mbslave-agent.ps1 as a hidden background process.
param(
  [string]$ControlFile = "D:\Code\NitroGateway\tools\factory-test\agent-control.txt",
  [string]$StatusFile  = "D:\Code\NitroGateway\tools\factory-test\agent-status.txt"
)
$agent = "D:\Code\NitroGateway\tools\factory-test\mbslave-agent.ps1"
Remove-Item -LiteralPath $ControlFile, $StatusFile -Force -ErrorAction SilentlyContinue
$p = Start-Process powershell -WindowStyle Hidden -ArgumentList @(
  '-NoProfile','-ExecutionPolicy','Bypass','-File',$agent,
  '-ControlFile',$ControlFile,'-StatusFile',$StatusFile
) -PassThru
Write-Output "agent pid=$($p.Id)"
Start-Sleep -Seconds 4
if (Test-Path $StatusFile) { Get-Content $StatusFile -Encoding UTF8 } else { Write-Output "no status file yet" }

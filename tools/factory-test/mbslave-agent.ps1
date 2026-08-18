# mbslave-agent.ps1 - persistent COM agent for Witte Modbus Slave.
#
# Why: Modbus Slave's TCP service is "session-scoped" - it only answers Modbus
# requests while a process keeps the automation client (Application object) and
# the document windows alive. A normal PowerShell exits and releases the COM
# references, so the listener disappears / stops serving. This agent:
#   * creates the Application singleton (Connection=1 TCP, 127.0.0.1:502),
#   * creates document windows on demand (ADD command) and KEEPS them alive,
#   * calls OpenConnection() once at startup and never releases anything.
#
# Protocol (line-based, ControlFile truncated after each batch):
#   ADD <id> <fc> <slave> <addr> <qty>   create+ShowWindow+Setup<Function>, keep alive
#   STAT                                   log current doc count + mbslave pid
#   QUIT                                   CloseConnection() then exit

param(
  [string]$ControlFile = "D:\Code\NitroGateway\tools\factory-test\agent-control.txt",
  [string]$StatusFile  = "D:\Code\NitroGateway\tools\factory-test\agent-status.txt"
)

$ErrorActionPreference = 'Stop'

function Write-Log { param([string]$M) Add-Content -Path $StatusFile -Value $M -Encoding UTF8 }

if (-not (Test-Path $ControlFile)) { New-Item -ItemType File -Path $ControlFile -Force | Out-Null }

$script:app = $null
$script:docs = New-Object System.Collections.ArrayList

function Initialize-App {
  $script:app = New-Object -ComObject Mbslave.Application
  $type = $script:app.GetType()
  # Force TCP/IP (1 of 5 connection types; per extracted VBA example).
  try { $type.InvokeMember('Connection', [System.Reflection.BindingFlags]::SetProperty, $null, $script:app, @(1)) | Out-Null } catch { Write-Log "AGENT set-Connection failed: $($_.Exception.Message)" }
  try { $type.InvokeMember('IPAddress', [System.Reflection.BindingFlags]::SetProperty, $null, $script:app, @('127.0.0.1')) | Out-Null } catch {}
  try { $type.InvokeMember('ServerPort', [System.Reflection.BindingFlags]::SetProperty, $null, $script:app, @(502)) | Out-Null } catch {}
  $open = $type.InvokeMember('OpenConnection', [System.Reflection.BindingFlags]::InvokeMethod, $null, $script:app, $null)
  Write-Log ("AGENT init pid={0} conn={1} open={2}" -f $PID, $type.InvokeMember('Connection',[System.Reflection.BindingFlags]::GetProperty,$null,$script:app,$null), $open)
}

function Add-Doc {
  param([int]$Id, [int]$Fc, [int]$Slave, [int]$Addr, [int]$Qty)
  try {
    $doc = New-Object -ComObject Mbslave.Document
    $sw = $doc.ShowWindow()
    $r = $null
    switch ($Fc) {
      3 { $r = $doc.SetupHoldingRegisters($Slave, $Addr, $Qty) }
      4 { $r = $doc.SetupInputRegisters($Slave, $Addr, $Qty) }
      1 { $r = $doc.SetupCoils($Slave, $Addr, $Qty) }
      2 { $r = $doc.SetupDiscreteInputs($Slave, $Addr, $Qty) }
      default { throw "bad fc=$Fc" }
    }
    [void]$script:docs.Add($doc)
    Write-Log ("DOC id={0} fc={1} slave={2} addr={3} qty={4} show=[{5}] setup={6} docs={7}" -f $Id, $Fc, $Slave, $Addr, $Qty, $sw, $r, $script:docs.Count)
  } catch {
    Write-Log ("DOC-ERR id={0} fc={1} slave={2} addr={3} qty={4} msg={5}" -f $Id, $Fc, $Slave, $Addr, $Qty, $_.Exception.Message)
  }
}

Write-Log "AGENT start pid=$PID control=$ControlFile"
Initialize-App

while ($true) {
  try {
    $lines = @(Get-Content -Path $ControlFile -ErrorAction SilentlyContinue)
    if ($lines.Count -gt 0) {
      foreach ($line in $lines) {
        $t = $line.Trim()
        if (-not $t) { continue }
        if ($t -eq 'QUIT') {
          try { $type.InvokeMember('CloseConnection',[System.Reflection.BindingFlags]::InvokeMethod,$null,$script:app,$null) | Out-Null } catch {}
          Write-Log "AGENT quit pid=$PID docs=$($script:docs.Count)"
          try { Remove-Item -LiteralPath $ControlFile -Force -ErrorAction SilentlyContinue } catch {}
          exit 0
        }
        if ($t -eq 'STAT') {
          Write-Log "STAT docs=$($script:docs.Count)"
          continue
        }
        if ($t -like 'ADD*') {
          $p = $t -split '\s+'
          if ($p.Count -ge 6) {
            Add-Doc -Id ([int]$p[1]) -Fc ([int]$p[2]) -Slave ([int]$p[3]) -Addr ([int]$p[4]) -Qty ([int]$p[5])
          } else {
            Write-Log "AGENT bad-command: $t"
          }
        } else {
          Write-Log "AGENT unknown-command: $t"
        }
      }
      Clear-Content -Path $ControlFile -ErrorAction SilentlyContinue
    }
  } catch {
    Write-Log "AGENT loop-err: $($_.Exception.Message)"
  }
  Start-Sleep -Milliseconds 200
}

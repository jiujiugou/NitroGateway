# Create Application + Document (FC03 slave1 qty90, FC04 slave1 qty6), set TCP,
# OpenConnection, then probe while staying alive. Compare with handoff's success
# which created documents BEFORE OpenConnection.
param(
  [string]$ResultFile = "D:\Code\NitroGateway\tools\factory-test\com-open2.txt"
)
$ErrorActionPreference = 'Continue'
$out = New-Object System.Text.StringBuilder
function L($m) { [void]$out.AppendLine($m) }

$app = $null
$docs = New-Object System.Collections.ArrayList
try {
  $app = New-Object -ComObject Mbslave.Application
  $type = $app.GetType()

  $type.InvokeMember('Connection', [System.Reflection.BindingFlags]::SetProperty, $null, $app, @(1)) | Out-Null
  L "Connection set to 1 (TCP)"
  $type.InvokeMember('IPAddress', [System.Reflection.BindingFlags]::SetProperty, $null, $app, @('127.0.0.1')) | Out-Null
  $type.InvokeMember('ServerPort', [System.Reflection.BindingFlags]::SetProperty, $null, $app, @(502)) | Out-Null
  L "IP/Port set"

  # Create documents FIRST (matching handoff's successful flow)
  $doc = New-Object -ComObject Mbslave.Document
  $sw1 = $doc.ShowWindow()
  $r1 = $doc.SetupHoldingRegisters(1, 0, 90)
  [void]$docs.Add($doc)
  L "FC03 slave1 qty90: show=$sw1 setup=$r1"

  $doc2 = New-Object -ComObject Mbslave.Document
  $sw2 = $doc2.ShowWindow()
  $r2 = $doc2.SetupInputRegisters(1, 0, 6)
  [void]$docs.Add($doc2)
  L "FC04 slave1 qty6: show=$sw2 setup=$r2"

  $doc3 = New-Object -ComObject Mbslave.Document
  $sw3 = $doc3.ShowWindow()
  $r3 = $doc3.SetupCoils(1, 0, 2)
  [void]$docs.Add($doc3)
  L "FC01 slave1 qty2: show=$sw3 setup=$r3"

  $open = $type.InvokeMember('OpenConnection', [System.Reflection.BindingFlags]::InvokeMethod, $null, $app, $null)
  L "OpenConnection() = $open"
  Start-Sleep -Seconds 2

  python "D:\Code\NitroGateway\tools\factory-test\probe2.py"
  Start-Sleep -Milliseconds 300
  $probe = Get-Content "D:\Code\NitroGateway\tools\factory-test\probe-result2.txt" -Raw -ErrorAction SilentlyContinue
  L "PROBE:`n$probe"

  try { $type.InvokeMember('CloseConnection', [System.Reflection.BindingFlags]::InvokeMethod, $null, $app, $null) | Out-Null; L "CloseConnection ok" } catch { L "CloseConnection: $($_.Exception.Message)" }
} catch {
  L "TOP-LEVEL ERROR: $($_.Exception.ToString())"
} finally {
  foreach ($d in $docs) { try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($d) } catch {} }
  if ($app) { try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($app) } catch {} }
  [System.IO.File]::WriteAllText($ResultFile, $out.ToString(), [System.Text.Encoding]::UTF8)
  Write-Output $out.ToString()
}

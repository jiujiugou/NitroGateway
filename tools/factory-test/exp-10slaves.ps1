# experiment: ONE doc per slave (10 windows), each with 4 function blocks.
# Model: Modbus Slave window = 1 slave (slave ID); multiple blocks per window.
param(
  [string]$ResultFile = "D:\Code\NitroGateway\tools\factory-test\exp-10slaves.txt"
)
$ErrorActionPreference = 'Continue'
$out = New-Object System.Text.StringBuilder
function L($m) { [void]$out.AppendLine($m) }

$app = $null
$docs = New-Object System.Collections.ArrayList
try {
  $app = New-Object -ComObject Mbslave.Application
  $type = $app.GetType()
  foreach ($pr in @(@('Connection',1),@('Mode',1),@('IPAddress','127.0.0.1'),@('ServerPort',502),@('IPVersion',4))) {
    try { $type.InvokeMember($pr[0], [System.Reflection.BindingFlags]::SetProperty, $null, $app, @($pr[1])) | Out-Null; L "set $($pr[0]) = $($pr[1]) ok" }
    catch { L "set $($pr[0]) fail: $($_.Exception.Message)" }
  }

  $slaveCount = 10
  for ($s = 1; $s -le $slaveCount; $s++) {
    $doc = New-Object -ComObject Mbslave.Document
    $sw = $doc.ShowWindow()
    $r1 = $doc.SetupHoldingRegisters($s, 0, 90)   # FC03 addr0 qty90
    $r2 = $doc.SetupInputRegisters($s, 0, 6)       # FC04 addr0 qty6
    $r3 = $doc.SetupCoils($s, 0, 2)               # FC01 addr0 qty2
    $r4 = $doc.SetupDiscreteInputs($s, 0, 2)      # FC02 addr0 qty2
    [void]$docs.Add($doc)
    L "slave=$s show=[$sw] HR=$r1 IR=$r2 COIL=$r3 DI=$r4 docs=$($docs.Count)"
  }

  $open = $type.InvokeMember('OpenConnection', [System.Reflection.BindingFlags]::InvokeMethod, $null, $app, $null)
  L "OpenConnection() = $open"
  Start-Sleep -Seconds 2

  python "D:\Code\NitroGateway\tools\factory-test\diag1.py"
  Start-Sleep -Milliseconds 400
  $probe = Get-Content "D:\Code\NitroGateway\tools\factory-test\diag1.txt" -Raw -ErrorAction SilentlyContinue
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

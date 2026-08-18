# Diagnose the Mbslave.Application COM interface and OpenConnection/TCP serving,
# while keeping this process alive so the automation client stays connected.
param(
  [string]$ResultFile = "D:\Code\NitroGateway\tools\factory-test\com-inspect.txt"
)
$ErrorActionPreference = 'Continue'
$out = New-Object System.Text.StringBuilder
function L($m) { [void]$out.AppendLine($m) }

$app = $null
try {
  $app = New-Object -ComObject Mbslave.Application
  L "app type = $($app.GetType().FullName)"
  $type = $app.GetType()

  foreach ($name in @('Connection','IPAddress','ServerPort','IPVersion','Mode','SerialPort','BaudRate','DataBits','StopBits','Parity')) {
    $val = '<fail>'
    try {
      $val = $type.InvokeMember($name, [System.Reflection.BindingFlags]::GetProperty, $null, $app, $null)
    } catch {
      $e1 = $_.Exception.Message
      try {
        $val = $type.InvokeMember($name, [System.Reflection.BindingFlags]::InvokeMethod, $null, $app, $null)
        $val = "(method) $val"
      } catch { $val = "GET/INVOKE fail: $e1 | $($_.Exception.Message)" }
    }
    L "read  $name = $val"
  }

  # Try to force TCP mode. Guess Connection=1 means TCP (per earlier IDispatch probe).
  foreach ($name in @('Connection','Mode')) {
    try {
      $type.InvokeMember($name, [System.Reflection.BindingFlags]::SetProperty, $null, $app, @(1)) | Out-Null
      L "set   $name = 1 -> ok"
    } catch { L "set   $name = 1 -> $($_.Exception.Message)" }
  }
  try {
    $type.InvokeMember('IPAddress', [System.Reflection.BindingFlags]::SetProperty, $null, $app, @('127.0.0.1')) | Out-Null
    L "set   IPAddress = 127.0.0.1 -> ok"
  } catch { L "set   IPAddress -> $($_.Exception.Message)" }
  try {
    $type.InvokeMember('ServerPort', [System.Reflection.BindingFlags]::SetProperty, $null, $app, @(502)) | Out-Null
    L "set   ServerPort = 502 -> ok"
  } catch { L "set   ServerPort -> $($_.Exception.Message)" }

  foreach ($name in @('Connection','Mode','IPAddress','ServerPort')) {
    try { $v = $type.InvokeMember($name, [System.Reflection.BindingFlags]::GetProperty, $null, $app, $null); L "after $name = $v" }
    catch { L "after $name fail: $($_.Exception.Message)" }
  }

  $open = $type.InvokeMember('OpenConnection', [System.Reflection.BindingFlags]::InvokeMethod, $null, $app, $null)
  L "OpenConnection() = $open"
  Start-Sleep -Milliseconds 800

  python "D:\Code\NitroGateway\tools\factory-test\probe2.py"
  Start-Sleep -Milliseconds 300
  $probe = Get-Content "D:\Code\NitroGateway\tools\factory-test\probe-result2.txt" -Raw -ErrorAction SilentlyContinue
  L "PROBE:`n$probe"

  try { $type.InvokeMember('CloseConnection', [System.Reflection.BindingFlags]::InvokeMethod, $null, $app, $null) | Out-Null; L "CloseConnection() ok" } catch { L "CloseConnection: $($_.Exception.Message)" }
} catch {
  L "TOP-LEVEL ERROR: $($_.Exception.ToString())"
} finally {
  if ($app) { try { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($app) } catch {} }
  [System.IO.File]::WriteAllText($ResultFile, $out.ToString(), [System.Text.Encoding]::UTF8)
  Write-Output $out.ToString()
}

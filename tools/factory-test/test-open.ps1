# Diagnostic: does OpenConnection() actually make TCP respond, while this
# PowerShell stays alive (probe runs as a child of this same process)?
$ErrorActionPreference = 'Stop'
$app = New-Object -ComObject Mbslave.Application
Write-Output "app created"
$before = $app.Connection
Write-Output "connection-before=$before"
$open = $app.OpenConnection()
Write-Output "open=$open"
$after = $app.Connection
Write-Output "connection-after=$after"
Start-Sleep -Milliseconds 800
# run probe as child while we stay alive
python "D:\Code\NitroGateway\tools\factory-test\probe3.py"
Start-Sleep -Milliseconds 300
if (Test-Path "D:\Code\NitroGateway\tools\factory-test\probe-result3.txt") {
  Write-Output "--- probe-result3 ---"
  Get-Content "D:\Code\NitroGateway\tools\factory-test\probe-result3.txt" -Encoding UTF8
}
Write-Output "still alive, releasing"

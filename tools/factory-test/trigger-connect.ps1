# Post F3 (Connect) to the mbslave main window via the message queue so MFC's
# accelerator processing picks it up without needing foreground focus.
param(
  [switch]$Quick,
  [switch]$DumpDialog
)
$ErrorActionPreference = 'Continue'
$sig = @'
using System;
using System.Runtime.InteropServices;
public static class KeyWin {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
  public const uint WM_KEYDOWN = 0x0100;
  public const uint WM_KEYUP = 0x0101;
  public const uint WM_COMMAND = 0x0111;
  public const byte VK_F3 = 0x72;
  public const byte VK_F5 = 0x74;
}
'@
Add-Type -TypeDefinition $sig
$p = Get-Process mbslave -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output "no mbslave"; exit }
$main = $p.MainWindowHandle
[KeyWin]::ShowWindow($main, 9) | Out-Null
[KeyWin]::SetForegroundWindow($main) | Out-Null
Start-Sleep -Milliseconds 300
if ($Quick) {
  Write-Output "trigger Quick Connect (F5 / WM_COMMAND 32838)"
  [KeyWin]::SendMessage($main, [KeyWin]::WM_COMMAND, [IntPtr]32838, [IntPtr]::Zero) | Out-Null
} else {
  Write-Output "trigger Connect (F3 keydown+keyup)"
  [void][KeyWin]::PostMessage($main, [KeyWin]::WM_KEYDOWN, [IntPtr][KeyWin]::VK_F3, [IntPtr]::Zero)
  [void][KeyWin]::PostMessage($main, [KeyWin]::WM_KEYUP,   [IntPtr][KeyWin]::VK_F3, [IntPtr]::Zero)
}
Start-Sleep -Seconds 2
if ($DumpDialog) {
  Add-Type -AssemblyName UIAutomationClient
  Add-Type -AssemblyName UIAutomationTypes
  . 'D:\Code\NitroGateway\tools\uia-mbslave.ps1'
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')
  $dlgs = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
  Write-Output "dialogs: $($dlgs.Count)"
  for ($i=0; $i -lt $dlgs.Count; $i++) {
    $d = $dlgs.Item($i)
    Write-Output ("dlg[{0}] name='{1}' rect={2}" -f $i, $d.Current.Name, $d.Current.BoundingRectangle)
    $dump = "D:\Code\NitroGateway\tools\factory-test\connect-dialog.txt"
    Dump-UiaTree -RootHwnd $d.Current.NativeWindowHandle -OutFile $dump
    Get-Content $dump -Encoding UTF8 | Select-Object -First 70
  }
}

<#
.SYNOPSIS
  Issue #116 (criterion 11, Windows): drive the desktop app's tray menu Quit and prove a clean exit.

.DESCRIPTION
  Starts AgentEyesDesktop.exe --tray, opens its tray menu, optionally dismisses it and opens it
  again, then clicks "Quit AgentEyes" - and reports whether the process exited with code 0, wrote
  [App] Quit, [SingleInstance] Dispose and [Program] Main: exited, raised no unhandled exception,
  and left no named pipe behind.

  NO INPUT IS SYNTHESIZED AT THE CURSOR AND NO OTHER WINDOW IS TOUCHED. Every message goes to a
  window owned by the process this script started:
    * the menu is opened with the shell's own tray callback - PostMessage of WM_TRAYMOUSE (0x800,
      Avalonia.Win32.TrayIconImpl) to the app's AvaloniaMessageWindow, wParam = icon id 1,
      lParam = WM_RBUTTONUP (what Explorer sends on a right-click);
    * the menu is dismissed with WM_CLOSE to the popup (what Alt+F4 and UIA WindowPattern.Close send);
    * Quit is clicked with WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP posted to the popup at the
      Quit item's client point, read from UI Automation.
  The menu popup is visible on screen for under a second per open.

.PARAMETER Exe
  The AgentEyesDesktop.exe to run. Defaults to the x64 Release build in this repository.

.PARAMETER DismissFirst
  Open the menu and dismiss it (WM_CLOSE) DismissFirst times before the open that clicks Quit.

.PARAMETER Action
  quit  (default) - click Quit and expect a clean exit.
  close - open and dismiss only (DismissFirst + 1 times), then expect the process still ALIVE with
          no unhandled exception; the script then quits it the same way.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\AgentEyes.DesktopApp\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesDesktop.exe'),
    [int]$DismissFirst = 0,
    [ValidateSet('quit', 'close')][string]$Action = 'quit'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class TrayRepro
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }

    // Top-level windows plus message-only windows (HWND_MESSAGE = -3) owned by the process.
    public static List<IntPtr> WindowsOf(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid) list.Add(h); return true; }, IntPtr.Zero);
        IntPtr after = IntPtr.Zero;
        while ((after = FindWindowEx(new IntPtr(-3), after, null, null)) != IntPtr.Zero)
        { uint p; GetWindowThreadProcessId(after, out p); if (p == pid) list.Add(after); }
        return list;
    }
}
'@

$WM_TRAYMOUSE = 0x800; $WM_RBUTTONUP = 0x205; $WM_CLOSE = 0x10
$WM_MOUSEMOVE = 0x200; $WM_LBUTTONDOWN = 0x201; $WM_LBUTTONUP = 0x202
$PipeName = "agenteyes-app-$($env:USERNAME.ToLowerInvariant())"

function Stamp([string]$msg) { Write-Output ("[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $msg) }
function PipePresent { @([System.IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like "*\$PipeName" }).Count -gt 0 }
function Lparam([int]$x, [int]$y) { [IntPtr](($y -shl 16) -bor ($x -band 0xFFFF)) }

function Get-MessageWindow([uint32]$procId) {
    foreach ($h in [TrayRepro]::WindowsOf($procId)) { if ([TrayRepro]::ClassOf($h) -like '*AvaloniaMessageWindow*') { return $h } }
    return [IntPtr]::Zero
}
function Get-Popups([uint32]$procId) {
    # The tray menu is an Avalonia TrayPopupRoot window; its UIA AutomationId is "AvaloniaTrayPopupRoot_<tooltip>".
    $found = @()
    foreach ($h in [TrayRepro]::WindowsOf($procId)) {
        if (-not [TrayRepro]::IsWindowVisible($h)) { continue }
        # A window can be destroyed between the enumeration and this query (the app creates and drops
        # short-lived helper windows); such a window is not the menu, so it is skipped.
        try { $aid = [System.Windows.Automation.AutomationElement]::FromHandle($h).Current.AutomationId }
        catch { continue }
        if ($aid -like 'AvaloniaTrayPopupRoot*') { $found += $h }
    }
    return , $found
}
function Open-Menu([uint32]$procId, [IntPtr]$msgWnd) {
    [void][TrayRepro]::PostMessage($msgWnd, $WM_TRAYMOUSE, [IntPtr]1, [IntPtr]$WM_RBUTTONUP)
    for ($i = 0; $i -lt 300; $i++) {
        Start-Sleep -Milliseconds 100
        $p = Get-Popups $procId
        if ($p.Count -ge 1) { return $p[0] }
    }
    throw "the tray menu popup did not appear within 30 s after WM_TRAYMOUSE/WM_RBUTTONUP"
}
function Click-Quit([IntPtr]$popup) {
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($popup)
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $quit = $null
    # The popup's items reach the UIA tree after its first layout pass, so wait for them.
    for ($i = 0; $i -lt 100 -and $null -eq $quit; $i++) {
        foreach ($item in $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
            if ($item.Current.Name -like 'Quit AgentEyes*') { $quit = $item }
        }
        if ($null -eq $quit) { Start-Sleep -Milliseconds 100 }
    }
    if ($null -eq $quit) { throw "no 'Quit AgentEyes' menu item in popup 0x$($popup.ToString('X')) after 10 s" }
    $r = $quit.Current.BoundingRectangle
    $pt = New-Object TrayRepro+POINT
    $pt.X = [int]($r.X + $r.Width / 2); $pt.Y = [int]($r.Y + $r.Height / 2)
    [void][TrayRepro]::ScreenToClient($popup, [ref]$pt)
    $lp = Lparam $pt.X $pt.Y
    [void][TrayRepro]::PostMessage($popup, $WM_MOUSEMOVE, [IntPtr]0, $lp)
    Start-Sleep -Milliseconds 80
    [void][TrayRepro]::PostMessage($popup, $WM_LBUTTONDOWN, [IntPtr]1, $lp)
    Start-Sleep -Milliseconds 40
    [void][TrayRepro]::PostMessage($popup, $WM_LBUTTONUP, [IntPtr]0, $lp)
    return "$($pt.X),$($pt.Y)"
}
function Dismiss-Menu([uint32]$procId, [IntPtr]$popup) {
    [void][TrayRepro]::PostMessage($popup, $WM_CLOSE, [IntPtr]0, [IntPtr]0)
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 100; if ((Get-Popups $procId).Count -eq 0) { return } }
    throw "the menu popup 0x$($popup.ToString('X')) was still visible 3 s after WM_CLOSE"
}

$Exe = (Resolve-Path $Exe).Path
if (Get-Process AgentEyesDesktop -ErrorAction SilentlyContinue) { throw "an AgentEyesDesktop process is already running - this repro needs to be the first instance" }
if (PipePresent) { throw "pipe $PipeName already exists before start" }

$logFile = Join-Path $env:LOCALAPPDATA ("AgentEyes\logs\AgentEyes-{0:yyyyMMdd}.log" -f (Get-Date))
$proc = Start-Process -FilePath $Exe -ArgumentList '--tray' -PassThru
Stamp "started $Exe --tray pid=$($proc.Id) action=$Action dismissFirst=$DismissFirst"

$msgWnd = [IntPtr]::Zero
for ($i = 0; $i -lt 600 -and $msgWnd -eq [IntPtr]::Zero; $i++) {
    Start-Sleep -Milliseconds 100
    if ($proc.HasExited) { throw "process exited during start-up with code $($proc.ExitCode)" }
    $msgWnd = Get-MessageWindow ([uint32]$proc.Id)
}
if ($msgWnd -eq [IntPtr]::Zero) { throw "no AvaloniaMessageWindow for pid $($proc.Id) after 60 s" }
Start-Sleep -Milliseconds 1500   # let the tray icon register with the shell
Stamp "message window=0x$($msgWnd.ToString('X')); pipe present while running=$(PipePresent)"

# The app logs "[Program] unhandled exception" the moment it goes down; the process itself can take
# seconds longer to die while Windows Error Reporting writes its dump, so the log is the first signal.
function Crashed { @(Get-Content $logFile | Where-Object { $_ -match "\[pid $($proc.Id)\].*unhandled exception" }).Count -gt 0 }

$opens = $DismissFirst + $(if ($Action -eq 'close') { 1 } else { 0 })
try {
    for ($n = 1; $n -le $opens; $n++) {
        $popup = Open-Menu ([uint32]$proc.Id) $msgWnd
        Stamp "open $n`: menu popup=0x$($popup.ToString('X')); dismissing with WM_CLOSE"
        Dismiss-Menu ([uint32]$proc.Id) $popup
        Start-Sleep -Milliseconds 1000
        if ($proc.HasExited -or (Crashed)) { throw "the app went down after dismissing the menu (open $n)" }
    }
    if ($Action -eq 'close') {
        Start-Sleep -Seconds 2
        if ($proc.HasExited -or (Crashed)) { throw "the app went down after $opens open/dismiss cycle(s)" }
        Stamp "after $opens open/dismiss cycle(s): alive, no unhandled exception"
    }
    $popup = Open-Menu ([uint32]$proc.Id) $msgWnd
    $at = Click-Quit $popup
    Stamp "menu popup=0x$($popup.ToString('X')); posted a left click to Quit at client $at"
    if (-not $proc.WaitForExit(15000)) { Stamp "FAIL: process did NOT exit within 15 s" }
}
catch {
    # Reported below with the app's own log lines; the RESULT line is FAIL either way.
    Stamp "FAIL: $($_.Exception.Message)"
}
Start-Sleep -Milliseconds 300
$exitCode = if ($proc.HasExited) { $proc.ExitCode } else { 'running' }
$pipeAfter = PipePresent
Stamp "process exited=$($proc.HasExited) exitCode=$exitCode; pipe present after=$pipeAfter"

$lines = @(Get-Content $logFile | Where-Object { $_ -match "\[pid $($proc.Id)\]" -and $_ -notmatch '\[ERROR\]    at ' })
Write-Output "--- log lines for pid $($proc.Id) (stack frames elided):"
$lines | ForEach-Object { Write-Output $_ }

$need = '[App] Quit', '[SingleInstance] Dispose', '[Program] Main: exited'
$missing = @($need | Where-Object { $n = $_; -not ($lines | Where-Object { $_.Contains($n) }) })
$crashed = @($lines | Where-Object { $_ -match 'unhandled exception' }).Count -gt 0
$pass = ($proc.HasExited -and $exitCode -eq 0 -and $missing.Count -eq 0 -and -not $crashed -and -not $pipeAfter)
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; Stamp "killed the leftover test process $($proc.Id)" }
Write-Output ("RESULT: {0} (exit={1}, missing=[{2}], unhandled={3}, pipeAfter={4})" -f $(if ($pass) { 'PASS' } else { 'FAIL' }), $exitCode, ($missing -join '; '), $crashed, $pipeAfter)
if (-not $pass) { exit 1 }

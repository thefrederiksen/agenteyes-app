# Issue #3 running-app proof. READ ONLY with respect to the owner's recordings: it launches the
# branch build, selects the Library view via UI Automation, and compares the RENDERED rows against
# the recording folders on disk. It never records, never renames, never deletes, and it never
# force-foregrounds the window or synthesizes input.
#
# BEFORE: the installed AgentEyes must not be running - it owns port 7882 and the tray. Check
#   Invoke-RestMethod http://127.0.0.1:7882/status
# is 'idle' (NEVER stop it mid-recording), then stop it.
# AFTER: this script does NOT restart the installed app. Put the always-on recorder back yourself:
#   Start-Process "$env:LOCALAPPDATA\AgentEyes\app\AgentEyesApp.exe" --tray
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

# docs/cencon/proof/issue-3 -> the repository root is four levels up.
$root = Split-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) -Parent
$exe  = Join-Path $root 'src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe'
$vid  = Join-Path $env:USERPROFILE 'Videos\AgentEyes'
$log  = Join-Path $env:LOCALAPPDATA 'AgentEyes\logs'
$crash = Join-Path $env:TEMP 'AgentEyes-crash.log'

if (-not (Test-Path $exe)) { throw "branch build missing: $exe" }
if (Test-Path $crash) { Remove-Item $crash -Force }

# What is actually on disk, right now (read only).
$onDisk = @(Get-ChildItem -Path $vid -Directory |
            Where-Object { Test-Path (Join-Path $_.FullName 'manifest.json') } |
            Select-Object -ExpandProperty Name | Sort-Object)
"ON DISK: $($onDisk.Count) recording folder(s) with a manifest.json"

$started = Get-Date
$proc = Start-Process -FilePath $exe -PassThru
"launched pid=$($proc.Id)"

$A = [System.Windows.Automation.AutomationElement]
function NameIs($n) { New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $n) }
function TypeIs($t) { New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $t) }
function BothOf($a,$b) { New-Object System.Windows.Automation.AndCondition($a,$b) }

$win = $null
for ($i = 0; $i -lt 40; $i++) {
  $win = $A::RootElement.FindFirst('Children', (BothOf (NameIs 'AgentEyes') (TypeIs ([System.Windows.Automation.ControlType]::Window))))
  if ($win) { break }
  Start-Sleep -Milliseconds 500
}
if (-not $win) { Stop-Process -Id $proc.Id -Force; throw 'main window "AgentEyes" not found within 20s' }
"main window found"

# Health, without touching the recorder.
$health = Invoke-RestMethod -Uri 'http://127.0.0.1:7882/health' -TimeoutSec 10
"HEALTH: ok=$($health.ok) app=$($health.app)"

# Select the Library view - a UIA invoke on the rail, no synthesized mouse or keyboard.
$rail = $win.FindFirst('Descendants', (NameIs 'Library view'))
if (-not $rail) { Stop-Process -Id $proc.Id -Force; throw 'the Library rail button was not found' }
($rail.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Start-Sleep -Seconds 3

# Every rendered Library row publishes the recording folder name as its AutomationId (issue #178).
$rendered = @()
for ($i = 0; $i -lt 20; $i++) {
  $items = $win.FindAll('Descendants', (TypeIs ([System.Windows.Automation.ControlType]::ListItem)))
  $rendered = @(foreach ($n in 0..([Math]::Max($items.Count - 1, 0))) {
                  if ($items.Count -gt 0) { $items[$n].Current.AutomationId }
                }) | Where-Object { $_ -and $_ -match '_' } | Sort-Object -Unique
  if ($rendered.Count -gt 0) { break }
  Start-Sleep -Milliseconds 500
}
"RENDERED: $($rendered.Count) library row(s)"

$missing = @($onDisk | Where-Object { $rendered -notcontains $_ })
$extra   = @($rendered | Where-Object { $onDisk -notcontains $_ })
"MISSING FROM THE LIBRARY: $($missing.Count)"
if ($missing.Count) { $missing | ForEach-Object { "  - $_" } }
"IN THE LIBRARY BUT NOT ON DISK: $($extra.Count)"
if ($extra.Count) { $extra | ForEach-Object { "  - $_" } }

Stop-Process -Id $proc.Id -Force
Start-Sleep -Seconds 2

"---- coherence log lines from this run ----"
$latest = Get-ChildItem $log -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $latest) { $latest = Get-ChildItem (Split-Path $log -Parent) -Filter '*.log' -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 }
if ($latest) {
  "log: $($latest.FullName)"
  Get-Content $latest.FullName -Tail 400 | Where-Object { $_ -match 'LibraryCoherence|coherence model|RecentItemCollection|Unhandled|Exception' }
} else { "NO LOG FILE FOUND - the log location has to be checked by hand" }

"---- crash log ----"
if (Test-Path $crash) { "CRASH LOG PRESENT:"; Get-Content $crash -Tail 40 } else { "no crash log (good)" }

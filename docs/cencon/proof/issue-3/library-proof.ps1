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

# Any BRANCH app still running from a previous run has to go before anything else - it holds a lock
# on the build output, so the compile below would fail with MSB3027 "the file is locked by
# AgentEyesApp". One survived on 2026-08-18 because this script was piped through
# Select-Object -First N, which closes the pipeline and stops the script before its own Stop-Process
# line. Sweeping here is deterministic and does not depend on the previous run having ended tidily.
# It matches on PATH, so the owner's INSTALLED app under %LOCALAPPDATA% is never touched by it.
Get-Process AgentEyesApp -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } |
  ForEach-Object { "stopping a branch app left from an earlier run: pid=$($_.Id)"; Stop-Process -Id $_.Id -Force }

# COMPILE THE BINARY UNDER TEST, HERE, EVERY RUN. This does NOT detect a stale build - it removes
# the possibility of one, which is the point: the exe launched below is compiled from the source in
# this tree by the line beneath this comment, seconds before it runs.
#
# It replaces a timestamp check that was not sound. That check compared the newest source file's
# LastWriteTime against the exe's and refused when a source was newer. It caught the case it was
# written for, but not the ordinary one: a restore that PRESERVES the original timestamp - Copy-Item,
# cp -p, robocopy, an archive extract - leaves the source OLDER than the exe, so the check passes,
# and then dotnet build skips recompiling while still printing "Build succeeded" and the
# "AgentEyes.App ->" line. Verified on 2026-08-18: after such a restore the dll hash was unchanged
# across a normal build (930f1492a3175d0f before and after) while the binary still carried a mutated
# ApplySnapshot, and the proof reported 44 of 44. Timestamps cannot decide freshness when whatever
# restored the file decides the timestamp.
#
# --no-incremental is what makes this a real recompile rather than another skipped build. A build
# FAILURE aborts the run: falling through to whatever exe happens to be on disk is the very thing
# this exists to prevent.
"compiling the branch build (no-incremental) so the binary under test is this tree's source..."
& dotnet build (Join-Path $root 'AgentEyes.sln') -c Release --no-incremental | Out-Null
if ($LASTEXITCODE -ne 0) {
  throw ("BUILD FAILED (exit $LASTEXITCODE) - aborting rather than testing whatever exe is on disk. " +
         "Run 'dotnet build AgentEyes.sln -c Release' to see the errors.")
}

if (-not (Test-Path $exe)) { throw "branch build missing after a successful build: $exe" }

# Reported, not asserted: this identifies the binary in the record. It is not a freshness claim - the
# compile above is what makes the binary fresh.
#
# It hashes the managed DLL, not the .exe. The .exe is the apphost launcher and barely ever changes:
# across the demonstration above it read 6C12D8F1515F6C49 whether the code was mutated or not, while
# the DLL went 930F1492A3175D0F (mutated) -> 14D7BD5968886539 (recompiled). Hashing the .exe would
# have printed a reassuring constant.
$dll = [IO.Path]::ChangeExtension($exe, '.dll')
$builtAt = (Get-Item $dll).LastWriteTime
$hash = (Get-FileHash -Path $dll -Algorithm SHA256).Hash.Substring(0, 16)
"compiled: $builtAt  AgentEyesApp.dll sha256[0..15]: $hash"

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

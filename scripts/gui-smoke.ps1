# GUI smoke test - drives the AgentEyes app via UI Automation (no human needed).
# The launcher is preset-based: this test installs its own temporary presets (video+mixed -
# the mux path that previously crashed - audio+mic, and screenshot), selects each in the
# PRESET combo and records through the REC button. Every UI interaction is asserted - a
# button that cannot be found or a state that never arrives fails the test immediately.
# The user's presets.json and config.json are backed up and restored; the microphone is
# discovered at runtime so the test passes on console and over RDP.
param([switch]$Confirm)
$ErrorActionPreference = 'Stop'
# USER-INVOKED ONLY (revised 2026-06-16): launches the app and records via UIA. Agents must NOT run it.
if (-not $Confirm -and $env:MQS_RUN_TESTS -ne '1') {
  Write-Host "REFUSED: gui-smoke.ps1 launches the app and records via UIA - USER-INVOKED ONLY. Re-run with -Confirm (or set MQS_RUN_TESTS=1)."
  exit 3
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root   = Split-Path $PSScriptRoot -Parent
$exe    = Join-Path $root 'src\AgentEyes.App\bin\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe'
$cli    = Join-Path $root 'src\AgentEyes.Core\bin\Release\net8.0-windows10.0.19041.0\agenteyes.exe'
$appdir = Join-Path $env:LOCALAPPDATA 'AgentEyes'
$crash  = Join-Path $env:TEMP 'AgentEyes-crash.log'
$vid    = Join-Path $env:USERPROFILE 'Videos\AgentEyes'

if (-not (Test-Path $exe)) { "GUI-SMOKE: FAIL (app not built: $exe)"; exit 1 }
if (-not (Test-Path $cli)) { "GUI-SMOKE: FAIL (engine not built: $cli)"; exit 1 }

# ---- discover a real microphone (NAudio name; also a fragment of the dshow name) ----
$micLines = & $cli screens | Where-Object { $_ -match '^\s+\[\d+\]\s+\S' }
if (-not $micLines) { "GUI-SMOKE: FAIL (no microphone found - 'agenteyes screens' listed none)"; exit 1 }
$mic = ([regex]::Match(@($micLines)[0], '^\s+\[\d+\]\s+(.+)$')).Groups[1].Value.Trim()
"  mic: $mic"

# ---- UIA helpers (every one throws on failure - no silent degradation) ----
$A = [System.Windows.Automation.AutomationElement]
function NameIs($name)  { New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name) }
function TypeIs($type)  { New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $type) }
function BothOf($a, $b) { New-Object System.Windows.Automation.AndCondition($a, $b) }

function Find-MainWindow {
    for ($i = 0; $i -lt 20; $i++) {
        $w = $A::RootElement.FindFirst('Children', (BothOf (NameIs 'AgentEyes') (TypeIs ([System.Windows.Automation.ControlType]::Window))))
        if ($w) { return $w }
        Start-Sleep -Milliseconds 500
    }
    throw 'main window "AgentEyes" not found within 10s'
}
function Find-Button($win, $name) {
    $win.FindFirst('Descendants', (BothOf (NameIs $name) (TypeIs ([System.Windows.Automation.ControlType]::Button))))
}
function Wait-Button($win, $name, $timeoutSec) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $b = Find-Button $win $name
        if ($b -and $b.Current.IsEnabled) { return $b }
        Start-Sleep -Milliseconds 400
    }
    throw "button '$name' did not become available within ${timeoutSec}s"
}
function Click-Button($win, $name, $timeoutSec = 10) {
    $b = Wait-Button $win $name $timeoutSec
    ($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Select-Preset($win, $name) {
    # Issue #21: presets live in a split-dropdown on the Record view. Open the menu
    # via the chevron ("Preset menu"), then invoke the preset's MenuItem. The menu
    # popup is its own top-level window of the app process, so search from the root.
    $chevron = $win.FindFirst('Descendants', (BothOf (NameIs 'Preset menu') (TypeIs ([System.Windows.Automation.ControlType]::Button))))
    if (-not $chevron) { throw 'preset menu button not found' }

    # The popup can close again while the app is busy (post-recording transcription
    # runs in the background) - re-open it and retry rather than failing on timing.
    $procCond = New-Object System.Windows.Automation.PropertyCondition(
        $A::ProcessIdProperty, $win.Current.ProcessId)
    $item = $null
    for ($attempt = 0; $attempt -lt 4 -and -not $item; $attempt++) {
        ($chevron.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not $item -and $sw.Elapsed.TotalSeconds -lt 5) {
            Start-Sleep -Milliseconds 400
            foreach ($top in $A::RootElement.FindAll('Children', $procCond)) {
                $item = $top.FindFirst('Descendants', (BothOf (NameIs $name) (TypeIs ([System.Windows.Automation.ControlType]::MenuItem))))
                if ($item) { break }
            }
        }
    }
    if (-not $item) { throw "preset '$name' not found in the preset menu after 4 open attempts" }
    ($item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 400
}

# ---- run (user config backed up; restored in finally) ----
$bakPresets = Join-Path $appdir 'presets.json.smoke-bak'
$bakConfig  = Join-Path $appdir 'config.json.smoke-bak'
$failure = $null
try {
    Get-Process AgentEyes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
    Remove-Item $crash -ErrorAction SilentlyContinue

    if (Test-Path (Join-Path $appdir 'presets.json')) { Copy-Item (Join-Path $appdir 'presets.json') $bakPresets -Force }
    if (Test-Path (Join-Path $appdir 'config.json'))  { Copy-Item (Join-Path $appdir 'config.json')  $bakConfig  -Force }

    $presets = @(
        @{ Id = 'smoke0000000000000000000000000001'; Name = 'smoke video'; Note = $null; MonitorIndex = 1
           UseRegion = $false; Region = $null; Source = 'mixed'; Mic = $mic; Gate = $true
           MicVol = 100; SysVol = 70; Mode = 'video'; Fps = 15 },
        @{ Id = 'smoke0000000000000000000000000002'; Name = 'smoke audio'; Note = $null; MonitorIndex = 1
           UseRegion = $false; Region = $null; Source = 'mic'; Mic = $mic; Gate = $true
           MicVol = 100; SysVol = 70; Mode = 'audio'; Fps = 30 },
        @{ Id = 'smoke0000000000000000000000000003'; Name = 'smoke shot'; Note = $null; MonitorIndex = 1
           UseRegion = $false; Region = $null; Source = 'mixed'; Mic = $null; Gate = $true
           MicVol = 100; SysVol = 70; Mode = 'shot'; Fps = 30 }
    )
    New-Item -ItemType Directory -Force $appdir | Out-Null
    ConvertTo-Json $presets -Depth 4 | Set-Content (Join-Path $appdir 'presets.json') -Encoding UTF8
    ConvertTo-Json @{ LastUsedPresetId = 'smoke0000000000000000000000000001' } |
        Set-Content (Join-Path $appdir 'config.json') -Encoding UTF8

    $beforeNames = @{}
    Get-ChildItem $vid -Directory -ErrorAction SilentlyContinue | ForEach-Object { $beforeNames[$_.Name] = $true }

    Start-Process $exe
    $win = Find-MainWindow

    # 1) video + mixed (the mux path that previously crashed)
    Select-Preset $win 'smoke video'
    Click-Button  $win 'REC'
    Wait-Button   $win 'STOP' 15 | Out-Null
    "  video-mixed: recording started"
    Start-Sleep -Seconds 4
    Click-Button  $win 'STOP'
    Wait-Button   $win 'REC' 30 | Out-Null
    "  video-mixed: stopped and finalized"

    # 2) audio + mic
    Select-Preset $win 'smoke audio'
    Click-Button  $win 'REC'
    Wait-Button   $win 'STOP' 15 | Out-Null
    "  audio-mic: recording started"
    Start-Sleep -Seconds 3
    Click-Button  $win 'STOP'
    Wait-Button   $win 'REC' 30 | Out-Null
    "  audio-mic: stopped and finalized"

    # 3) screenshot
    Select-Preset $win 'smoke shot'
    Click-Button  $win 'CAPTURE'
    Start-Sleep -Seconds 2
    "  screenshot: captured"

    # ---- assertions on disk ----
    $new = @(Get-ChildItem $vid -Directory -ErrorAction SilentlyContinue | Where-Object { -not $beforeNames.ContainsKey($_.Name) })
    "  recordings produced: $($new.Count)"
    if ($new.Count -ne 3) { throw "expected 3 new recordings, got $($new.Count)" }
    foreach ($d in $new) {
        if (-not (Test-Path (Join-Path $d.FullName 'manifest.json'))) { throw "no manifest.json in $($d.Name)" }
    }
    if (Test-Path $crash) { throw "crash log present: $((Get-Content $crash -Raw).Trim())" }
}
catch { $failure = $_.Exception.Message }
finally {
    Get-Process AgentEyes -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $bakPresets) { Move-Item $bakPresets (Join-Path $appdir 'presets.json') -Force }
    if (Test-Path $bakConfig)  { Move-Item $bakConfig  (Join-Path $appdir 'config.json')  -Force }
}

if ($failure) { "GUI-SMOKE: FAIL ($failure)"; exit 1 }
"GUI-SMOKE: PASS"
exit 0

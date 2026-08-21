# End-to-end demo of the qa-walk-companion plugin (issue #32), driving the REAL app.
#
# It enables the plugin, drives a real GUI audio recording via UI Automation while a
# spoken QA narration plays into the system-loopback (so the captured audio is known
# content), and lets the app's own pipeline transcribe (Whisper) and run the plugin.
# Then it prints the transcript and the extracted bugs, and points at qa-report.html.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\qa-walk-companion-demo.ps1
#
# Requirements: a Release build, an OpenAI provider configured in the app (Settings >
# AI), the plugin installed under %LOCALAPPDATA%\AgentEyes\plugins\qa-walk-companion
# (Settings > Plugins > Get plugins), and a working audio render device for loopback.
#
# This is a MANUAL demo, not part of run-all: it needs an API key, audio hardware, and
# ~2 minutes. It is non-destructive - presets.json and config.json are backed up and
# fully restored, so it does not change your enabled-plugins or preset state.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Speech

$root   = Split-Path $PSScriptRoot -Parent
# Both projects set <Platforms>x64</Platforms>, so `dotnet build -c Release` lands in
# bin\x64\Release\. A stale non-x64 output directory on an old checkout holds a month-old binary -
# launching it silently tests code nobody built (issue #9). x64 path ONLY; missing = FAIL, no fallback.
$exe = Join-Path $root 'src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe'
if (-not (Test-Path $exe)) {
    "DEMO: FAIL (app binary not found - it has not been built)"
    "  expected: $exe"
    "  build it: dotnet build AgentEyes.sln -c Release"
    exit 1
}

$appdir      = Join-Path $env:LOCALAPPDATA 'AgentEyes'
$cfgPath     = Join-Path $appdir 'config.json'
$presetsPath = Join-Path $appdir 'presets.json'
$pluginDir   = Join-Path $appdir 'plugins\qa-walk-companion'
$vid         = Join-Path $env:USERPROFILE 'Videos\AgentEyes'
$narr        = Join-Path $env:TEMP 'qawc-narration.wav'
$tempId      = 'qawcdemo000000000000000000000001'

if (-not (Test-Path $pluginDir)) {
    "DEMO: FAIL (qa-walk-companion not installed - Settings > Plugins > Get plugins)"; exit 1
}

$narration = "Okay, starting the Q A walkthrough of the settings screen. First, I opened the profile page and the avatar image fails to load, it just shows a broken icon. That is a bug. Next, I clicked the save button on the preferences tab and nothing happened, my changes were not saved. Then I tried to export my data and the application froze for about ten seconds before it recovered. The rest of the navigation worked fine. End of walkthrough."

# ---- UIA helpers (same approach as gui-smoke.ps1) ----
$A = [System.Windows.Automation.AutomationElement]
function NameIs($n)  { New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $n) }
function TypeIs($t)  { New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $t) }
function BothOf($a,$b){ New-Object System.Windows.Automation.AndCondition($a,$b) }
function Find-MainWindow {
    for ($i=0; $i -lt 20; $i++) {
        $w = $A::RootElement.FindFirst('Children', (BothOf (NameIs 'AgentEyes') (TypeIs ([System.Windows.Automation.ControlType]::Window))))
        if ($w) { return $w }; Start-Sleep -Milliseconds 500
    }
    throw 'main window not found'
}
function Find-Button($win,$n){ $win.FindFirst('Descendants', (BothOf (NameIs $n) (TypeIs ([System.Windows.Automation.ControlType]::Button)))) }
function Wait-Button($win,$n,$sec){
    $sw=[Diagnostics.Stopwatch]::StartNew()
    while($sw.Elapsed.TotalSeconds -lt $sec){ $b=Find-Button $win $n; if($b -and $b.Current.IsEnabled){return $b}; Start-Sleep -Milliseconds 400 }
    throw "button '$n' not available in ${sec}s"
}
function Click-Button($win,$n,$sec=15){ ((Wait-Button $win $n $sec).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }

$bakP = "$presetsPath.demo-bak"; $bakC = "$cfgPath.demo-bak"; $failure = $null
try {
    Get-Process AgentEyes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700

    # back up, then enable the plugin + add a temp system-audio preset (key preserved)
    Copy-Item $cfgPath $bakC -Force
    if (Test-Path $presetsPath) { Copy-Item $presetsPath $bakP -Force }

    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $enabled = @(); if ($cfg.PSObject.Properties['EnabledPlugins']) { $enabled = @($cfg.EnabledPlugins) }
    if ($enabled -notcontains 'qa-walk-companion') { $enabled += 'qa-walk-companion' }
    $cfg | Add-Member -NotePropertyName EnabledPlugins   -NotePropertyValue $enabled -Force
    $cfg | Add-Member -NotePropertyName LastUsedPresetId -NotePropertyValue $tempId  -Force
    ConvertTo-Json $cfg -Depth 8 | Set-Content $cfgPath -Encoding UTF8

    $tempPreset = [pscustomobject][ordered]@{
        Id=$tempId; Name='qa demo'; Note=$null; MonitorIndex=1; UseRegion=$false; Region=$null
        Source='system'; Mic=$null; Gate=$true; MicVol=100; SysVol=100; Mode='audio'; Fps=30
    }
    $arr = @(); if (Test-Path $presetsPath) { $arr = @(Get-Content $presetsPath -Raw | ConvertFrom-Json) }
    $arr += $tempPreset
    ConvertTo-Json $arr -Depth 8 | Set-Content $presetsPath -Encoding UTF8

    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $synth.SetOutputToWaveFile($narr); $synth.Speak($narration); $synth.Dispose()

    $before = @{}; Get-ChildItem $vid -Directory -ErrorAction SilentlyContinue | ForEach-Object { $before[$_.Name]=$true }

    Start-Process $exe
    $win = Find-MainWindow      # app launches with 'qa demo' (LastUsedPresetId) active
    Click-Button $win 'REC'
    Wait-Button  $win 'STOP' 15 | Out-Null
    "  recording started; playing narration into the loopback..."
    (New-Object System.Media.SoundPlayer $narr).PlaySync()
    Start-Sleep -Milliseconds 500
    Click-Button $win 'STOP'
    Wait-Button  $win 'REC' 30 | Out-Null
    "  stopped; app is transcribing + running the plugin..."

    $new = $null
    for($i=0;$i -lt 10 -and -not $new;$i++){ Start-Sleep -Milliseconds 500
        $new = Get-ChildItem $vid -Directory | Where-Object { -not $before.ContainsKey($_.Name) } | Select-Object -First 1 }
    if(-not $new){ throw 'no new recording directory appeared' }
    $dir = $new.FullName
    $mf = Get-Content (Join-Path $dir 'manifest.json') -Raw | ConvertFrom-Json
    "  recording: $($new.Name) (mode=$($mf.mode) mic=$($mf.microphone) $([math]::Round($mf.durationSeconds,1))s)"

    $report = Join-Path $dir 'qa-report.html'
    $sw=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path $report) -and $sw.Elapsed.TotalSeconds -lt 120){ Start-Sleep -Seconds 2 }
    if(-not (Test-Path $report)){ throw "the plugin did not produce qa-report.html (see plugin-qa-walk-companion.log in $dir)" }

    "--- transcript ---"
    Get-Content (Join-Path $dir 'transcript.json') -Raw | ConvertFrom-Json | ForEach-Object { "  [{0:00}:{1:00}] {2}" -f [int][math]::Floor($_.startSeconds/60), [int]($_.startSeconds%60), $_.text }
    "--- bugs found ---"
    $bugs = (Get-Content (Join-Path $dir 'qa-bugs.json') -Raw | ConvertFrom-Json).bugs
    $bugs | ForEach-Object { "  [{0,-6}] {1} ({2})" -f $_.severity.ToUpper(), $_.title, $_.timestamp }
    ""
    "DEMO: PASS - $($bugs.Count) bug(s) extracted; report: $report"
}
catch { $failure = $_.Exception.Message }
finally {
    Get-Process AgentEyes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    if (Test-Path $bakC) { Move-Item $bakC $cfgPath -Force }
    if (Test-Path $bakP) { Move-Item $bakP $presetsPath -Force }
    elseif (Test-Path $presetsPath) { Remove-Item $presetsPath -Force }
}

if ($failure) { "DEMO: FAIL ($failure)"; exit 1 }
exit 0

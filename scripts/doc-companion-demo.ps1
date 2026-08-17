# End-to-end demo of the doc-companion plugin (issue #32), driving the REAL app.
#
# It enables doc-companion, drives a real GUI VIDEO recording via UI Automation while a
# how-to narration plays into the system loopback (so the captured audio is known and
# the screen gives keyframes), and lets the app's own pipeline transcribe + extract
# frames + run the plugin. Then it prints the generated guide and how many screenshots
# were embedded, and points at docs.html / docs.md.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\doc-companion-demo.ps1
#
# Requirements: a Release build, an OpenAI provider configured in the app (Settings >
# AI), the plugin installed under %LOCALAPPDATA%\AgentEyes\plugins\doc-companion
# (Settings > Plugins > Get plugins), and a working audio render device for loopback.
#
# Video is used so frames are extracted into shots/ and embedded under each step (the
# qa-walk-companion demo uses audio). This is a MANUAL demo, not part of run-all: it
# needs an API key, audio hardware, and ~2 minutes. Non-destructive - presets.json and
# config.json are backed up and fully restored, so it does not change your state.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Speech

$root = Split-Path $PSScriptRoot -Parent
$exe  = @(
    'src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe',
    'src\AgentEyes.App\bin\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe'
) | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) { "DEMO: FAIL (app not built - run: dotnet build AgentEyes.sln -c Release)"; exit 1 }

$appdir      = Join-Path $env:LOCALAPPDATA 'AgentEyes'
$cfgPath     = Join-Path $appdir 'config.json'
$presetsPath = Join-Path $appdir 'presets.json'
$pluginDir   = Join-Path $appdir 'plugins\doc-companion'
$vid         = Join-Path $env:USERPROFILE 'Videos\AgentEyes'
$narr        = Join-Path $env:TEMP 'doc-narration.wav'
$tempId      = 'docdemo0000000000000000000000001'

if (-not (Test-Path $pluginDir)) {
    "DEMO: FAIL (doc-companion not installed - Settings > Plugins > Get plugins)"; exit 1
}

$narration = "Welcome to this quick how-to. First, open the application and find the main toolbar across the top of the window. Next, click the record button on the left side to start a new capture. After that, choose your preset from the dropdown next to it so the right screen and microphone are used. Finally, when you are done, press stop and your recording is saved automatically to the library. That is the whole workflow."

# ---- UIA helpers (same approach as gui-smoke.ps1) ----
$A = [System.Windows.Automation.AutomationElement]
function NameIs($n){New-Object System.Windows.Automation.PropertyCondition($A::NameProperty,$n)}
function TypeIs($t){New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty,$t)}
function BothOf($a,$b){New-Object System.Windows.Automation.AndCondition($a,$b)}
function Find-MainWindow{for($i=0;$i -lt 20;$i++){$w=$A::RootElement.FindFirst('Children',(BothOf (NameIs 'AgentEyes') (TypeIs ([System.Windows.Automation.ControlType]::Window))));if($w){return $w};Start-Sleep -Milliseconds 500};throw 'main window not found'}
function Find-Button($win,$n){$win.FindFirst('Descendants',(BothOf (NameIs $n) (TypeIs ([System.Windows.Automation.ControlType]::Button))))}
function Wait-Button($win,$n,$sec){$sw=[Diagnostics.Stopwatch]::StartNew();while($sw.Elapsed.TotalSeconds -lt $sec){$b=Find-Button $win $n;if($b -and $b.Current.IsEnabled){return $b};Start-Sleep -Milliseconds 400};throw "button '$n' not available in ${sec}s"}
function Click-Button($win,$n,$sec=15){((Wait-Button $win $n $sec).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}

$bakP="$presetsPath.demo-bak"; $bakC="$cfgPath.demo-bak"; $failure=$null
try {
    Get-Process AgentEyes -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700
    Copy-Item $cfgPath $bakC -Force
    if (Test-Path $presetsPath) { Copy-Item $presetsPath $bakP -Force }

    # enable doc-companion + add a temp system-audio VIDEO preset (key preserved)
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $enabled = @(); if ($cfg.PSObject.Properties['EnabledPlugins']) { $enabled = @($cfg.EnabledPlugins) }
    if ($enabled -notcontains 'doc-companion') { $enabled += 'doc-companion' }
    $cfg | Add-Member EnabledPlugins $enabled -Force
    $cfg | Add-Member LastUsedPresetId $tempId -Force
    ConvertTo-Json $cfg -Depth 8 | Set-Content $cfgPath -Encoding UTF8

    $tempPreset = [pscustomobject][ordered]@{
        Id=$tempId; Name='doc demo'; Note=$null; MonitorIndex=1; UseRegion=$false; Region=$null
        Source='system'; Mic=$null; Gate=$true; MicVol=100; SysVol=100; Mode='video'; Fps=15
    }
    $arr=@(); if (Test-Path $presetsPath) { $arr=@(Get-Content $presetsPath -Raw | ConvertFrom-Json) }
    $arr += $tempPreset
    ConvertTo-Json $arr -Depth 8 | Set-Content $presetsPath -Encoding UTF8

    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $synth.SetOutputToWaveFile($narr); $synth.Speak($narration); $synth.Dispose()

    $before=@{}; Get-ChildItem $vid -Directory -ErrorAction SilentlyContinue | ForEach-Object { $before[$_.Name]=$true }

    Start-Process $exe
    $win = Find-MainWindow      # launches with 'doc demo' (LastUsedPresetId) active
    Click-Button $win 'REC'
    Wait-Button  $win 'STOP' 15 | Out-Null
    "  video recording started; playing narration into the loopback..."
    (New-Object System.Media.SoundPlayer $narr).PlaySync()
    Start-Sleep -Milliseconds 500
    Click-Button $win 'STOP'
    Wait-Button  $win 'REC' 40 | Out-Null
    "  stopped; app is muxing + extracting frames + transcribing + running the plugin..."

    $new=$null
    for($i=0;$i -lt 12 -and -not $new;$i++){ Start-Sleep -Milliseconds 500
        $new=Get-ChildItem $vid -Directory | Where-Object { -not $before.ContainsKey($_.Name) } | Select-Object -First 1 }
    if(-not $new){ throw 'no new recording directory appeared' }
    $dir=$new.FullName
    $mf=Get-Content (Join-Path $dir 'manifest.json') -Raw | ConvertFrom-Json
    "  recording: $($new.Name) (mode=$($mf.mode) mic=$($mf.microphone) $([math]::Round($mf.durationSeconds,1))s)"

    $docs = Join-Path $dir 'docs.md'
    $sw=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path $docs) -and $sw.Elapsed.TotalSeconds -lt 180){ Start-Sleep -Seconds 3 }
    if(-not (Test-Path $docs)){ throw "doc-companion did not produce docs.md (see plugin-doc-companion.log in $dir)" }

    $shotCount = @(Get-ChildItem (Join-Path $dir 'shots') -Filter *.png -ErrorAction SilentlyContinue).Count
    $embedded  = @(Select-String -Path $docs -Pattern '!\[').Count
    "  frames extracted: $shotCount; screenshots embedded in the guide: $embedded"
    ""
    Get-Content $docs -Raw
    ""
    "DEMO: PASS - docs: $(Join-Path $dir 'docs.html')"
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

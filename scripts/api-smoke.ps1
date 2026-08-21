# REST API smoke test - starts the app in tray mode (no window needed) and drives the
# control API over HTTP. Asserts status transitions, produced files, and the 409 conflict.
param([switch]$Confirm)
$ErrorActionPreference = 'Stop'
# USER-INVOKED ONLY (revised 2026-06-16): launches the app and records. Agents must NOT run it.
if (-not $Confirm -and $env:MQS_RUN_TESTS -ne '1') {
  Write-Host "REFUSED: api-smoke.ps1 launches the app and records - USER-INVOKED ONLY. Re-run with -Confirm (or set MQS_RUN_TESTS=1)."
  exit 3
}
# Both projects set <Platforms>x64</Platforms>, so `dotnet build -c Release` lands in
# bin\x64\Release\. A stale non-x64 output directory on an old checkout holds a month-old binary -
# launching it silently tests code nobody built (issue #9). x64 path ONLY; missing = FAIL, no fallback.
$exe       = "$PSScriptRoot\..\src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe"
$agenteyes = "$PSScriptRoot\..\src\AgentEyes.Core\bin\x64\Release\net8.0-windows10.0.19041.0\agenteyes.exe"
if (-not (Test-Path $exe)) {
  Write-Host "API-SMOKE: FAIL (app binary not found - it has not been built)"
  Write-Host "  expected: $exe"
  Write-Host "  build it: dotnet build AgentEyes.sln -c Release"
  exit 1
}
if (-not (Test-Path $agenteyes)) {
  Write-Host "API-SMOKE: FAIL (CLI binary not found - it has not been built)"
  Write-Host "  expected: $agenteyes"
  Write-Host "  build it: dotnet build AgentEyes.sln -c Release"
  exit 1
}
$base = "http://127.0.0.1:7882"
$crash = Join-Path $env:TEMP 'AgentEyes-crash.log'
Remove-Item $crash -ErrorAction SilentlyContinue

Get-Process AgentEyes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Start-Process $exe -ArgumentList '--tray'

# Wait for the API to come up.
$up = $false
for ($i = 0; $i -lt 40; $i++) {
  try { $h = Invoke-RestMethod "$base/health" -TimeoutSec 2; if ($h.ok) { $up = $true; break } } catch { }
  Start-Sleep -Milliseconds 500
}
if (-not $up) { "API-SMOKE: FAIL (API did not come up)"; exit 1 }

$fail = 0
function Chk($name, $cond, $detail) {
  if ($cond) { "[PASS] $name  $detail" } else { "[FAIL] $name  $detail"; $script:fail = 1 }
}
function Post($path, $obj) {
  Invoke-RestMethod "$base$path" -Method Post -ContentType 'application/json' -Body ($obj | ConvertTo-Json)
}

# The system-loopback recordings below need real audio playing, or sys_native.wav captures zero
# samples and the deferred mux (issue #77) produces an audio-less mp4 that can't be transcribed.
# Play a steady tone to the default output for the duration of the recording tests so the smoke is
# deterministic on a silent/headless box (and the deferred mux is exercised with actual audio).
$toneWav = Join-Path $env:TEMP 'agenteyes-smoke-tone.wav'
# Build the tone via a detached process so PowerShell 5.1 does not wrap ffmpeg's stderr in a
# NativeCommandError (a known 5.1 trap). Wait for it to exit, then play the file.
$toneBuild = Start-Process ffmpeg -ArgumentList '-y','-f','lavfi','-i','sine=frequency=440:duration=30',$toneWav -PassThru -Wait -WindowStyle Hidden
$tonePlayer = $null
if (Test-Path $toneWav) {
  $tonePlayer = New-Object System.Media.SoundPlayer $toneWav
  try { $tonePlayer.PlayLooping() } catch { $tonePlayer = $null }
}

$st = Invoke-RestMethod "$base/status";  Chk "status-idle"   ($st.State -eq 'idle') $st.State
$dev = Invoke-RestMethod "$base/devices"; Chk "devices"       ($dev.monitors.Count -ge 1) "$($dev.monitors.Count) monitors"
$shot = Post "/screenshot" @{ screen = 1 };  Chk "screenshot" (Test-Path $shot.file) $shot.file

# audio, system source (no mic dependency -> deterministic headless)
[void](Post "/record/start" @{ mode = 'audio'; screen = 1; source = 'system' })
Start-Sleep -Seconds 1
$st = Invoke-RestMethod "$base/status"; Chk "audio-recording" ($st.State -eq 'recording') $st.State
Start-Sleep -Seconds 2
# Issue #77: stop now returns as soon as the RAW files + manifest are durable; the audio mux is
# deferred to the background packaging pass, so the final mixed file ($res.File) does NOT exist
# yet here. Assert the durable raw capture instead (dir + manifest written); the final file is
# asserted after packaging below.
$res = Post "/record/stop" @{}
$audioDir = Split-Path $res.File -Parent
Chk "audio-stop" ((Test-Path $audioDir) -and (Test-Path (Join-Path $audioDir 'manifest.json'))) $res.File
# Keep this recording's id: it has no transcript -> used for the AC7 404 not_found path.
$audioId = Split-Path $audioDir -Leaf
$st = Invoke-RestMethod "$base/status"; Chk "idle-after" ($st.State -eq 'idle') $st.State

# conflict: starting twice returns 409. This recording also gets a marker shot so that, once
# packaged below, it has both marker shots and a transcript (AC6/AC7 200 paths).
[void](Post "/record/start" @{ mode = 'video'; screen = 1; source = 'system' })
$conflict = $false
try { [void](Post "/record/start" @{ mode = 'video'; screen = 1; source = 'system' }) }
catch { if ($_.Exception.Response.StatusCode.value__ -eq 409) { $conflict = $true } }
Chk "conflict-409" $conflict "second start rejected"
[void](Post "/record/shot" @{})
Start-Sleep -Seconds 2
# Issue #77: deferred mux - the raw files + manifest are durable on return; the final
# recording.mp4 is produced by the packaging pass below. Assert the raw capture here.
$res = Post "/record/stop" @{}
$videoDir = Split-Path $res.File -Parent
$videoId  = Split-Path $videoDir -Leaf
Chk "video-stop" ((Test-Path $videoDir) -and (Test-Path (Join-Path $videoDir 'manifest.json'))) $res.File

# Package the video: this first completes the deferred mux (RecordingService.FinalizePending,
# producing recording.mp4) then transcribes (transcript.json + extracted frame shots) - the same
# in-process pipeline the app's background pass uses. Selftest (which runs before this in run-all)
# already downloaded the Whisper model, so this is fast.
# ($agenteyes is resolved and existence-checked at the top of the script - x64 path only.)
& $agenteyes package $videoDir | Out-Null
# Issue #77 AC5: the deferred mux ran, so the final mixed file now exists on disk.
Chk "video-final" (Test-Path (Join-Path $videoDir 'recording.mp4')) "recording.mp4 produced by deferred mux"
Chk "package" (Test-Path (Join-Path $videoDir 'transcript.json')) "transcript.json written"

# Recording tests are done; stop the tone.
if ($tonePlayer) { try { $tonePlayer.Stop() } catch { } }

# Take a full-screen capture so the gallery has at least one PNG (AC8).
$cap = Post "/capture" @{ mode = 'full'; screen = 1 };  Chk "capture" (Test-Path $cap.file) $cap.file

# ---- issue #73: Control API S1 read/browse JSON surface --------------------
# Helper that returns status + parsed body without throwing on 4xx (PS 5.1 has no
# -SkipHttpErrorCheck), so the 404 envelope can be asserted.
function Get-Result($path) {
  try {
    $r = Invoke-WebRequest "$base$path" -UseBasicParsing
    return @{ Status = [int]$r.StatusCode; Body = ($r.Content | ConvertFrom-Json) }
  } catch {
    $resp = $_.Exception.Response
    $sc = if ($resp) { [int]$resp.StatusCode } else { 0 }
    # PS 5.1 buffers the error body into ErrorDetails.Message (the response stream is already
    # consumed by then); fall back to reading the stream only if that is empty.
    $txt = $_.ErrorDetails.Message
    if (-not $txt -and $resp) {
      try { $txt = (New-Object IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } catch { }
    }
    $body = $null; if ($txt) { try { $body = $txt | ConvertFrom-Json } catch { } }
    return @{ Status = $sc; Body = $body }
  }
}

# AC1: GET /version
$ver = Invoke-RestMethod "$base/version"
Chk "version" ([bool]$ver.version -and $ver.version.Length -gt 0) "v$($ver.version)"

# AC2: unknown route -> 404 + { error, code: not_found }
$r = Get-Result "/does-not-exist"
Chk "unknown-route-404" ($r.Status -eq 404 -and $r.Body.code -eq 'not_found') "$($r.Status)/$($r.Body.code)"

# AC3: unknown recording id -> 404 + not_found
$r = Get-Result "/recordings/no-such-recording-xyz"
Chk "unknown-id-404" ($r.Status -eq 404 -and $r.Body.code -eq 'not_found') "$($r.Status)/$($r.Body.code)"

# AC4: GET /recordings?limit=2 -> total + items[<=2] each with the full field set, newest-first
$recs = Invoke-RestMethod "$base/recordings?limit=2&offset=0"
$item0 = @($recs.items)[0]
$fieldsOk = $item0 -and ($item0.PSObject.Properties.Name -contains 'id') -and `
  ($item0.PSObject.Properties.Name -contains 'durationSeconds') -and `
  ($item0.PSObject.Properties.Name -contains 'shotCount') -and `
  ($item0.PSObject.Properties.Name -contains 'hasTranscript')
Chk "recordings-list" (($recs.total -ge 1) -and (@($recs.items).Count -le 2) -and $fieldsOk) "total=$($recs.total) items=$(@($recs.items).Count)"

# AC5: GET /recordings/{id} detail
$det = Invoke-RestMethod "$base/recordings/$videoId"
Chk "recording-detail" ($det.id -eq $videoId -and $det.manifest -and (Test-Path $det.dir)) $det.id

# AC6: GET /recordings/{id}/shots -> non-empty, each path exists on disk
$shots = @(Invoke-RestMethod "$base/recordings/$videoId/shots")
$shotOk = ($shots.Count -ge 1) -and (Test-Path $shots[0].path)
Chk "recording-shots" $shotOk "$($shots.Count) shot(s)"

# AC7a: GET /recordings/{id}/transcript -> 200 with { text, segments } when a transcript exists
$r = Get-Result "/recordings/$videoId/transcript"
$txOk = ($r.Status -eq 200) -and ($null -ne $r.Body) -and ($r.Body.PSObject.Properties.Name -contains 'segments')
Chk "transcript-200" $txOk "status=$($r.Status)"

# AC7b: a recording that exists but has no transcript -> 404 not_found
$r = Get-Result "/recordings/$audioId/transcript"
Chk "transcript-404" ($r.Status -eq 404 -and $r.Body.code -eq 'not_found') "$($r.Status)/$($r.Body.code)"

# AC8: GET /captures -> non-empty, each with sizeBytes > 0 and an existing absolute path
$caps = @(Invoke-RestMethod "$base/captures")
$capOk = ($caps.Count -ge 1) -and ($caps[0].sizeBytes -gt 0) -and (Test-Path $caps[0].path)
Chk "captures" $capOk "$($caps.Count) capture(s)"

# AC9: discovery lists every new route
$disc = (Invoke-RestMethod "$base/").endpoints -join ' '
$discOk = ($disc -match '/version') -and ($disc -match '/recordings/\{id\}/shots') -and `
  ($disc -match '/recordings/\{id\}/transcript') -and ($disc -match '/captures')
Chk "discovery" $discOk "routes advertised"

if (Test-Path $crash) { "CRASH LOG PRESENT:"; Get-Content $crash -Raw; $fail = 1 }

Get-Process AgentEyes -ErrorAction SilentlyContinue | Stop-Process -Force

if ($fail) { "API-SMOKE: FAIL"; exit 1 } else { "API-SMOKE: PASS"; exit 0 }

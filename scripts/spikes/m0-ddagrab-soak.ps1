# M0 spike (issue #65, slice S0 of epic #60) - THROWAWAY research script, not product code.
#
# Proves the 24/7 encoding shape: ddagrab change-only (VFR) screen capture + continuous default-mic
# audio, hardware-encoded, written as independently-playable 60s segments that stay A/V-synced across
# segment boundaries, with tiny output for a static screen.
#
# Audio note: ffmpeg has no native WASAPI *loopback* input on Windows, so this spike captures the
# default MIC via dshow only. System-loopback audio is the app's own WASAPI capture and is an S1
# concern - the make-or-break question here (VFR video vs continuous audio: do they segment + stay in
# sync?) is provable with a single continuous audio track.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\spikes\m0-ddagrab-soak.ps1 [-Seconds 180] [-Monitor 0]
param(
    [int]$Seconds = 180,
    [int]$Monitor = 0
)
# 'Continue' (not 'Stop'): ffmpeg/ffprobe write progress + the device list to stderr, which PS 5.1
# would otherwise turn into terminating NativeCommandError records. Explicit `throw` + $LASTEXITCODE
# checks below are the real guards.
$ErrorActionPreference = 'Continue'
$ff   = Join-Path $env:LOCALAPPDATA 'AgentEyes\app\ffmpeg.exe'
$ffp  = Join-Path $env:LOCALAPPDATA 'AgentEyes\app\ffprobe.exe'
if (-not (Test-Path $ff))  { throw "ffmpeg not found at $ff" }
if (-not (Test-Path $ffp)) { throw "ffprobe not found at $ffp" }

$buf = Join-Path $env:TEMP 'agenteyes-m0-spike'
if (Test-Path $buf) { Remove-Item $buf -Recurse -Force }
New-Item -ItemType Directory -Force $buf | Out-Null
function Say($m) { $m }

# --- find the default mic's dshow name -------------------------------------------------
Say "== discover dshow audio device =="
$devLines = & $ff -hide_banner -list_devices true -f dshow -i dummy 2>&1
$mic = $null
foreach ($l in $devLines) {
    if ($l -match '"(.+)"\s*\(audio\)' -or ($l -match '"(.+)"' -and $l -match '\(audio\)')) { $mic = $Matches[1]; break }
}
if (-not $mic) { throw "no dshow audio (mic) device found; cannot prove A/V sync" }
Say "  mic (dshow): $mic"

# --- probe a working video encoder over ddagrab (no silent no-op; log the choice) -------
Say "== probe hardware encoder (ddagrab -> encoder) =="
# ddagrab yields D3D11 frames; for the spike we hwdownload to system memory then encode, which is the
# most portable path and isolates the make-or-break (VFR + segmenting + sync) from D3D11<->encoder mapping.
$vf = "ddagrab=output_idx=${Monitor}:framerate=10:dup_frames=0,hwdownload,format=bgra,format=nv12"
$probeOrder = @('h264_qsv','hevc_qsv','h264_amf','h264_nvenc','h264_mf','libx264')
$encoder = $null
foreach ($enc in $probeOrder) {
    $test = Join-Path $buf "probe_$enc.mp4"
    $px = if ($enc -eq 'libx264') { @('-pix_fmt','yuv420p') } else { @() }
    & $ff -hide_banner -loglevel error -y -filter_complex $vf -frames:v 12 -fps_mode vfr -c:v $enc @px $test 2>$null
    if ($LASTEXITCODE -eq 0 -and (Test-Path $test) -and (Get-Item $test).Length -gt 0) {
        $encoder = $enc; Remove-Item $test -Force; break
    }
    Remove-Item $test -Force -ErrorAction SilentlyContinue
    Say "  $enc : FAILED (trying next)"
}
if (-not $encoder) { throw "ALL encoders failed: $($probeOrder -join ', '). No silent fallback - the encoding shape needs investigation." }
Say "  chosen encoder: $encoder"

# --- the soak: ddagrab change-only + mic, hardware-encoded, 60s mp4 segments ------------
Say "== capture $Seconds s (segments of 60s) =="
$px = if ($encoder -eq 'libx264') { @('-pix_fmt','yuv420p') } else { @() }
$args = @(
    '-hide_banner','-loglevel','warning','-y',
    '-filter_complex', $vf,
    '-f','dshow','-i', "audio=$mic",
    '-c:v', $encoder
) + $px + @(
    '-fps_mode','vfr',
    '-c:a','aac','-b:a','128k',
    '-f','segment','-segment_time','60','-reset_timestamps','1','-segment_format','mp4',
    '-t', "$Seconds",
    (Join-Path $buf 'seg_%05d.mp4')
)
$t0 = Get-Date
& $ff @args
$elapsed = ((Get-Date) - $t0).TotalSeconds
Say ("  ffmpeg exit: {0}; wall-clock {1:N1}s" -f $LASTEXITCODE, $elapsed)

# --- analyze segments ------------------------------------------------------------------
Say "== analyze =="
$segs = Get-ChildItem $buf -Filter 'seg_*.mp4' | Sort-Object Name
Say ("  segments produced: {0}" -f $segs.Count)
function Probe($path, $stream) {
    $d = & $ffp -v error -select_streams $stream -show_entries stream=duration -of csv=p=0 $path 2>$null
    if (-not $d) { $d = & $ffp -v error -show_entries format=duration -of csv=p=0 $path 2>$null }
    [double]($d | Select-Object -First 1)
}
$rows = @(); $sumDur = 0.0
foreach ($s in $segs) {
    $vd = Probe $s.FullName 'v:0'
    $ad = Probe $s.FullName 'a:0'
    $sync = [math]::Round([math]::Abs($vd - $ad), 3)
    $sumDur += $vd
    $rows += [pscustomobject]@{ Seg=$s.Name; VideoSec=[math]::Round($vd,2); AudioSec=[math]::Round($ad,2); SyncDelta=$sync; KB=[math]::Round($s.Length/1KB) }
}
$rows | Format-Table -AutoSize | Out-String | ForEach-Object { Say $_ }
$continuity = [math]::Round([math]::Abs($sumDur - $elapsed), 2)
$maxSync = ($rows | Measure-Object SyncDelta -Maximum).Maximum
Say ("  continuity: sum(video)={0:N1}s vs wall={1:N1}s  delta={2}s" -f $sumDur, $elapsed, $continuity)
Say ("  max A/V sync delta across segments: {0}s" -f $maxSync)
Say ("  byte sizes (KB): " + (($rows.KB) -join ', '))
Say ""
Say ("RESULT: encoder=$encoder segments=$($segs.Count) continuity=${continuity}s maxSync=${maxSync}s")
Say "buffer: $buf"

# Issue #156 pass 3 - Windows QA: criterion 4 on a REAL capture whose owner is hard-killed.
#
# Usage: powershell -File real-crash.ps1 -Harness <crash.exe> -Tree <repo checkout> -Out <scratch-dir>
#
# 1. The harness (QA's, not the product) loads <Tree>'s agenteyes.dll, redirects AgentEyes' data and
#    recordings roots into <Out> (AppDataPaths.RedirectForThisProcess, the test-host seam - #161 has no
#    supported redirect for a real agenteyes process), and starts a REAL capture through the product's
#    RecordingService.StartVideo: ffmpeg screen capture of a 320x240 corner, 15 fps, with the MIC (this laptop's mic records silence - a
#    hardware fault - but the chain and the mux are the shipped ones).
# 2. After 12 s the owner is ended with TerminateProcess (Stop-Process -Force) - the Windows SIGKILL.
# 3. The orphaned capture ffmpeg keeps writing. A SECOND process (<Tree>'s agenteyes.exe) runs
#    'package <dir> --no-frames' while it writes.
# 4. QA ends the orphan gracefully with CTRL_C on its console (QA's tool, NOT a product path: on
#    Windows the product's own reap kills it at once - #166), then runs 'package' again.
# Every process signalled here was started by this script or by the harness it started. The captured
# video is deleted at the end (it shows a corner of the owner's screen); manifests and logs are kept.
param([Parameter(Mandatory)] [string]$Harness, [Parameter(Mandatory)] [string]$Tree, [Parameter(Mandatory)] [string]$Out)
$ErrorActionPreference = 'Continue'
function Say($m) { Write-Output ("{0} {1}" -f (Get-Date).ToUniversalTime().ToString('HH:mm:ss.fff'), $m) }
function Sz($f) { if (Test-Path $f) { (Get-Item $f).Length } else { 'absent' } }

$bin = Join-Path $Tree 'src\AgentEyes.Core\bin\x64\Release\net8.0-windows10.0.19041.0'
$dll = Join-Path $bin 'agenteyes.dll'; $exe = Join-Path $bin 'agenteyes.exe'
$ver = (Select-String -Path $dll -Pattern '1\.11\.11\+[0-9a-f]{40}' -List).Matches[0].Value
Say "tree: $Tree  embedded: $ver"
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory $Out | Out-Null

$before = @(Get-Process ffmpeg -ErrorAction SilentlyContinue | ForEach-Object Id)
Say ("ffmpeg running before: [{0}]" -f ($before -join ','))
$h = Start-Process -FilePath $Harness -ArgumentList 'start', "`"$dll`"", "`"$Out`"" -PassThru -NoNewWindow -RedirectStandardOutput "$Out\owner.out" -RedirectStandardError "$Out\owner.err"
$dir = $null
for ($i = 0; $i -lt 100 -and -not $dir; $i++) {
    Start-Sleep -Milliseconds 200
    $l = Get-Content "$Out\owner.out" -ErrorAction SilentlyContinue | Where-Object { $_ -like 'capturing into *' }
    if ($l) { $dir = $l.Substring('capturing into '.Length) }
    if ($h.HasExited) { break }
}
Get-Content "$Out\owner.out", "$Out\owner.err" -ErrorAction SilentlyContinue | ForEach-Object { "    owner: $_" }
if (-not $dir) { Say "ABORT: capture did not start"; if (-not $h.HasExited) { Stop-Process -Id $h.Id -Force }; exit 3 }
Start-Sleep -Seconds 12
$m0 = Get-Content -Raw (Join-Path $dir 'manifest.json') | ConvertFrom-Json
$rawName = if ($m0.PendingMux) { $m0.PendingMux.RawVideo } else { $m0.VideoFile }
$raw = Join-Path $dir $rawName
Say ("start manifest: PendingMux={0} capture file={1}" -f ($m0.PendingMux | ConvertTo-Json -Compress), $rawName)
$own = Get-Content -Raw (Join-Path $dir 'capture.owner.json') | ConvertFrom-Json
$writers = @($own.Writers | ForEach-Object { $_.Pid })
Say ("owner record: pid {0}, writers [{1}]; raw.mp4 {2} bytes" -f $own.Pid, ($writers -join ','), (Sz $raw))
foreach ($w in $writers) { $wp = Get-Process -Id $w; Say ("  writer {0} {1} parent-started-by-harness: {2}" -f $w, $wp.ProcessName, (-not ($before -contains $w))) }

Say "HARD KILL of the owner pid $($h.Id) (TerminateProcess)"
Stop-Process -Id $h.Id -Force
$h.WaitForExit()
$s1 = Sz $raw; Start-Sleep -Seconds 3; $s2 = Sz $raw
$alive = @($writers | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
Say ("after the kill: owner exited={0}; orphan writers alive [{1}]; raw.mp4 {2} -> {3} bytes in 3 s" -f $h.HasExited, ($alive -join ','), $s1, $s2)

function Package($tag) {
    $log = Join-Path $Out "package-$tag.log"
    $p = Start-Process -FilePath $exe -ArgumentList 'package', "`"$dir`"", '--no-frames' -NoNewWindow -Wait -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    Say ("SECOND PROCESS package ({0}): exit {1}" -f $tag, $p.ExitCode)
    Get-Content $log, "$log.err" | Select-String -Pattern 'refus|STILL RUNNING|error|failed|moov|stale|CLEARED|Access' | Select-Object -First 4 | ForEach-Object { '    ' + $_.Line.Substring(0, [Math]::Min(320, $_.Line.Length)) }
    $m = Get-Content -Raw (Join-Path $dir 'manifest.json') | ConvertFrom-Json
    $mux = $null; if ($m.PostProcessing) { $mux = $m.PostProcessing.mux }
    Say ("  PendingMux={0} mux.State={1} mux.Attempts={2} AttemptedBy={3} recording.mp4={4} capture.owner.json={5}" -f $(if ($m.PendingMux) { 'pending' } else { 'cleared' }), $mux.State, $mux.Attempts, ($mux.AttemptedBy | ConvertTo-Json -Compress), (Sz (Join-Path $dir 'recording.mp4')), $(if (Test-Path (Join-Path $dir 'capture.owner.json')) { 'PRESENT' } else { 'absent' }))
}
Package 'while-the-orphan-writes'

foreach ($w in $alive) {
    Say "QA ends orphan $w gracefully: CTRL_C on its console"
    $c = Start-Process -FilePath $Harness -ArgumentList 'ctrlc', $w -Wait -PassThru -NoNewWindow
    $gone = $false
    for ($i = 0; $i -lt 75 -and -not $gone; $i++) { Start-Sleep -Milliseconds 200; $gone = -not (Get-Process -Id $w -ErrorAction SilentlyContinue) }
    Say ("  ctrlc exit {0}; writer exited: {1}" -f $c.ExitCode, $gone)
    if (-not $gone) { Say "  writer did not exit on CTRL_C - stopping it (started by this run's harness)"; Stop-Process -Id $w -Force }
}
Say ("raw.mp4 after the writer ended: {0} bytes; probe: {1}" -f (Sz $raw), ((& ffprobe -v error -show_entries format=duration:stream=codec_type -of csv=p=0 $raw 2>&1) -join ' '))
Package 'after-the-writer-ended'
$final = Join-Path $dir 'recording.mp4'
if (Test-Path $final) {
    Say ("recording.mp4 probe: {0}" -f ((& ffprobe -v error -show_entries format=duration:stream=codec_type -of csv=p=0 $final 2>&1) -join ' '))
    & ffmpeg -v error -i $final -f null - 2>&1 | Select-Object -First 3; Say "full decode exit $LASTEXITCODE"
}
Say "capture-related log lines (redirected data root):"
Get-ChildItem "$Out\LocalAppData\AgentEyes\logs" -Filter *.log | Get-Content | Select-String -Pattern 'CaptureLock|NoteWriter|IsSameProcess' | Select-Object -First 8 | ForEach-Object { '    ' + $_.Line.Substring(0, [Math]::Min(260, $_.Line.Length)) }
$after = @(Get-Process ffmpeg -ErrorAction SilentlyContinue | ForEach-Object Id)
Say ("ffmpeg running after: [{0}]" -f ($after -join ','))
Get-ChildItem $dir -Include *.mp4, *.mkv, *.png, *.jpg -Recurse | Remove-Item -Force
Say "captured media deleted from $dir (manifest and logs kept)"

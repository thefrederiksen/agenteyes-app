# Issue #156 pass 3 - Windows QA. The gate's defect on the REAL CLI binary, plus the elevated case.
#
# Usage:  powershell -File foreign-pid.ps1 -Bin <agenteyes.exe> -Out <scratch-dir> [-ElevatedPid <pid>]
#
# Builds, under <scratch-dir> only, what a crash leaves: a START manifest with a pending mux, a complete
# raw.mp4 (ffmpeg lavfi source, 4 s), a FREE capture.lock, and a same-host capture.owner.json whose
# owner pid does not exist and whose one writer is:
#   A  pid 4 (SYSTEM) - another user's process, which a non-admin token cannot open
#   B  -ElevatedPid with its REAL start time - a still-running, ELEVATED (High IL) process of THIS user,
#      standing in for an orphaned capture ffmpeg started by an elevated AgentEyes. MUST be declined.
#   C  a pid that does not exist - the control that proves the run can recover at all
# Then a second process runs 'agenteyes package <dir> --no-frames' on each.
#
# It starts one ffmpeg per recording (its own, to write raw.mp4) and signals nothing else. The CLI's
# own log lines land in the real %LOCALAPPDATA%\AgentEyes\logs (#161: no supported redirect for a real
# agenteyes process). No recording outside <scratch-dir> is read or written.
param([Parameter(Mandatory)] [string]$Bin, [Parameter(Mandatory)] [string]$Out, [int]$ElevatedPid = 0)
$ErrorActionPreference = 'Stop'
function Say($m) { Write-Output ("{0} {1}" -f (Get-Date).ToUniversalTime().ToString('HH:mm:ss'), $m) }

New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$ver = (Select-String -Path ([IO.Path]::ChangeExtension($Bin, '.dll')) -Pattern '1\.11\.11\+[0-9a-f]{40}' -List).Matches[0].Value
Say "binary: $Bin  embedded: $ver"
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
Say ("this process: user {0}, admin token {1}" -f $me.Name, (New-Object Security.Principal.WindowsPrincipal($me)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))

$dead = 99996
while (Get-Process -Id $dead -ErrorAction SilentlyContinue) { $dead -= 4 }

$cases = @(@{ Name = 'A_writer-pid4-system'; Pid = 4; Start = '2026-11-15T12:00:01Z' })
if ($ElevatedPid -gt 0) {
    $e = Get-Process -Id $ElevatedPid
    $cases += @{ Name = 'B_writer-elevated-live'; Pid = $ElevatedPid; Start = $e.StartTime.ToUniversalTime().ToString('o') }
    Say ("elevated stand-in: pid {0} {1}, started {2}" -f $ElevatedPid, $e.ProcessName, $e.StartTime.ToUniversalTime().ToString('o'))
}
$cases += @{ Name = 'C_writer-gone-control'; Pid = $dead; Start = '2026-11-15T12:00:01Z' }

foreach ($c in $cases) {
    $rec = Join-Path $Out ("2026-11-15_120000_" + $c.Name)
    if (Test-Path $rec) { Remove-Item -Recurse -Force $rec }
    New-Item -ItemType Directory $rec | Out-Null
    & ffmpeg -hide_banner -nostats -loglevel error -f lavfi -i testsrc2=size=1280x720:rate=30 -f lavfi -i sine=frequency=440:sample_rate=48000 -t 4 `
        -c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 23 -g 30 -c:a aac -b:a 128k -y (Join-Path $rec 'raw.mp4')
    Set-Content -Encoding ascii (Join-Path $rec 'manifest.json') '{ "Tool": "AgentEyes", "Mode": "video", "Label": "video", "CreatedUtc": "2026-11-15T12:00:00.0000000Z", "MonitorIndex": 1, "MonitorName": "stand-in", "PendingMux": { "Mode": "video", "Source": "mic", "RawVideo": "raw.mp4", "FinalFile": "recording.mp4", "RawDurationSeconds": 0 } }'
    Set-Content -Encoding ascii (Join-Path $rec 'capture.lock') ''
    Set-Content -Encoding ascii (Join-Path $rec 'capture.owner.json') ('{ "Host": "' + $env:COMPUTERNAME + '", "Pid": ' + $dead + ', "ProcessStartUtc": "2026-11-15T12:00:00Z", "What": "capture session", "StartedUtc": "2026-11-15T12:00:00Z", "Writers": [ { "Label": "video", "Pid": ' + $c.Pid + ', "StartUtc": "' + $c.Start + '" } ] }')
    Say ("--- {0}: lock free, owner pid {1} (gone), writer pid {2} start {3}" -f $c.Name, $dead, $c.Pid, $c.Start)
    $log = Join-Path $Out ($c.Name + '.package.log')
    $p = Start-Process -FilePath $Bin -ArgumentList 'package', "`"$rec`"", '--no-frames' -NoNewWindow -Wait -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    Say ("  package exit {0}" -f $p.ExitCode)
    Get-Content $log, "$log.err" | Select-String -Pattern 'Win32Exception|Access is denied|refus|declin|STILL RUNNING|error|failed|stale|CLEARED|recording.mp4' | Select-Object -First 5 | ForEach-Object { '    ' + $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
    $m = Get-Content -Raw (Join-Path $rec 'manifest.json') | ConvertFrom-Json
    $mux = $null; if ($m.PostProcessing) { $mux = $m.PostProcessing.mux }
    Say ("  PendingMux={0}  mux.State={1}  mux.Attempts={2}  DurationSeconds={3}" -f $(if ($m.PendingMux) { 'pending' } else { 'cleared' }), $mux.State, $mux.Attempts, $m.DurationSeconds)
    $final = Join-Path $rec 'recording.mp4'
    Say ("  recording.mp4: {0}   capture.owner.json: {1}" -f $(if (Test-Path $final) { (Get-Item $final).Length } else { 'absent' }), $(if (Test-Path (Join-Path $rec 'capture.owner.json')) { 'PRESENT' } else { 'absent' }))
    if (Test-Path $final) {
        $probe = & ffprobe -v error -show_entries format=duration:stream=codec_type -of csv=p=0 $final
        Say ("  ffprobe: {0}" -f ($probe -join ' '))
        & ffmpeg -v error -i $final -f null - ; Say ("  full decode exit {0}" -f $LASTEXITCODE)
    }
}

# Issue #161 Windows redirected start time. Same shape as linux-pass5/measure.sh: a redirected start of the
# real agenteyes.exe with no arguments (prints help, exit 1), wall clock from the parent, fresh scratch per run.
# 7882 is held on this machine by the owner's running service (http.sys queue of AgentEyesApp.exe), which this
# seat must not touch; so the probe is pointed at a port of the seat's own with AGENTEYES_SERVICE_PROBE_PORT.
param([string]$Exe, [string]$Probe, [string]$Scratch, [int]$FreePort, [int]$StandinPort, [int]$N = 10)
function Up($p) { [bool](Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue) }
function Time-Starts($label, $port) {
  $times = @()
  for ($i = 1; $i -le $N; $i++) {
    $r = Join-Path $Scratch "$label-$i"; New-Item -ItemType Directory -Force "$r\d","$r\v" | Out-Null
    $env:AGENTEYES_REDIRECT_DATA = "$r\d"; $env:AGENTEYES_REDIRECT_VIDEOS = "$r\v"; $env:AGENTEYES_SERVICE_PROBE_PORT = "$port"
    $sw = [Diagnostics.Stopwatch]::StartNew(); $o = & $Exe 2>&1 | ForEach-Object { "$_" }; $rc = $LASTEXITCODE; $sw.Stop()
    Remove-Item Env:AGENTEYES_REDIRECT_DATA, Env:AGENTEYES_REDIRECT_VIDEOS, Env:AGENTEYES_SERVICE_PROBE_PORT
    $ms = [int]$sw.Elapsed.TotalMilliseconds; $times += $ms
    $note = ([regex]::Match(($o -join ' '), "the (AgentEyes )?service (answering )?on 127\.0\.0\.1:\d+[^;]*")).Value
    if ($note.Length -gt 170) { $note = $note.Substring(0,170) + '...' }
    "$label run ${i}: exit $rc, $ms ms | $note"
  }
  $s = $times | Sort-Object
  "$label MEAN: $([int]($times | Measure-Object -Average).Average) ms  MEDIAN: $([int](($s[4]+$s[5])/2)) ms  MIN: $($s[0])  MAX: $($s[-1])  over $N runs"
}
"# $(Get-Date -Format o)  machine: Windows 11 Home 10.0.26200, 4 cores / 8 threads; SDK 10.0.301 building net8.0, runtime 8.0.21"
"# 7882: $(if (Up 7882) {'LISTENING (owner service - not asked)'} else {'free'})   port $FreePort before: $(if (Up $FreePort) {'LISTENING'} else {'free'})"
"# warm-up run (not counted):"; Time-Starts warmup $FreePort | Select-Object -Last 1 | Out-Null
Time-Starts no-service $FreePort
$p = Start-Process -FilePath $Probe -ArgumentList "standin $StandinPort" -PassThru -WindowStyle Hidden
for ($i = 0; $i -lt 100 -and -not (Up $StandinPort); $i++) { Start-Sleep -Milliseconds 100 }
"# stand-in (HttpListener / http.sys, like the real service) pid $($p.Id) on ${StandinPort}: $(if (Up $StandinPort) {'LISTENING'} else {'NOT UP'})"
Time-Starts service $StandinPort
Stop-Process -Id $p.Id; $p.WaitForExit()
"# stand-in stopped; ${StandinPort}: $(if (Up $StandinPort) {'LISTENING'} else {'free'})"

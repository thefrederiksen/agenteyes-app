param([string]$Label, [string]$Worktree, [int]$OptIn)
$ErrorActionPreference = 'Stop'
$S = Split-Path -Parent $MyInvocation.MyCommand.Path
$real = 'C:\Users\<user>\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin'
$log = Join-Path $S "log-adv-$Label"; Remove-Item -Recurse -Force $log -ErrorAction SilentlyContinue; New-Item -ItemType Directory $log | Out-Null
$out = Join-Path $S "adv-$Label.txt"
function Say($m) { $l = "{0:O}  {1}" -f (Get-Date), $m; Write-Output $l; Add-Content -Path $out -Value $l }
Set-Content -Path $out -Value "# adversarial ownership run: $Label  worktree=$Worktree  optin=$OptIn"
$st = (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:7882/status -TimeoutSec 3).Content | ConvertFrom-Json
Say "laptop app /status before: State=$($st.State)"
$pre = @(Get-Process ffmpeg -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Id)@$($_.StartTime.ToString('O'))" })
Say "ffmpeg processes before: $($pre.Count) [$($pre -join ', ')]"
$env:SHIM_REAL_DIR = $real; $env:SHIM_LOG_DIR = $log; $env:QA_RECORD_FFMPEG_DIR = Join-Path $S 'shim'
$env:PATH = (Join-Path $S 'shim') + ';' + $env:PATH
if ($OptIn -eq 1) { $env:AGENTEYES_FFMPEG_TESTS = '1' } else { Remove-Item Env:AGENTEYES_FFMPEG_TESTS -ErrorAction SilentlyContinue }
$testOut = Join-Path $S "adv-$Label-dotnet.txt"
$t = Start-Process -FilePath dotnet -ArgumentList @('test', "$Worktree\tests\AgentEyes.Tests\AgentEyes.Tests.csproj", '-c', 'Release', '-p:Platform=x64', '--no-build', '--filter', 'FullyQualifiedName~MicChainStallTests.RunWatched_FilterThatStallsOnNan_FailsWithinTheBound', '--logger', 'console;verbosity=detailed') -RedirectStandardOutput $testOut -RedirectStandardError "$testOut.err" -PassThru -NoNewWindow
$null = $t.Handle
Say "dotnet test started pid $($t.Id)"
# wait for the test's watched (stalling) run to start - a shim line with -progress
$deadline = (Get-Date).AddSeconds(120); $stallLine = $null
while (-not $stallLine -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250; $stallLine = Get-ChildItem $log -Filter 'ffmpeg-*' | Get-Content | Where-Object { $_ -match '-progress' } | Select-Object -First 1 }
if (-not $stallLine) { Say "BROKEN: the test's stalling ffmpeg never appeared"; exit 2 }
Say "test's stalling ffmpeg seen by shim: $($stallLine.Substring(0, [Math]::Min(160, $stallLine.Length)))"
Start-Sleep -Seconds 2
# FOREIGN: a plain real ffmpeg the suite did not start, started INSIDE the old sweep's window
$psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $real 'ffmpeg.exe')
$psi.Arguments = '-nostdin -hide_banner -loglevel error -re -f lavfi -i sine=f=550:d=600 -f null -'
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$foreign = [Diagnostics.Process]::Start($psi)
$fStart = $foreign.StartTime
Say "FOREIGN ffmpeg started: pid $($foreign.Id) start $($fStart.ToString('O')) (after the test's own ffmpeg, so inside the window)"
$t.WaitForExit(240000) | Out-Null
if (-not $t.HasExited) { Say "BROKEN: dotnet test still running after 240 s"; }
Say "dotnet test exited: code $($t.ExitCode)"
Get-Content $testOut | Select-String -Pattern 'Passed!|Failed!|failure path|killed leaked|could not kill|\[FAIL\]|Total tests' | ForEach-Object { Say ("  test: " + $_.Line.Trim()) }
$alive = $false
try { $p = Get-Process -Id $foreign.Id -ErrorAction Stop; $alive = ($p.StartTime -eq $fStart) -and ($p.ProcessName -eq 'ffmpeg') } catch { $alive = $false }
Say ("FOREIGN pid {0}: {1}" -f $foreign.Id, $(if ($alive) { 'ALIVE after the failure path' } else { 'DEAD - killed during the run' }))
if ($foreign.HasExited) { Say "FOREIGN exit code: $($foreign.ExitCode)" }
if (-not $foreign.HasExited) { $foreign.Kill(); $foreign.WaitForExit(5000) | Out-Null; Say "QA cleanup: killed FOREIGN pid $($foreign.Id) by its own handle (QA started it)" }
$post = @(Get-Process ffmpeg -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Id)@$($_.StartTime.ToString('O'))" })
Say "ffmpeg processes after: $($post.Count) [$($post -join ', ')]"
$st = (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:7882/status -TimeoutSec 3).Content | ConvertFrom-Json
Say "laptop app /status after: State=$($st.State)"
Say "RESULT $Label foreignAlive=$alive testExit=$($t.ExitCode)"

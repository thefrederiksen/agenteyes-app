param([string]$Label)
# Snapshot: AgentEyes processes, /health, /status State, 7882 listener, and which AgentEyes pid
# last logged "REST API listening" in the real app log (HTTP.sys shows pid 4, so the log line is the attribution).
"=== snapshot: $Label at $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'))"
$ps = Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'AgentEyesApp.exe','ffmpeg.exe' }
if (-not $ps) { "processes: (no AgentEyesApp / ffmpeg)" }
foreach ($p in $ps) { "pid=$($p.ProcessId) name=$($p.Name) started=$($p.CreationDate.ToString('HH:mm:ss')) exe=$($p.ExecutablePath) cmd=$($p.CommandLine)" }
try { "health: " + ((Invoke-WebRequest http://127.0.0.1:7882/health -UseBasicParsing -TimeoutSec 4).Content -replace '\s+',' ') } catch { "health: NO ANSWER ($($_.Exception.Message))" }
try { "status.State: " + ((Invoke-WebRequest http://127.0.0.1:7882/status -UseBasicParsing -TimeoutSec 4).Content | ConvertFrom-Json).State } catch { "status: NO ANSWER" }
$l = Get-NetTCPConnection -LocalPort 7882 -State Listen -ErrorAction SilentlyContinue
"7882 listener: " + $(if ($l) { "yes (HTTP.sys pid $($l.OwningProcess -join ','))" } else { "NOTHING listening" })
$log = Get-ChildItem "$env:LOCALAPPDATA\AgentEyes\logs" -Filter 'AgentEyes-*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$last = Select-String -Path $log.FullName -Pattern 'REST API listening' | Select-Object -Last 1
"last 'REST API listening' log line: " + $(if ($last) { $last.Line } else { '(none)' })

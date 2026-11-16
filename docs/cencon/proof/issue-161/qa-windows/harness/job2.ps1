# Issue #161 Windows derivation question. Run from a clean environment; it spoofs only child processes.
param([string]$Dll, [string]$Exe, [string]$Probe, [string]$Scratch, [int]$FreePort)
$realLad = [Environment]::GetFolderPath('LocalApplicationData'); $realVid = [Environment]::GetFolderPath('MyVideos')
$snap = { param($p) Get-ChildItem -LiteralPath $p -Recurse -Depth 1 -Force -ErrorAction SilentlyContinue |
          ForEach-Object { "$($_.FullName.Substring($p.Length))|$($_.CreationTimeUtc.ToString('o'))" } | Sort-Object }
$beforeState = & $snap (Join-Path $realLad 'AgentEyes'); $beforeRec = & $snap (Join-Path $realVid 'AgentEyes')
"# snapshot before: real state root entries=$($beforeState.Count), real recordings root entries=$($beforeRec.Count) (names+creation times, depth 2)"
"# real folders, read in THIS (unspoofed) shell: LocalApplicationData=$realLad  MyVideos=$realVid"
"# port $FreePort listening before: $([bool](Get-NetTCPConnection -LocalPort $FreePort -State Listen -ErrorAction SilentlyContinue))"
Remove-Item -Recurse -Force $Scratch -ErrorAction SilentlyContinue
$fake = Join-Path $Scratch 'fakehome'
New-Item -ItemType Directory -Force "$fake\AppData\Local","$fake\AppData\Roaming","$fake\Videos","$Scratch\d","$Scratch\v" | Out-Null
$spoof = @{ USERPROFILE = $fake; LOCALAPPDATA = "$fake\AppData\Local"; APPDATA = "$fake\AppData\Roaming";
            HOMEDRIVE = $fake.Substring(0,2); HOMEPATH = $fake.Substring(2) }
function Run-Spoofed([scriptblock]$b, [hashtable]$extra) {
  $saved = @{}; $all = $spoof + $extra
  foreach ($k in $all.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $all[$k]) }
  try { & $b } finally { foreach ($k in $all.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
}
"`n######## PART A - the derivation, in a SPOOFED process (USERPROFILE/LOCALAPPDATA/APPDATA/HOMEDRIVE/HOMEPATH -> scratch)"
Run-Spoofed { & $Probe derive $Dll $realLad $realVid $Scratch 2>&1 } @{}
"`n######## PART B - the REAL agenteyes.exe from the PR head, spoofed, probe pointed at free port $FreePort (owner's service on 7882 NOT asked)"
$cases = @(
  @{ L = 'B1 ATTACK: DATA = real local app data, VIDEOS = scratch'; D = $realLad;    V = "$Scratch\v" },
  @{ L = 'B2 ATTACK: DATA = scratch, VIDEOS = real Videos';         D = "$Scratch\d"; V = $realVid },
  @{ L = 'B3 CONTROL: both scratch (must be ACCEPTED, so the refusals above are not caused by the spoof itself)'; D = "$Scratch\d"; V = "$Scratch\v" })
foreach ($c in $cases) {
  "--- $($c.L)"
  $out = Run-Spoofed { & $Exe 2>&1 | ForEach-Object { "$_" }; "exit=$LASTEXITCODE" } @{ AGENTEYES_REDIRECT_DATA = $c.D; AGENTEYES_REDIRECT_VIDEOS = $c.V; AGENTEYES_SERVICE_PROBE_PORT = "$FreePort" }
  $out | Where-Object { $_ -match 'error|redirect|exit=' } | ForEach-Object { "  $_" }
}
"`n# scratch after: $((Get-ChildItem -Recurse -Force $Scratch | ForEach-Object { $_.FullName.Substring($Scratch.Length) }) -join ', ')"
$afterState = & $snap (Join-Path $realLad 'AgentEyes'); $afterRec = & $snap (Join-Path $realVid 'AgentEyes')
"# snapshot after: real state root entries=$($afterState.Count) new=[$((Compare-Object $beforeState $afterState | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject) -join '; ')]"
"#                 real recordings root entries=$($afterRec.Count) new=[$((Compare-Object $beforeRec $afterRec | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject) -join '; ')]"
"# marker present in real roots: state=$(Test-Path (Join-Path $realLad 'AgentEyes\.agenteyes-scratch-library')) recordings=$(Test-Path (Join-Path $realVid 'AgentEyes\.agenteyes-scratch-library'))"

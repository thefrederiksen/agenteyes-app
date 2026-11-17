param([string]$Label)
# Fingerprint of the six things the finalizer writes on the REAL machine, plus the real app.
# Shortcut locations come from the same shell-folder lookups the product uses (GetFolderPath), so a
# redirected Desktop is read where the product would write. Each shortcut is listed with its target,
# its LastWriteTime to the millisecond and a SHA-256 of the .lnk bytes: a delete, a re-create or a
# repoint all change the line. Home paths are written as %USERPROFILE% so the listing can be committed.
function Scrub([string]$s) { if ($null -eq $s) { return '' }; return $s.Replace($env:USERPROFILE, '%USERPROFILE%') }
"=== fingerprint: $Label at $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'))"
$sh = New-Object -ComObject WScript.Shell
$lnks = @(
  (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\AgentEyes.lnk'),
  (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'AgentEyes.lnk'))
foreach ($l in $lnks) {
  if (Test-Path -LiteralPath $l) {
    $i = Get-Item -LiteralPath $l
    $h = (Get-FileHash -LiteralPath $l -Algorithm SHA256).Hash.Substring(0, 16)
    "1-2 LNK $(Scrub $l) target=$(Scrub $sh.CreateShortcut($l).TargetPath) mtime=$($i.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss.fff')) sha256=$h"
  } else { "1-2 LNK $(Scrub $l) MISSING" }
}
$u = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentEyes' -ErrorAction SilentlyContinue
if ($u) { "3 ARP ver=$($u.DisplayVersion) loc=$(Scrub $u.InstallLocation) uninstall=$(Scrub $u.UninstallString) icon=$(Scrub $u.DisplayIcon)" } else { "3 ARP MISSING" }
$run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).AgentEyes
"4 RUN " + $(if ($run) { Scrub $run } else { 'MISSING' })
$e = Get-ItemProperty 'HKCU:\Environment'
"5 PATH-AgentEyes " + (Scrub ((($e.Path -split ';') | Where-Object { $_ -match 'AgentEyes' }) -join ' ; '))
"6 DOTNET_BUNDLE_EXTRACT_BASE_DIR " + (Scrub $e.DOTNET_BUNDLE_EXTRACT_BASE_DIR)
Get-CimInstance Win32_Process -Filter "Name='AgentEyesApp.exe'" | ForEach-Object { "APP pid=$($_.ProcessId) started=$($_.CreationDate.ToString('HH:mm:ss')) exe=$(Scrub $_.ExecutablePath)" }
try { "health: " + ((Invoke-WebRequest http://127.0.0.1:7882/health -UseBasicParsing -TimeoutSec 4).Content -replace '\s+', ' ') } catch { "health: NO ANSWER" }

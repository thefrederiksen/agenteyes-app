# Issue #200 - "a --root install must touch nothing outside its root", measured under a KERNEL deny.
#
# The Windows counterpart of the Landlock / sandbox-exec deny #161's seats used: Mandatory Integrity Control.
# A process at LOW integrity is refused by the kernel when it writes to any object at Medium - every file and
# folder of the person's profile (the real %LOCALAPPDATA%\AgentEyes, %TEMP%, the Start Menu), HKCU (PATH, the
# Run key, the uninstall entry) and the person's own processes and named events (so it cannot stop the real
# AgentEyes or ask it to quit). Only the scratch root is labelled Low, so it is the one place such a process
# can write. A setup CLI that stays inside its root therefore runs to completion; one that reaches outside
# is refused by the kernel at the first touch, which the command's output and its exit code then show.
#
# Order: (1) the DENIAL CONTROL, proven first: a low-integrity probe must be denied P1-P7 and allowed P8;
#        (2) the binary under test, run at low integrity with --root <scratch root>;
#        (3) the real app's pid, /health and the real finalizer state, before and after.
# Usage:  harness.ps1 -SetupExe <path to agenteyes-setup.exe> -ReleaseDir <local release> -Work <scratch dir> -Label <name>
# The setup exe's whole folder is copied (a framework-dependent build needs its dlls). ASCII only.
param(
    [Parameter(Mandatory = $true)][string]$SetupExe,
    [Parameter(Mandatory = $true)][string]$ReleaseDir,
    [Parameter(Mandatory = $true)][string]$Work,
    [Parameter(Mandatory = $true)][string]$Label
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-RealState {
    $app = Get-CimInstance Win32_Process -Filter "Name='AgentEyesApp.exe'" | Where-Object { $_.ExecutablePath -like "$env:LOCALAPPDATA\AgentEyes\app\*" }
    $health = try { ((Invoke-WebRequest http://127.0.0.1:7882/health -UseBasicParsing -TimeoutSec 3).Content -replace '\s', '') } catch { "NO ANSWER ($($_.Exception.Message))" }
    $lnkPath = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\AgentEyes.lnk'
    $lnk = if (Test-Path $lnkPath) { (Get-Item $lnkPath).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss.fff') } else { 'absent' }
    $arp = Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentEyes -ErrorAction SilentlyContinue
    $run = (Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -ErrorAction SilentlyContinue).AgentEyes
    [pscustomobject]@{
        Pids        = (@($app | ForEach-Object { "$($_.ProcessId) started $($_.CreationDate.ToString('HH:mm:ss'))" }) -join ', ')
        Health      = $health
        StartMenu   = $lnk
        Uninstall   = if ($arp) { "$($arp.DisplayVersion) at $($arp.InstallLocation)" } else { 'absent' }
        RunKey      = $run
        UserPath    = (([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') | Where-Object { $_ -match 'AgentEyes' }) -join ';'
        BundleVar   = [Environment]::GetEnvironmentVariable('DOTNET_BUNDLE_EXTRACT_BASE_DIR', 'User')
    }
}

function Write-Scrubbed([string]$Text) {
    $Text -replace [regex]::Escape($env:USERPROFILE), '%USERPROFILE%' -replace [regex]::Escape(($env:USERPROFILE -replace '\\', '/')), '%USERPROFILE%'
}

New-Item -ItemType Directory -Force $Work | Out-Null
$bin = Join-Path $Work "bin-$Label"
$scratch = Join-Path $Work "lowroot-$Label"
if (Test-Path $scratch) { throw "scratch root $scratch already exists - use a new -Label" }
New-Item -ItemType Directory -Force $bin, $scratch | Out-Null

# Low-labelled copies: the probe's powershell, and the setup CLI's exe. The scratch root is labelled Low with
# inheritance, so the CLI can create root\app, root\logs, root\staging... under it and nowhere else.
Copy-Item "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" (Join-Path $bin 'lowps.exe') -Force
Copy-Item (Join-Path (Split-Path -Parent $SetupExe) '*') $bin -Recurse -Force
$exe = Join-Path $bin (Split-Path -Leaf $SetupExe)
foreach ($f in @((Join-Path $bin 'lowps.exe'), $exe)) { icacls $f /setintegritylevel low | Out-Null }
icacls $scratch /setintegritylevel '(OI)(CI)low' | Out-Null

$real = Get-CimInstance Win32_Process -Filter "Name='AgentEyesApp.exe'" | Where-Object { $_.ExecutablePath -like "$env:LOCALAPPDATA\AgentEyes\app\*" } | Select-Object -First 1
if (-not $real) { throw 'the real AgentEyes is not running - this harness measures that it is left alone, so start it first' }

"=== #200 low-integrity harness, run '$Label' at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
"binary: $(Write-Scrubbed $SetupExe)  version: $((Get-Item $exe).VersionInfo.ProductVersion)"
"--- (1) DENIAL CONTROL (low-integrity probe; real app pid $($real.ProcessId))"
$control = & (Join-Path $bin 'lowps.exe') -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $here 'probe.ps1') -ScratchRoot $scratch -RealAppPid $real.ProcessId 2>&1 | ForEach-Object { "$_" }
$control | ForEach-Object { Write-Scrubbed $_ }
$ok = ($control -match '^P0 .*Low \(S-1-16-4096\)').Count -eq 1 `
      -and ($control -match '^P[1-7] .*: DENIED').Count -eq 7 `
      -and ($control -match '^P8 .*: ALLOWED').Count -eq 1
if (-not $ok) { "CONTROL FAILED - the instrument does not deny what it must; nothing below would mean anything. Stopping."; exit 3 }
"CONTROL PROVEN: low integrity; P1-P7 denied by the kernel; P8 (the scratch root) allowed."

"--- (3a) real install BEFORE"
Get-RealState | Format-List | Out-String | ForEach-Object { Write-Scrubbed $_.Trim() }

$root = Join-Path $scratch 'root'
"--- (2) agenteyes-setup install --release-dir <release> --root <scratch>\root --no-finalize   (at LOW integrity)"
$out = Join-Path $Work "stdout-$Label.txt"; $err = Join-Path $Work "stderr-$Label.txt"
$p = Start-Process -FilePath $exe -ArgumentList @('install', '--release-dir', "`"$ReleaseDir`"", '--root', "`"$root`"", '--no-finalize') `
        -RedirectStandardOutput $out -RedirectStandardError $err -NoNewWindow -Wait -PassThru
"exit=$($p.ExitCode)"
Get-Content $out, $err | ForEach-Object { Write-Scrubbed $_ }
"--- the CLI's own log (inside the scratch root), the lines that name a process, a path outside, or a failure"
$log = Join-Path $root 'logs\setup-cli.log'
if (Test-Path $log) { Get-Content $log | Where-Object { $_ -match 'RunningApp|IsolatedRootAppHandle|UpdateRestartCycle|Relaunch|DownloadAssetAsync|ArchiveInstaller|FAIL|denied|REFUSED' } | ForEach-Object { Write-Scrubbed $_ } } else { "(no log at $(Write-Scrubbed $log))" }
"--- files the CLI placed in the scratch root"
if (Test-Path $root) { Get-ChildItem $root -Recurse -File | ForEach-Object { "{0,12} {1}" -f $_.Length, $_.FullName.Substring($root.Length) } }

Start-Sleep -Seconds 3
"--- (3b) real install AFTER"
Get-RealState | Format-List | Out-String | ForEach-Object { Write-Scrubbed $_.Trim() }

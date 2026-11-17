# Issue #200 - the DENIAL CONTROL for the low-integrity harness. Runs AT LOW INTEGRITY (started from a
# low-labelled copy of powershell.exe by harness.ps1) and tries each kind of outside-the-root touch the setup
# CLI could make. Every probe on a real path must be DENIED by the kernel; the probe on the scratch root
# must be ALLOWED. A probe that is not denied is reported as a CONTROL FAILURE - and is undone at once
# (a written file or value is deleted; the real app's handle and quit event are opened, never used).
#
# ASCII only. Prints one line per probe: "<name>: DENIED|ALLOWED - <detail>".
param(
    [Parameter(Mandatory = $true)][string]$ScratchRoot,
    [Parameter(Mandatory = $true)][int]$RealAppPid
)
$ErrorActionPreference = 'Stop'

function Write-Probe([string]$Name, [scriptblock]$Try, [scriptblock]$Undo) {
    try {
        & $Try
        "{0}: ALLOWED" -f $Name
        if ($Undo) { try { & $Undo } catch { "{0}: UNDO FAILED - {1}" -f $Name, $_.Exception.Message } }
    }
    catch {
        "{0}: DENIED - {1}" -f $Name, ($_.Exception.Message -replace "`r?`n", ' ')
    }
}

$label = (whoami /groups | Select-String 'Mandatory Label\\').Line
$level = if ($label -match 'S-1-16-4096') { 'Low (S-1-16-4096)' } elseif ($label -match 'S-1-16-8192') { 'Medium (S-1-16-8192)' } else { 'unknown' }
"P0 this probe runs at integrity: $level"

$tag = 'agenteyes-200-probe-' + [guid]::NewGuid().ToString('N')
$realRoot = Join-Path $env:LOCALAPPDATA 'AgentEyes'
$startMenu = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'

Write-Probe 'P1 write a file in the real per-user root' { [IO.File]::WriteAllText((Join-Path $realRoot "$tag.txt"), 'x') } { Remove-Item (Join-Path $realRoot "$tag.txt") -Force }
Write-Probe 'P2 write a file in %TEMP%' { [IO.File]::WriteAllText((Join-Path ([IO.Path]::GetTempPath()) "$tag.txt"), 'x') } { Remove-Item (Join-Path ([IO.Path]::GetTempPath()) "$tag.txt") -Force }
Write-Probe 'P3 write a file in the Start Menu' { [IO.File]::WriteAllText((Join-Path $startMenu "$tag.txt"), 'x') } { Remove-Item (Join-Path $startMenu "$tag.txt") -Force }
Write-Probe 'P4 write a value in HKCU\Environment' { $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true); if ($null -eq $k) { throw 'OpenSubKey returned null' }; $k.SetValue($tag, 'x'); $k.Dispose() } { $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true); $k.DeleteValue($tag); $k.Dispose() }
Write-Probe 'P5 write a value in HKCU Run' { $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $true); if ($null -eq $k) { throw 'OpenSubKey returned null' }; $k.SetValue($tag, 'x'); $k.Dispose() } { $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $true); $k.DeleteValue($tag); $k.Dispose() }
Write-Probe 'P6 open the real app process with full access (never used)' { $p = [Diagnostics.Process]::GetProcessById($RealAppPid); $null = $p.Handle; $p.Dispose() } $null
Write-Probe 'P7 open the real app quit event for modify (never set)' { $e = [Threading.EventWaitHandle]::OpenExisting("AgentEyes-quit-$RealAppPid", [Security.AccessControl.EventWaitHandleRights]::Modify); $e.Dispose() } $null
Write-Probe 'P8 write a file in the scratch root (must be ALLOWED)' { [IO.File]::WriteAllText((Join-Path $ScratchRoot "$tag.txt"), 'x') } { Remove-Item (Join-Path $ScratchRoot "$tag.txt") -Force }

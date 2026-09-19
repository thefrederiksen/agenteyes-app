# Running-instance awareness for the scripts that launch the app (issue #61).
#
# Every one of these scripts used to open with
#
#     Get-Process AgentEyes -ErrorAction SilentlyContinue | Stop-Process -Force
#
# and that line could never do anything. The process is named AgentEyesApp, Get-Process takes no
# wildcard here, and -ErrorAction SilentlyContinue swallowed the "no such process" error - so the
# check passed by finding nothing, every single time. The script then started a SECOND instance, the
# app's single-instance lock refused it, and the owner got a modal "AgentEyes is already running"
# popup thrown on top of whatever he was doing. It is the same mistake the installer had, found and
# fixed in issue #95; the scripts never got the fix.
#
# Two rules here, and they are the whole point of this file:
#
#   1. The process name is DERIVED from the exe path the caller already has, never typed as a
#      literal. A literal is what drifted last time.
#   2. These scripts never stop an AgentEyes they did not start. The owner's tray app may be in the
#      middle of a recording; force-killing it destroys that recording. If one is already running,
#      the script refuses and says exactly what to do.

function Get-AgentEyesProcessName {
    param([Parameter(Mandatory)][string]$ExePath)
    $name = [System.IO.Path]::GetFileNameWithoutExtension($ExePath)
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw "Cannot derive the AgentEyes process name from an empty exe path."
    }
    return $name
}

function Get-AgentEyesInstance {
    # Returns every running instance of the app that $ExePath names, as an array (possibly empty).
    # Deliberately enumerates the process table and filters on an EXACT name match rather than
    # asking Get-Process for a name: a name query that finds nothing and a name query that failed
    # look identical once the error is suppressed, and that is exactly how this defect hid. Here a
    # broken process table throws instead of reporting "nothing is running".
    param([Parameter(Mandatory)][string]$ExePath)
    $name = Get-AgentEyesProcessName -ExePath $ExePath
    return @(Get-Process -ErrorAction Stop | Where-Object { $_.ProcessName -eq $name })
}

function Assert-NoAgentEyesRunning {
    # Refuses to continue while an AgentEyes the script did not start is running.
    #
    # CALL THIS BEFORE THE SCRIPT'S try BLOCK, never inside it. PowerShell runs a finally block when
    # a script exits from inside its try, so a refusal raised in there would run a cleanup written
    # for a run that had actually started - and at least two of these scripts read "no backup file
    # exists" as "this presets.json is mine, delete it". Refusing before the try means nothing has
    # been touched and nothing needs undoing.
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$ScriptName
    )
    $running = Get-AgentEyesInstance -ExePath $ExePath
    if ($running.Count -eq 0) { return }

    $ids = ($running | ForEach-Object { $_.Id }) -join ', '
    Write-Host "REFUSED: AgentEyes is already running (process id: $ids)."
    Write-Host "  $ScriptName starts its own instance of the app, and it will not stop yours -"
    Write-Host "  yours may be in the middle of a recording."
    Write-Host "  Quit it first (tray icon -> Quit), then re-run $ScriptName."
    exit 4
}

function Start-AgentEyesForScript {
    # Starts the app for a script to drive, and hands back the process so the script can stop THAT
    # one - and only that one - when it is done.
    #
    # This re-check exists for the narrow race where an instance appears between the script's
    # opening Assert-NoAgentEyesRunning and this call. It THROWS rather than exiting, and the
    # difference matters: by this point a script has usually backed the person's presets.json and
    # config.json up and installed its own, and its finally block is what puts them back. Exiting
    # here would skip nothing - PowerShell runs finally on exit too - but it would run that finally
    # in a state it was never written for. A throw goes through the script's own catch, so the
    # restore happens exactly as it does for any other mid-run failure.
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$ScriptName,
        [string[]]$AppArguments = @()
    )
    $running = Get-AgentEyesInstance -ExePath $ExePath
    if ($running.Count -gt 0) {
        $ids = ($running | ForEach-Object { $_.Id }) -join ', '
        throw "AgentEyes started up while $ScriptName was preparing (process id: $ids). " +
              "$ScriptName will not run alongside it, and will not stop it - quit it from the tray icon and re-run."
    }

    if ($AppArguments.Count -gt 0) {
        return Start-Process $ExePath -ArgumentList $AppArguments -PassThru
    }
    return Start-Process $ExePath -PassThru
}

function Stop-ScriptOwnedAgentEyes {
    # Stops ONLY the instance this script started. A null process (the script failed before it got
    # that far) is a no-op, not an error.
    param($Process)
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) { Stop-Process -Id $Process.Id -Force -ErrorAction Stop }
    }
    catch {
        Write-Host "  note: could not stop the AgentEyes this script started (pid $($Process.Id)): $($_.Exception.Message)"
    }
}

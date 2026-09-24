# Issue #84 - the full-suite loop. Runs `dotnet test AgentEyes.sln -c Release` N times and quotes,
# for every run, the runner's own result line and every failed test by name. Each `dotnet test`
# invocation starts its own testhost, so every run is a FRESH PROCESS; the per-run folder the test
# host creates (%TEMP%\agenteyes-tests\run-yyyyMMdd-HHmmss-<pid>) is printed after each run as the
# evidence of that - a new pid per run. Builds once first so every run tests the same binaries.
#
# It is silent and app-free: `dotnet test` launches nothing, records nothing and runs no ffmpeg. It
# is NOT one of the heavy smokes and needs no -Confirm.
#
# Exit code: 0 only when EVERY run reported "Passed!"; 1 otherwise. A run that produced no result
# line at all is counted as a failure, never as a pass (a check whose pass condition is an absence
# certifies a run that never happened).
#
# Usage:
#   scripts\test-loop.ps1                        20 runs, build first, output to the console
#   scripts\test-loop.ps1 -Runs 7 -StartAt 8     runs numbered 8..14 (to split a long loop over
#                                                several invocations without renumbering)
#   scripts\test-loop.ps1 -Out proof.txt -Append also append every line to a file
#   scripts\test-loop.ps1 -NoBuild               skip the build (the binaries are already current)
param(
  [int]$Runs = 20,
  [int]$StartAt = 1,
  [switch]$NoBuild,
  [string]$Out = "",
  [switch]$Append
)

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runsParent = Join-Path $env:TEMP 'agenteyes-tests'

# -Out is resolved against the CALLER's directory once, here, before the Push-Location below - so a
# relative path is reset and written in the same place.
if ($Out -ne "" -and -not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path (Get-Location).Path $Out }

function Say([string]$line) {
  Write-Output $line
  if ($Out -ne "") { Add-Content -Path $Out -Value $line -Encoding ascii }
}

if ($Out -ne "" -and -not $Append -and (Test-Path $Out)) { Remove-Item $Out }

Push-Location $repo
try {
  Say ("test-loop: {0} run(s) numbered {1}..{2} of `dotnet test AgentEyes.sln -c Release`, each a fresh test-host process" -f $Runs, $StartAt, ($StartAt + $Runs - 1))
  Say ("test-loop: repo={0}" -f $repo)
  Say ("test-loop: started {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))

  if (-not $NoBuild) {
    Say "test-loop: building once (dotnet build AgentEyes.sln -c Release)"
    $build = & dotnet build AgentEyes.sln -c Release | ForEach-Object { "$_" }
    $buildResult = $build | Where-Object { $_ -match 'Build succeeded|Build FAILED|error' } | Select-Object -Last 3
    foreach ($l in $buildResult) { Say ("  " + $l.Trim()) }
    if ($LASTEXITCODE -ne 0) {
      Say "test-loop: BUILD FAILED - no runs made"
      exit 1
    }
  }

  $passedRuns = 0
  $failedRuns = 0
  for ($i = 0; $i -lt $Runs; $i++) {
    $n = $StartAt + $i
    $before = Get-Date
    # No TRX: every failure's message is quoted below, and the test host's own per-run folder keeps the
    # run's log. A TRX per run would sit under %TEMP% for ever - nothing prunes it.
    $lines = & dotnet test AgentEyes.sln -c Release --no-build | ForEach-Object { "$_" }
    $exit = $LASTEXITCODE
    $summary = $lines | Where-Object { $_ -match '^(Passed!|Failed!)' } | Select-Object -Last 1
    # Every failed test with its message: the "Failed <name> [ms]" line, the lines up to its stack
    # trace, and the first frame inside the test assembly (the file:line of the assertion).
    $failed = @()
    for ($k = 0; $k -lt $lines.Count; $k++) {
      if ($lines[$k] -match '^\s+Failed ') {
        $failed += $lines[$k].Trim()
        for ($m = $k + 1; $m -lt $lines.Count -and $lines[$m] -notmatch '^\s+Stack Trace:'; $m++) {
          if ($lines[$m].Trim() -ne '' -and $lines[$m] -notmatch '^\s+Error Message:') { $failed += ('    ' + $lines[$m].Trim()) }
        }
        for ($m = $m + 1; $m -lt $lines.Count -and $lines[$m] -match '^\s+at '; $m++) {
          if ($lines[$m] -match 'AgentEyes\.Tests') { $failed += ('    ' + $lines[$m].Trim()); break }
        }
      }
    }
    $runFolders = @()
    if (Test-Path $runsParent) {
      $runFolders = Get-ChildItem $runsParent -Directory | Where-Object { $_.Name -like 'run-*' -and $_.CreationTime -ge $before } | Sort-Object CreationTime | ForEach-Object { $_.Name }
    }

    if ($summary -eq $null) {
      Say ("run {0,2}/{1}: NO RESULT LINE (exit {2}) - counted as a failure" -f $n, ($StartAt + $Runs - 1), $exit)
      $failedRuns++
    }
    elseif ($summary -match '^Passed!' -and $exit -eq 0) {
      Say ("run {0,2}/{1}: {2}" -f $n, ($StartAt + $Runs - 1), $summary.Trim())
      $passedRuns++
    }
    else {
      Say ("run {0,2}/{1}: {2} (exit {3})" -f $n, ($StartAt + $Runs - 1), $summary.Trim(), $exit)
      foreach ($f in $failed) { Say ("           " + $f) }
      $failedRuns++
    }
    Say ("           test-host run folder(s): " + ($(if ($runFolders.Count -gt 0) { $runFolders -join ', ' } else { '(none found under ' + $runsParent + ')' })))
  }

  Say ("test-loop: finished {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
  Say ("test-loop: RESULT {0} of {1} run(s) passed, {2} failed" -f $passedRuns, $Runs, $failedRuns)
  if ($failedRuns -eq 0 -and $passedRuns -eq $Runs) { exit 0 } else { exit 1 }
}
finally {
  Pop-Location
}

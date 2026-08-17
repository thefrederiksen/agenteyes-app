# Developer fast-gate (CenCon Development Method).
#
# This is the Developer Agent's scoped check before handing an issue to QA: build + unit tests +
# ONLY the smoke for the area the issue touches. It deliberately does NOT run the full regression
# sweep (selftest, the other area's smoke) - that is the QA Agent's gate, scripts\run-all.ps1.
#
# Map the issue's "Affected area" to a smoke:
#   -Smoke api       Control API / AgentEyes.Core changes   -> api-smoke.ps1
#   -Smoke gui       WPF UI / AgentEyes.App view changes     -> gui-smoke.ps1
#   -Smoke api,gui   change spans both surfaces
#   -Smoke none      installer or pure-logic Core (unit-covered) -> build + unit only
#   (no argument)    build + unit only (cheap default, revised 2026-06-16) - pass -Smoke to opt in
#
# NOTE (revised 2026-06-16): this script is now a LOCAL CONVENIENCE only. The Developer gate is just
# build + unit tests - the dev no longer runs smokes or launches the app to hand off. All running-app
# verification is the QA Agent's job, at the scope QA decides. See docs\cencon\DEVELOPMENT_METHOD.md.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-dev.ps1            # build + unit
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-dev.ps1 -Smoke api # opt into a smoke
param([string[]]$Smoke = @('none'), [switch]$Confirm)
$ErrorActionPreference = 'Continue'
# USER-INVOKED ONLY (revised 2026-06-16). Runs the heavy test suite (and optional smokes).
# Agents build only and must NOT run this. It refuses unless -Confirm (or MQS_RUN_TESTS=1).
if (-not $Confirm -and $env:MQS_RUN_TESTS -ne '1') {
  Write-Host "REFUSED: run-dev.ps1 runs the heavy test suite (and optional smokes) - USER-INVOKED ONLY."
  Write-Host "Agents build only and must NOT run this. To run it yourself: re-run with -Confirm  (or set MQS_RUN_TESTS=1)."
  exit 3
}
$env:MQS_RUN_TESTS = '1'   # children (smokes) inherit this
Set-Location (Resolve-Path "$PSScriptRoot\..")
$results = [ordered]@{}

# Normalize the smoke list (lowercase, drop blanks and the 'none' sentinel).
$smokes = @($Smoke | ForEach-Object { $_.ToLowerInvariant().Trim() } | Where-Object { $_ -and $_ -ne 'none' })

"== build =="
dotnet build src\AgentEyes.App\AgentEyes.App.csproj -c Release -v q
$results['build'] = ($LASTEXITCODE -eq 0)

"== unit tests =="
dotnet test tests\AgentEyes.Tests\AgentEyes.Tests.csproj -v q
$results['unit'] = ($LASTEXITCODE -eq 0)

if ($smokes -contains 'api') {
  "== api smoke =="
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\api-smoke.ps1
  $results['api-smoke'] = ($LASTEXITCODE -eq 0)
}

if ($smokes -contains 'gui') {
  "== gui smoke =="
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\gui-smoke.ps1
  $results['gui-smoke'] = ($LASTEXITCODE -eq 0)
}

""
"============== SUMMARY (dev fast-gate) =============="
$allPass = $true
foreach ($k in $results.Keys) {
  $ok = $results[$k]
  if (-not $ok) { $allPass = $false }
  "{0,-12} {1}" -f $k, ($(if ($ok) { 'PASS' } else { 'FAIL' }))
}
if ($smokes.Count -eq 0) { "(smokes: none - build + unit only)" }
"NOTE: local convenience only. The Developer gate is build + unit; QA owns running-app verification"
"      and decides its own scope (api/gui smoke, or scripts\run-all.ps1 for broad changes)."
"===================================================="
if ($allPass) { "RUN-DEV: PASS"; exit 0 } else { "RUN-DEV: FAIL"; exit 1 }

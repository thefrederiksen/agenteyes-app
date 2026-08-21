# Full regression sweep - the QA Agent's gate (CenCon Development Method).
# build + unit tests + headless selftest + api-smoke + gui-smoke.
# Prints a single PASS/FAIL summary.
#
# The Developer Agent does NOT run this whole sweep before handoff - it runs the scoped dev
# fast-gate (scripts\run-dev.ps1: build + unit + the smoke for the area it touched). QA owns this
# full sweep as the regression gate. See docs\cencon\DEVELOPMENT_METHOD.md Section 6.
param([switch]$Confirm)
$ErrorActionPreference = 'Continue'
# USER-INVOKED ONLY (revised 2026-06-16). This is the HEAVY full sweep: it launches the app,
# records audio, runs ffmpeg/Whisper, and takes minutes. Agents must NOT run it. It refuses
# unless you pass -Confirm (or set MQS_RUN_TESTS=1).
if (-not $Confirm -and $env:MQS_RUN_TESTS -ne '1') {
  Write-Host "REFUSED: run-all.ps1 is the HEAVY full sweep (app launch, audio, ffmpeg/Whisper, minutes) - USER-INVOKED ONLY."
  Write-Host "Agents must NOT run this. To run it yourself: re-run with -Confirm  (or set MQS_RUN_TESTS=1)."
  exit 3
}
$env:MQS_RUN_TESTS = '1'   # children (smokes) inherit this; they will not re-prompt
Set-Location (Resolve-Path "$PSScriptRoot\..")
# Both projects set <Platforms>x64</Platforms>, so `dotnet build -c Release` lands in
# bin\x64\Release\. A stale non-x64 output directory on an old checkout holds a month-old binary -
# launching it silently tests code nobody built (issue #9). x64 path ONLY; missing = FAIL, no fallback.
$exe = "src\AgentEyes.Core\bin\x64\Release\net8.0-windows10.0.19041.0\agenteyes.exe"
$results = [ordered]@{}

"== build =="
dotnet build src\AgentEyes.App\AgentEyes.App.csproj -c Release -v q
$results['build'] = ($LASTEXITCODE -eq 0)

"== unit tests =="
dotnet test tests\AgentEyes.Tests\AgentEyes.Tests.csproj -v q
$results['unit'] = ($LASTEXITCODE -eq 0)

"== selftest (headless) =="
if (-not (Test-Path $exe)) {
  "RUN-ALL: FAIL (CLI binary not found - it has not been built)"
  "  expected: $(Join-Path (Get-Location) $exe)"
  "  build it: dotnet build AgentEyes.sln -c Release"
  exit 1
}
& $exe selftest
$results['selftest'] = ($LASTEXITCODE -eq 0)

"== api smoke =="
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\api-smoke.ps1
$results['api-smoke'] = ($LASTEXITCODE -eq 0)

"== gui smoke =="
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\gui-smoke.ps1
$results['gui-smoke'] = ($LASTEXITCODE -eq 0)

""
"================ SUMMARY ================"
$allPass = $true
foreach ($k in $results.Keys) {
  $ok = $results[$k]
  if (-not $ok) { $allPass = $false }
  "{0,-12} {1}" -f $k, ($(if ($ok) { 'PASS' } else { 'FAIL' }))
}
"========================================"
if ($allPass) { "RUN-ALL: PASS"; exit 0 } else { "RUN-ALL: FAIL"; exit 1 }

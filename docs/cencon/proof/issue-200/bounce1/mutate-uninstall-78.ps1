param([string]$Out = 'docs/cencon/proof/issue-200/bounce1/M-uninstall-78-red-without-deleting.txt')
# Issue #200, QA bounce 1: reintroduce the Uninstaller.cs:78 regression (the isolated branch of Apply
# disabled - the pre-PR behaviour), run the uninstall --root test, and fingerprint the REAL finalizer
# writes before and after. The mutation is reverted byte-for-byte in a finally, and the solution rebuilt.
# Run from the repo root. Tests run through AgentEyes.sln so the fresh bin\x64 build is the one tested.
$ErrorActionPreference = 'Stop'
$fp = 'docs/cencon/proof/issue-200/bounce1/fingerprint.ps1'
$u = 'tools/AgentEyes.Setup.Engine/Uninstaller.cs'
$orig = [IO.File]::ReadAllBytes($u)
$t = [IO.File]::ReadAllText($u)
$from = "        if (_layout.IsIsolated)`n        {`n            DeleteDirectoryWithRetry"
if (-not $t.Contains($from)) { $from = $from -replace "`n", "`r`n" }
if (-not $t.Contains($from)) { throw "the isolated branch was not found - the mutation would test nothing" }
$to = $from.Replace('if (_layout.IsIsolated)', 'if (false && _layout.IsIsolated) // MUTATION: isolated branch disabled')
$testOut = Join-Path $env:TEMP "agenteyes-mutate-78-$PID.txt"
$lines = @()
try {
    [IO.File]::WriteAllText($u, $t.Replace($from, $to), (New-Object Text.UTF8Encoding($true)))
    $lines += "MUTATION: Uninstaller.Apply's isolated branch (Uninstaller.cs:78) disabled - the pre-PR behaviour"
    $lines += "  - " + (($from -split "`r?`n")[0].Trim())
    $lines += "  + " + (($to -split "`r?`n")[0].Trim())
    $lines += ""
    $lines += (& $fp -Label "BEFORE the mutation run")
    $lines += ""
    $lines += "--- dotnet build AgentEyes.sln -c Release (mutated)"
    $lines += (dotnet build AgentEyes.sln -c Release 2>&1 | Select-String -Pattern 'Build succeeded|Error\(s\)' | ForEach-Object Line)
    $lines += "--- dotnet test AgentEyes.sln --filter UninstallWithRoot_RemovesOnlyWhatIsInsideTheRoot_AndSaysWhatItLeftAlone (mutated)"
    $ErrorActionPreference = 'Continue'
    cmd /c "dotnet test AgentEyes.sln -c Release --no-build --filter FullyQualifiedName~UninstallWithRoot_RemovesOnlyWhatIsInsideTheRoot_AndSaysWhatItLeftAlone > `"$testOut`" 2>&1"
    $ErrorActionPreference = 'Stop'
    $lines += (Get-Content $testOut | Where-Object { $_ -notmatch 'DesktopApp|^\s*$|Test run for|A total of|Starting test|No test matches|End of stack trace' } |
        ForEach-Object { $_.Replace($env:USERPROFILE, '%USERPROFILE%').Replace($env:TEMP, '%TEMP%').Replace((Get-Location).Path, '<repo>') })
    $lines += ""
    $lines += (& $fp -Label "AFTER the mutation run")
}
finally {
    [IO.File]::WriteAllBytes($u, $orig)
    Remove-Item $testOut -ErrorAction SilentlyContinue
}
$lines += ""
$lines += "Uninstaller.cs restored byte-for-byte; rebuild:"
$lines += (dotnet build AgentEyes.sln -c Release 2>&1 | Select-String -Pattern 'Build succeeded|Error\(s\)' | ForEach-Object Line)
$lines | Set-Content -Path $Out -Encoding ascii
$lines

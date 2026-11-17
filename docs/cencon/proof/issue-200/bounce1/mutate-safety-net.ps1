param([string]$Out = 'docs/cencon/proof/issue-200/bounce1/M-safety-net.txt')
# Issue #200, QA bounce 1: two mutations aimed at the SAFETY NET rather than the product.
#  M-B  a shortcut delete that goes AROUND the seam: the isolated branch of Uninstaller.Apply also does
#       File.Delete(InstallFinalizer.StartMenuShortcutPath()). No seam write is recorded; the test must
#       still go red, because the test run's shortcuts are real placeholder files and the snapshot reads them.
#  M-C  the safety net removed: TestRunIsolation no longer swaps InstallFinalizer.UserShell. Run with the
#       product CORRECT, so nothing could be deleted even if the guard failed. The uninstall test must
#       refuse at its precondition, before the uninstall runs.
# Each mutation is reverted byte-for-byte in a finally; the real finalizer writes are fingerprinted around each.
$ErrorActionPreference = 'Stop'
$fp = 'docs/cencon/proof/issue-200/bounce1/fingerprint.ps1'
$filter = 'FullyQualifiedName~UninstallWithRoot_RemovesOnlyWhatIsInsideTheRoot_AndSaysWhatItLeftAlone|FullyQualifiedName~InstallFinalizer_InATestRun_KeepsTheShellEntriesInMemoryNotTheRealFolders'
$lines = @()

function Invoke-Mutation([string]$Name, [string]$File, [string]$From, [string]$To) {
    $orig = [IO.File]::ReadAllBytes($File)
    $t = [IO.File]::ReadAllText($File)
    if (-not $t.Contains($From)) { $From = $From -replace "`n", "`r`n"; $To = $To -replace "`n", "`r`n" }
    if (-not $t.Contains($From)) { throw "${Name}: mutation site not found - it would test nothing" }
    $bom = $orig.Length -ge 3 -and $orig[0] -eq 0xEF
    $res = @("==================== $Name ($File)")
    $res += ($From -split "`r?`n" | ForEach-Object { "  - $($_.Trim())" })
    $res += ($To -split "`r?`n" | ForEach-Object { "  + $($_.Trim())" })
    $testOut = Join-Path $env:TEMP "agenteyes-mutate-net-$PID.txt"
    try {
        [IO.File]::WriteAllText($File, $t.Replace($From, $To), (New-Object Text.UTF8Encoding($bom)))
        $res += (& $fp -Label "BEFORE $Name")
        $res += (dotnet build AgentEyes.sln -c Release 2>&1 | Select-String -Pattern 'Build succeeded|Error\(s\)' | ForEach-Object Line)
        $ErrorActionPreference = 'Continue'
        cmd /c "dotnet test AgentEyes.sln -c Release --no-build --filter `"$filter`" > `"$testOut`" 2>&1"
        $ErrorActionPreference = 'Stop'
        $res += (Get-Content $testOut | Where-Object { $_ -match '\[FAIL\]|Error Message|^\s{3}\S|Failed!|Passed!' -and $_ -notmatch 'DesktopApp|at AgentEyes|End of stack' } |
            ForEach-Object { $_.Replace($env:USERPROFILE, '%USERPROFILE%').Replace($env:TEMP, '%TEMP%').Replace((Get-Location).Path, '<repo>') })
        $res += (& $fp -Label "AFTER $Name")
    }
    finally {
        [IO.File]::WriteAllBytes($File, $orig)
        Remove-Item $testOut -ErrorAction SilentlyContinue
    }
    $res += "$File restored byte-for-byte."
    $res += ""
    return $res
}

$lines += Invoke-Mutation 'M-B shortcut delete around the seam' 'tools/AgentEyes.Setup.Engine/Uninstaller.cs' `
    "            DeleteDirectoryWithRetry(_layout.BundleExtractDir, `"Native extraction cache`", steps, errors);`n            steps.Add(OutsideTheRootNotTouched);" `
    "            DeleteDirectoryWithRetry(_layout.BundleExtractDir, `"Native extraction cache`", steps, errors);`n            File.Delete(InstallFinalizer.StartMenuShortcutPath()); // MUTATION: bypasses the seam`n            steps.Add(OutsideTheRootNotTouched);"

$lines += Invoke-Mutation 'M-C safety net removed (product correct)' 'tests/AgentEyes.Tests/TestRunIsolation.cs' `
    "            InstallFinalizer.UserShell = UserShell;" `
    "            // InstallFinalizer.UserShell = UserShell; // MUTATION: the test run no longer swaps the shell"

$lines += "--- rebuild after both reverts"
$lines += (dotnet build AgentEyes.sln -c Release 2>&1 | Select-String -Pattern 'Build succeeded|Error\(s\)' | ForEach-Object Line)
$lines | Set-Content -Path $Out -Encoding ascii
$lines

# Bumps the product version everywhere it lives, commits, and tags - the tag push
# triggers .github\workflows\release.yml, which builds and publishes the release.
# Mirrors cc-director's release flow. Run ONLY when you intend to cut a release:
#   scripts\new-release.ps1 0.3.0          (full release  -> tag v0.3.0)
#   scripts\new-release.ps1 1.0.0-rc.1     (pre-release   -> tag v1.0.0-rc.1)
#
# A pre-release version carries a semver pre-release suffix (a '-', e.g. -rc.1). The
# '-' is the marker release.yml keys the pre-release channel off (publish as a GitHub
# pre-release, skip the public download channel). The full X.Y.Z-<suffix> string is
# written ONLY to <Version>; the .NET SDK derives numeric AssemblyVersion/FileVersion
# from the X.Y.Z prefix and carries the suffix in InformationalVersion, so nothing that
# parses a System.Version downstream ever sees the suffix.
param(
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "version must be X.Y.Z or X.Y.Z-<suffix> (e.g. 0.3.0 or 1.0.0-rc.1), got '$Version'"
}
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# Untracked files don't block a release; uncommitted changes to tracked files do.
$status = git status --porcelain --untracked-files=no
if ($status) { throw "working tree is not clean; commit or stash first:`n$status" }

# Issue #196: the version files are FOUND, not listed. A hard-coded list of four is what missed
# src/AgentEyes.DesktopApp when #116 added it - the release would have published with the tag check
# green (release.yml reads only AgentEyes.App.csproj) and the desktop app reporting an older version.
# The rule, shared with tests/Shared/ReleaseVersionTests.cs (which fails when any two disagree): every
# tracked *.csproj / *.props / *.targets with a <Version>...</Version> ELEMENT outside XML comments,
# excluding docs/ (frozen proof harnesses from past issues, which are evidence and are never released).
$element = '<Version>[^<]*</Version>'
$tracked = @(git ls-files -- '*.csproj' '*.props' '*.targets')
if ($LASTEXITCODE -ne 0) { throw "git ls-files failed (exit $LASTEXITCODE); cannot find the project files" }

# Pass 1 - find and validate EVERY file before writing ANY, so a refusal leaves the tree untouched.
$plan = @()
foreach ($f in ($tracked | Where-Object { $_ -notlike 'docs/*' } | Sort-Object)) {
    $path = Join-Path $root $f
    $content = Get-Content -LiteralPath $path -Raw
    $uncommented = [regex]::Replace($content, '(?s)<!--.*?-->', '')
    $declared = [regex]::Matches($uncommented, $element).Count
    if ($declared -eq 0) { continue }
    # Exactly one per file: two (e.g. one per condition) would leave it unclear which the build uses,
    # so stop and let a person decide rather than bump both and hope.
    if ($declared -ne 1) { throw "$f declares $declared <Version> elements; expected exactly 1. Nothing was changed." }
    # Match the current <Version> whether it is a full X.Y.Z or already carries a
    # pre-release suffix, so re-bumping rc -> rc or rc -> full release both replace cleanly.
    $updated = $content -replace '<Version>\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?</Version>', "<Version>$Version</Version>"
    if ($updated -eq $content) { throw "no <Version> element updated in $f (is it X.Y.Z, or already $Version?). Nothing was changed." }
    $after = [regex]::Matches([regex]::Replace($updated, '(?s)<!--.*?-->', ''), $element)
    if ($after.Count -ne 1 -or $after[0].Value -ne "<Version>$Version</Version>") {
        throw "$f would declare '$($after[0].Value)' after the bump, not <Version>$Version</Version>. Nothing was changed."
    }
    $plan += [pscustomobject]@{ File = $f; Path = $path; Updated = $updated }
}
if ($plan.Count -eq 0) {
    throw "found no tracked project file declaring a <Version> - the search found nothing, so nothing would be bumped"
}
"version files found: $($plan.Count)"

# Pass 2 - write.
foreach ($p in $plan) {
    # Preserve the file's original (UTF-8, no BOM) encoding.
    [IO.File]::WriteAllText($p.Path, $p.Updated)
    "bumped: $($p.File)"
}
$files = @($plan | ForEach-Object { $_.File })

# Native commands do not stop the script on failure, and a tag on a commit that failed would point
# at the UN-bumped tree. Every git step is checked.
git add -- $files
if ($LASTEXITCODE -ne 0) { throw "git add failed (exit $LASTEXITCODE); nothing committed or tagged" }
git commit -m "Release v$Version"
if ($LASTEXITCODE -ne 0) { throw "git commit failed (exit $LASTEXITCODE); NOT tagged" }
git tag "v$Version"
if ($LASTEXITCODE -ne 0) { throw "git tag v$Version failed (exit $LASTEXITCODE); the release commit exists but is not tagged" }
""
"Tagged v$Version. Push with:"
"  git push origin main v$Version"

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

$files = @(
    'src\AgentEyes.App\AgentEyes.App.csproj',
    'src\AgentEyes.Core\AgentEyes.Core.csproj',
    'tools\AgentEyes.Setup\AgentEyes.Setup.csproj',
    'tools\AgentEyes.Setup.Cli\AgentEyes.Setup.Cli.csproj'
)
foreach ($f in $files) {
    $content = Get-Content $f -Raw
    # Match the current <Version> whether it is a full X.Y.Z or already carries a
    # pre-release suffix, so re-bumping rc -> rc or rc -> full release both replace cleanly.
    $updated = $content -replace '<Version>\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?</Version>', "<Version>$Version</Version>"
    if ($updated -eq $content) { throw "no <Version> element updated in $f" }
    # Preserve the file's original (UTF-8, no BOM) encoding.
    [IO.File]::WriteAllText((Join-Path $root $f), $updated)
    "bumped: $f"
}

git add $files
git commit -m "Release v$Version"
git tag "v$Version"
""
"Tagged v$Version. Push with:"
"  git push origin main v$Version"

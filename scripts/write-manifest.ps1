# Writes release-manifest.json for an already-published dist\release\ folder,
# hashing the assets EXACTLY as they sit on disk. This is deliberately a
# separate step from publishing (build-release.ps1 -SkipManifest) so CI can
# code-sign the first-party exes BETWEEN publish and manifest - then the
# sha256 recorded here is the hash of the SIGNED bytes. The setup engine
# SHA-256-verifies each asset at install, so the manifest MUST hash the final
# (signed) bytes or installs fail.
#
# Inputs (both default to the standard build layout):
#   -ReleaseDir    folder holding the published assets. Default: dist\release
#   -VersionsFile  the asset-version sidecar written by build-release.ps1.
#                  Default: dist\asset-versions.json
#
# build-release.ps1 (without -SkipManifest) invokes this itself, so a local
# one-shot unsigned build produces the same release-manifest.json as before.
param(
    [string]$ReleaseDir,
    [string]$VersionsFile
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $ReleaseDir)   { $ReleaseDir   = Join-Path $root 'dist\release' }
if (-not $VersionsFile) { $VersionsFile = Join-Path $root 'dist\asset-versions.json' }

if (-not (Test-Path $ReleaseDir))   { throw "release dir not found: $ReleaseDir (run build-release.ps1 first)" }
if (-not (Test-Path $VersionsFile)) { throw "asset-versions sidecar not found: $VersionsFile (run build-release.ps1 first)" }

"== write release-manifest.json =="
$sidecar = Get-Content $VersionsFile -Raw | ConvertFrom-Json
$version = $sidecar.version
if (-not $version) { throw "no version in sidecar: $VersionsFile" }

$assets = [ordered]@{}
foreach ($prop in $sidecar.assetVersions.PSObject.Properties) {
    $name = $prop.Name
    $path = Join-Path $ReleaseDir $name
    if (-not (Test-Path $path)) { throw "missing asset: $name" }   # no silent drops
    $assets[$name] = [ordered]@{
        version  = $prop.Value
        size     = (Get-Item $path).Length
        sha256   = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        platform = 'windows'
    }
}
$manifest = [ordered]@{
    version = "$version"
    tag     = "v$version"
    date    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    assets  = $assets
}
$manifestPath = Join-Path $ReleaseDir 'release-manifest.json'
$manifest | ConvertTo-Json -Depth 4 | Out-File $manifestPath -Encoding utf8
"   wrote: $manifestPath"

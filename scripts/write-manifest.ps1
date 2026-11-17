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
#   -VersionsFile  the asset-version sidecar(s): build-release.ps1 writes
#                  dist\asset-versions.json (the Windows assets) and
#                  scripts/linux/build-release.sh writes asset-versions-linux.json
#                  (issue #194). Pass several to hash a release that holds both;
#                  every sidecar must name the SAME release version, and an asset
#                  named by two sidecars is an error. Default: dist\asset-versions.json
#
# Each asset's "platform" comes from its NAME: "-win-x64" -> windows,
# "-linux-x64" -> linux. A name carrying neither is refused, so no asset is
# ever published with a guessed platform.
#
# build-release.ps1 (without -SkipManifest) invokes this itself, so a local
# one-shot unsigned build produces the same release-manifest.json as before.
param(
    [string]$ReleaseDir,
    [string[]]$VersionsFile
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $ReleaseDir)   { $ReleaseDir   = Join-Path $root 'dist\release' }
if (-not $VersionsFile) { $VersionsFile = @(Join-Path $root 'dist\asset-versions.json') }

if (-not (Test-Path $ReleaseDir))   { throw "release dir not found: $ReleaseDir (run build-release.ps1 first)" }
foreach ($file in $VersionsFile) {
    if (-not (Test-Path $file)) { throw "asset-versions sidecar not found: $file (run build-release.ps1 / scripts/linux/build-release.sh first)" }
}

function Get-AssetPlatform([string]$name) {
    if ($name -like '*-win-x64*')   { return 'windows' }
    if ($name -like '*-linux-x64*') { return 'linux' }
    throw "asset '$name' names no platform (-win-x64 or -linux-x64); refusing to guess one"
}

"== write release-manifest.json =="
$version = $null
$assets = [ordered]@{}
foreach ($file in $VersionsFile) {
    $sidecar = Get-Content $file -Raw | ConvertFrom-Json
    if (-not $sidecar.version) { throw "no version in sidecar: $file" }
    if ($null -eq $version) { $version = $sidecar.version }
    elseif ($sidecar.version -ne $version) { throw "sidecar $file is for version $($sidecar.version), but the release is $version" }
    foreach ($prop in $sidecar.assetVersions.PSObject.Properties) {
        $name = $prop.Name
        if ($assets.Contains($name)) { throw "asset $name is named by more than one sidecar" }
        $path = Join-Path $ReleaseDir $name
        if (-not (Test-Path $path)) { throw "missing asset: $name" }   # no silent drops
        $assets[$name] = [ordered]@{
            version  = $prop.Value
            size     = (Get-Item $path).Length
            sha256   = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
            platform = Get-AssetPlatform $name
        }
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

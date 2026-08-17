# Builds a complete release into dist\release\: every asset + release-manifest.json,
# in cc-director's release shape (per-asset version/sha256/size/platform).
#
# Assets:
#   AgentEyesApp-win-x64.exe       the tray/GUI app (self-contained single file)
#   agenteyes-win-x64.exe                 the CLI (self-contained single file)
#   agenteyes-setup-cli-win-x64.exe       the setup CLI (install/update/uninstall)
#   AgentEyes-Setup-win-x64.exe the setup wizard - what users download
#   agenteyes-ffmpeg-win-x64.zip          bundled ffmpeg+ffprobe, versioned independently
#   release-manifest.json
#
# The dir is a valid offline release: agenteyes-setup install --release-dir dist\release
# performs a full install with no network. CI (release.yml) runs this same script.
#
# -FfmpegDir: folder containing ffmpeg.exe + ffprobe.exe. Default: this machine's
# winget install (fails with instructions if absent - no fallback engine).
#
# -SkipManifest: publish every asset and write the asset-version sidecar
# (dist\asset-versions.json), but STOP before generating release-manifest.json.
# CI uses this to insert the code-signing step BETWEEN publish and manifest so
# the manifest records the SHA-256 of the SIGNED binaries (the setup engine
# SHA-256-verifies assets at install, so the manifest must hash the final bytes):
#   1. build-release.ps1 -SkipManifest   (publish -> ffmpeg zip -> sidecar)
#   2. sign the four first-party exes in dist\release
#   3. write-manifest.ps1                 (hash the signed assets -> manifest)
# WITHOUT -SkipManifest (the local one-shot build) this script calls
# write-manifest.ps1 itself, so a local unsigned build behaves IDENTICALLY to
# before: it produces the same dist\release\ with release-manifest.json.
param(
    [string]$FfmpegDir,
    [switch]$SkipManifest
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$version = ([xml](Get-Content src\AgentEyes.App\AgentEyes.App.csproj)).Project.PropertyGroup.Version
if (-not $version) { throw "no <Version> in AgentEyes.App.csproj" }
$cliVersion = ([xml](Get-Content src\AgentEyes.Core\AgentEyes.Core.csproj)).Project.PropertyGroup.Version
if ($cliVersion -ne $version) { throw "version mismatch: app $version vs cli $cliVersion (run scripts\new-release.ps1)" }
"version: $version"

$out = Join-Path $root 'dist\release'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

function Publish-SingleFile($csproj, $producedExe, $assetName, $extraProps) {
    "== publish $assetName =="
    $stage = Join-Path $root "dist\publish-$([IO.Path]::GetFileNameWithoutExtension($csproj))"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    $props = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
               '-p:EnableCompressionInSingleFile=true') + $extraProps
    dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $stage -v q @props
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $csproj" }
    $exe = Join-Path $stage $producedExe
    if (-not (Test-Path $exe)) { throw "publish produced no $producedExe in $stage" }
    Copy-Item $exe (Join-Path $out $assetName) -Force
    Remove-Item $stage -Recurse -Force
}

Publish-SingleFile 'src\AgentEyes.App\AgentEyes.App.csproj' 'AgentEyesApp.exe' 'AgentEyesApp-win-x64.exe' @()
Publish-SingleFile 'src\AgentEyes.Core\AgentEyes.Core.csproj' 'agenteyes.exe' 'agenteyes-win-x64.exe' @()
Publish-SingleFile 'tools\AgentEyes.Setup.Cli\AgentEyes.Setup.Cli.csproj' 'agenteyes-setup.exe' 'agenteyes-setup-cli-win-x64.exe' @()
Publish-SingleFile 'tools\AgentEyes.Setup\AgentEyes.Setup.csproj' 'AgentEyes.Setup.exe' 'AgentEyes-Setup-win-x64.exe' @()

"== package ffmpeg =="
if (-not $FfmpegDir) {
    $winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
    $candidate = Get-ChildItem $winget -Recurse -Filter 'ffprobe.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($candidate) { $FfmpegDir = $candidate.DirectoryName }
}
if (-not $FfmpegDir -or -not (Test-Path (Join-Path $FfmpegDir 'ffmpeg.exe')) -or -not (Test-Path (Join-Path $FfmpegDir 'ffprobe.exe'))) {
    throw "ffmpeg.exe + ffprobe.exe not found. Install with: winget install Gyan.FFmpeg - or pass -FfmpegDir"
}
"ffmpeg source: $FfmpegDir"

# The ffmpeg asset carries ffmpeg's OWN version so app releases never re-ship it.
# Read ALL of ffmpeg's output before taking the first line: piping straight into
# Select-Object -First 1 terminates the pipeline early, which kills ffmpeg and
# leaves $LASTEXITCODE non-zero for everything downstream.
$ffOutput = & (Join-Path $FfmpegDir 'ffmpeg.exe') -version 2>$null
if ($LASTEXITCODE -ne 0) { throw "ffmpeg -version failed (exit $LASTEXITCODE): $(Join-Path $FfmpegDir 'ffmpeg.exe')" }
$ffVersionLine = $ffOutput | Select-Object -First 1
if ($ffVersionLine -notmatch 'ffmpeg version (\d+\.\d+(\.\d+)?)') { throw "could not parse ffmpeg version from: $ffVersionLine" }
$ffVersion = $Matches[1]
if ($ffVersion -notmatch '^\d+\.\d+\.\d+$') { $ffVersion = "$ffVersion.0" }
"ffmpeg version: $ffVersion"

$ffZip = Join-Path $out 'agenteyes-ffmpeg-win-x64.zip'
Compress-Archive -Path (Join-Path $FfmpegDir 'ffmpeg.exe'), (Join-Path $FfmpegDir 'ffprobe.exe') -DestinationPath $ffZip -CompressionLevel Optimal

# The asset-version map is the handoff artifact between "publish" and "manifest".
# It lives in dist\ (NOT dist\release\), so it is never part of the published set.
# write-manifest.ps1 reads it to know each asset's version without re-running
# ffmpeg (only this script has the ffmpeg source to read $ffVersion from).
"== write asset-versions sidecar =="
$assetVersions = [ordered]@{
    'AgentEyesApp-win-x64.exe'        = $version
    'agenteyes-win-x64.exe'           = $version
    'agenteyes-setup-cli-win-x64.exe' = $version
    'AgentEyes-Setup-win-x64.exe'     = $version
    'agenteyes-ffmpeg-win-x64.zip'    = $ffVersion
}
$sidecar = [ordered]@{
    version       = "$version"
    assetVersions = $assetVersions
}
$sidecarPath = Join-Path $root 'dist\asset-versions.json'
$sidecar | ConvertTo-Json -Depth 4 | Out-File $sidecarPath -Encoding utf8

if ($SkipManifest) {
    ""
    "== published (manifest skipped): $out =="
    "   sidecar: $sidecarPath"
    "   next: sign the four first-party exes in dist\release, then scripts\write-manifest.ps1"
    Get-ChildItem $out | ForEach-Object { "{0,12:N0} KB  {1}" -f ($_.Length / 1KB), $_.Name }
    return
}

# Local one-shot build: generate the manifest now (unsigned), so behavior is
# identical to before this ordering refactor.
# No $LASTEXITCODE check: that variable reflects the last NATIVE command, and
# write-manifest.ps1 runs none - so checking it here tested a stale exit code
# from an earlier exe and threw on success. write-manifest.ps1 sets
# $ErrorActionPreference = 'Stop' and throws on any failure, and that throw
# propagates through the call operator, which is the real error path.
& (Join-Path $PSScriptRoot 'write-manifest.ps1')

""
"== done: $out =="
Get-ChildItem $out | ForEach-Object { "{0,12:N0} KB  {1}" -f ($_.Length / 1KB), $_.Name }

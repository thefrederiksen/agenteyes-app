# Package a plugin folder for the registry (issue #32).
#
# Zips plugins\<id>\ (with plugin.json at the zip root), computes the SHA-256 the
# installer verifies, and prints the ready-to-paste registry.json entry. It does
# NOT publish anything.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-plugin.ps1 qa-walk-companion
#
# Output goes to dist\plugins\<id>-<version>.zip.
#
# Publishing to the registry (manual, public AgentEyes-releases repo):
#   1. Upload the zip to the EXISTING 'plugins' release - do NOT create it again:
#        gh release upload plugins dist\plugins\<id>-<version>.zip -R thefrederiksen/AgentEyes-releases
#      GOTCHA: `gh release create plugins ...` marks that release "Latest", which
#      hijacks /releases/latest and breaks the in-app updater (it expects the app
#      version there). If that happens, repin the app release:
#        gh release edit vX.Y.Z -R thefrederiksen/AgentEyes-releases --latest
#      `gh release upload` (asset add) does NOT touch the latest flag - prefer it.
#   2. Add/update this entry under .plugins[] in plugins/registry.json on main
#      (the raw URL the app reads:
#       https://raw.githubusercontent.com/thefrederiksen/AgentEyes-releases/main/plugins/registry.json).

param(
    [Parameter(Mandatory = $true)] [string] $Id,
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$src = Join-Path $repo "plugins\$Id"
if (-not (Test-Path -LiteralPath $src)) { Write-Error "no such plugin folder: plugins\$Id"; exit 1 }

$manifestPath = Join-Path $src 'plugin.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { Write-Error "plugins\$Id has no plugin.json"; exit 1 }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$version = if ($manifest.version) { $manifest.version } else { '0.0.0' }

if (-not $OutDir) { $OutDir = Join-Path $repo 'dist\plugins' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zip = Join-Path $OutDir "$Id-$version.zip"
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

# Zip the CONTENTS of the plugin folder so plugin.json sits at the zip root
# (the installer rejects a zip whose plugin.json is nested).
Compress-Archive -Path (Join-Path $src '*') -DestinationPath $zip -Force
$sha = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLower()

$entry = [ordered]@{
    id          = $manifest.id
    name        = $manifest.name
    description = $manifest.description
    version     = $version
    zipUrl      = "https://github.com/thefrederiksen/AgentEyes-releases/releases/download/plugins/$Id-$version.zip"
    sha256      = $sha
}

Write-Output "Packaged: $zip"
Write-Output "SHA-256 : $sha"
Write-Output ""
Write-Output "registry.json entry (add under .plugins[] in the releases repo):"
Write-Output ($entry | ConvertTo-Json)

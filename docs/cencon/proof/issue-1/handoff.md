# Issue #1 - Developer handoff to QA

Issue: [Plugins] The published plugin zips are stale relative to source - re-cut both
at 1.0.1 after the consolidation.

I believe this is finished. Every acceptance criterion is implemented; build and the
full test suite are green (evidence below).

## What changed

| File | Change |
|------|--------|
| `plugins/doc-companion/plugin.json` | version `1.0.0` -> `1.0.1` |
| `plugins/qa-walk-companion/plugin.json` | version `1.0.0` -> `1.0.1` |
| `plugins/registry.json` | both entries: version `1.0.1`, zipUrl `*-1.0.1.zip`, NEW sha256 pins |
| `tests/AgentEyes.Tests/PluginRegistryChannelTests.cs` | the packaging expectations now DERIVE the version from `plugins/<id>/plugin.json` (new `SourcePluginVersion` helper), so the next re-cut needs no test edits (the RetiredZipUrl known-bad constant deliberately keeps `1.0.0`) |
| `tests/AgentEyes.Tests/PublishedPluginAssetTests.cs` | NEW - downloads the published assets and pins them (details per criterion below) |
| `docs/plugins.md` | registry section no longer names the retired `AgentEyes-releases` repo; example entry updated to `1.0.1` on `agenteyes-app` |

Published (release assets, NOT part of the git diff): `doc-companion-1.0.1.zip` and
`qa-walk-companion-1.0.1.zip` uploaded to the `plugins` release of
`thefrederiksen/agenteyes-app` via `gh release upload` (upload only - the latest flag
is never touched by an upload). The `1.0.0` assets were deliberately LEFT on the
release so the registry currently on `main` keeps resolving until this PR merges.

**Post-merge cleanup (REQUIRED, part of closing this issue):** the 1.0.0 zips are
the stale artifacts this issue exists to retire - the published doc-companion 1.0.0
`run.ps1` still reads the pre-rename `MyQuietShadow` credential path. They must not
stay published once nothing references them. Immediately AFTER the squash-merge to
`main` (so the raw registry URL serves the 1.0.1 entries), whoever merges runs:

```
gh release delete-asset plugins doc-companion-1.0.0.zip -R thefrederiksen/agenteyes-app --yes
gh release delete-asset plugins qa-walk-companion-1.0.0.zip -R thefrederiksen/agenteyes-app --yes
gh release view plugins -R thefrederiksen/agenteyes-app --json assets --jq ".assets[].name"
```

Expected after cleanup: exactly the two `*-1.0.1.zip` assets. No published registry
ever pinned by `main` will dangle: the only registry that named the 1.0.0 URLs is
replaced by this same merge, and the new `PublishedPluginAssetTests` keep proving the
1.0.1 entries still resolve. Deleting them BEFORE the merge would break installs for
every user whose app still reads main's current registry - hence post-merge, not now.

The new hashes:

```
doc-companion-1.0.1.zip      73b1d2ea7bdf229debfdfbf1865083483f5c65c4676ae0930efcaa8ac664edac
qa-walk-companion-1.0.1.zip  67d1e36157d0bb60831b3576dc433ea1812a3c5adb32b8abe7619e6be0378280
```

## Acceptance criteria

### 1. Both plugins re-cut at 1.0.1 from current source

Implemented: version bumped in both `plugin.json` files; both packaged with
`scripts\package-plugin.ps1` from this checkout's source; zips uploaded to the
`plugins` release.

QA verify:
```
gh release view plugins -R thefrederiksen/agenteyes-app --json assets --jq ".assets[] | .name + \" \" + .digest"
```
Expected: `doc-companion-1.0.1.zip sha256:73b1d2ea...` and
`qa-walk-companion-1.0.1.zip sha256:67d1e361...` (full values above), alongside the
old `1.0.0` assets.

### 2. registry.json names 1.0.1 with the NEW sha256, and a test proves each registry hash matches the published asset

Implemented: `plugins/registry.json` updated in the same commit as the bump. The new
test `PublishedPluginAssetTests.PublishedAssets_HashToTheRegistryPins_AndInstallEndToEnd`
reads THIS repo's `plugins/registry.json`, downloads each `zipUrl` from GitHub for
real, and asserts the sha256 of the served bytes equals the registry pin. The
negative control `InstallZip_WrongRegistryHash_RefusesToInstall` shows the comparison
actually fires.

QA verify: run the suite (below), and/or independently:
```
Invoke-WebRequest https://github.com/thefrederiksen/agenteyes-app/releases/download/plugins/doc-companion-1.0.1.zip -OutFile d.zip
(Get-FileHash d.zip -Algorithm SHA256).Hash.ToLower()   # expect 73b1d2ea...edac
```
Same for qa-walk-companion (expect `67d1e361...8280`), and compare against the
`sha256` values in `plugins/registry.json` on the PR branch.

### 3. Published doc-companion run.ps1 uses the DevThrottle account, no MyQuietShadow path, no direct OpenAI key read

Implemented: the zips were cut from current source, which resolves credentials from
`DEVTHROTTLE_API_KEY` / `DEVTHROTTLE_BASE_URL` (issue #88). The new test
`PublishedScripts_MatchRepoSource_AndCarryNoStaleCredentialPath` extracts `run.ps1`
and `plugin.json` from the DOWNLOADED zips, asserts they match repo source
(line-ending-normalized), asserts `DEVTHROTTLE_API_KEY` is present, and scans for the
stale markers (`MyQuietShadow`, `openai`, case-insensitive). Negative control:
`StaleCredentialScan_FiresOnThePreRenameScript`.

QA verify: extract the downloaded `doc-companion-1.0.1.zip` and inspect `run.ps1` -
expect `DEVTHROTTLE_API_KEY`, and zero hits for `MyQuietShadow` or `openai`
(case-insensitive).

### 4. Installing each plugin from the registry succeeds end to end - the hash check passes

Implemented: the same new test installs each plugin from the REAL downloaded bytes
via `PluginPackage.InstallZip(zip, root, entry.Sha256)` - the exact code path
`PluginRegistry.InstallAsync` takes after its download - and asserts the installed
`plugin.json` id/version match the registry entry.

QA verify (running-app proof, this is QA's call): in the app, Plugin Manager ->
Browse catalog -> install/update each plugin; both installs must succeed with no hash
mismatch error, and show version 1.0.1 installed. NOTE: until this PR merges, the
app's default registry URL still serves main's registry (1.0.0, whose assets were
kept). To exercise the NEW entries before merge, set `PluginRegistryUrl` in
config.json to the PR branch's raw registry URL, or verify via the unit test which
reads the branch's registry file directly.

### 5. The plugins release keeps --latest=false

Implemented: assets were added with `gh release upload` only, which does not touch
the latest flag. Verified right after upload: `gh api
repos/thefrederiksen/agenteyes-app/releases/latest --jq .tag_name` -> `v1.4.9`.
Pinned by the new test `ReleasesLatest_IsNotThePluginsRelease` (reads the
`/releases/latest` redirect, no API rate limit) with negative control
`LatestReleasePin_FiresWhenLatestIsThePluginsRelease`.

QA verify:
```
gh api repos/thefrederiksen/agenteyes-app/releases/latest --jq .tag_name
```
Expected: the app release tag (currently `v1.4.9`), NOT `plugins`.

### 6. Build clean, dotnet test Failed: 0

Ran by me (the developer), after the change:

```
dotnet build AgentEyes.sln -c Release   -> Build succeeded. 0 Error(s)
dotnet test AgentEyes.sln -c Release    -> Passed! - Failed: 0, Passed: 826, Skipped: 0, Total: 826
```

## Notes for QA

- **Machine gotcha (pre-existing, not from this change):** the x64 dotnet on the dev
  machine has no .NET 8 WindowsDesktop runtime (only 10.x/6.x at
  `C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App`), so the testhost
  aborts at launch with "framework_version=8.0.0 arch=x64 missing". Setting
  `DOTNET_ROLL_FORWARD=LatestMajor` for the test run fixes it. This affects main too.
- The new `PublishedPluginAssetTests` need github.com reachable; they fail loudly
  naming the URL when it is not (deliberate - a skipped network check certifies
  nothing).
- No app-code changes, so no heavy smoke is indicated; the runtime surface touched is
  the registry-install path, which the new tests exercise over the real published
  bytes. If QA wants running-app proof, the focus-free layers are the REST Control
  API (`http://127.0.0.1:7882`), UIA (`scripts\gui-smoke.ps1` patterns), and
  PrintWindow. Never force-foreground the app and synthesize input without warning
  the human. The recording HUD is capture-excluded (`WDA_EXCLUDEFROMCAPTURE`) -
  assert HUD/recording state via UIA or `/status`, never a screen grab.

## CenCon impact

No drift: no component-map change, no privacy-posture change. `docs/plugins.md` was
minimally realigned because its registry section documented the retired repo and the
exact artifacts this issue re-cuts.

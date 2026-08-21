# QA Report - Issue #1 (PR #25)

Issue: [Plugins] The published plugin zips are stale relative to source - re-cut both
at 1.0.1 after the consolidation.
Branch: issue-1-recut-plugin-zips, tip 065d50fa867c769e339a231f486c3196e50b08d8
Verified: 2026-08-21, independently (developer report used as a map, not as evidence).

## Verdict

VERIFIED - all 6 acceptance criteria met. PASS. Handed to the Review Gate
(flow:ready-gate) per D7; QA does not merge.

## Gate runs (run by QA on the PR branch tip)

- dotnet build AgentEyes.sln -c Release -> Build succeeded. 0 Error(s) (2 warnings,
  pre-existing xUnit1031 in PostRecordingQueueTests.cs, untouched by this PR).
- dotnet test AgentEyes.sln -c Release -> Failed: 0, Passed: 826, Skipped: 0,
  Total: 826. (DOTNET_ROLL_FORWARD=LatestMajor set for the known machine gotcha
  documented in the handoff; it affects main too, not this change.)
- The six new tests filtered in isolation
  (--filter FullyQualifiedName~PublishedPluginAssetTests):
  Failed: 0, Passed: 6, Skipped: 0.

## Criteria - Expected vs Actual

### 1. Both plugins re-cut at 1.0.1 from current source - PASS

Expected: published 1.0.1 zips whose content is this branch's plugin source.
Actual: downloaded both assets from
https://github.com/thefrederiksen/agenteyes-app/releases/download/plugins/;
each zip contains exactly run.ps1 + plugin.json (the full content of
plugins/doc-companion/ and plugins/qa-walk-companion/, which hold exactly those two
files); line-ending-normalized comparison against the PR branch source: MATCH for all
four files. Published plugin.json declares version 1.0.1 in both.
Evidence: plugins/doc-companion/plugin.json:5, plugins/qa-walk-companion/plugin.json:5
(version 1.0.1); QA's own Expand-Archive + string-equality run, plus the committed
test PublishedScripts_MatchRepoSource_AndCarryNoStaleCredentialPath
(tests/AgentEyes.Tests/PublishedPluginAssetTests.cs:203).

### 2. registry.json pins 1.0.1 with the NEW sha256; a test proves pin == published - PASS

Expected: registry names 1.0.1 and the pins equal the served bytes' sha256.
Actual (QA's own download + Get-FileHash, stated in full - presence, not absence):

```
doc-companion-1.0.1.zip      73b1d2ea7bdf229debfdfbf1865083483f5c65c4676ae0930efcaa8ac664edac (4910 bytes)
qa-walk-companion-1.0.1.zip  67d1e36157d0bb60831b3576dc433ea1812a3c5adb32b8abe7619e6be0378280 (4708 bytes)
```

Both equal the pins in plugins/registry.json on the PR branch (lines 9 and 17)
character for character. The GitHub release API digests agree.
Test: PublishedAssets_HashToTheRegistryPins_AndInstallEndToEnd
(PublishedPluginAssetTests.cs:150) downloads each zipUrl for real and asserts the pin.
Mutation evidence (known-bad input, run by QA): flipping the doc-companion pin's first
byte to 00b1d2ea... in the local registry made that test FAIL with "Registry hash
mismatch for doc-companion ... pins 00b1d2ea... but the asset ... hashes to
73b1d2ea...". The instrument fires. Mutation reverted; tree clean.

### 3. Published doc-companion run.ps1: DevThrottle account, no MyQuietShadow, no OpenAI key read - PASS

Expected: the SERVED run.ps1 resolves the DevThrottle account and carries no
pre-rename credential path.
Actual, over the downloaded (not source) run.ps1 of BOTH plugins - absence checks
paired with a presence check, per Section 6c:

- PRESENCE: DEVTHROTTLE_API_KEY present (and DEVTHROTTLE_BASE_URL; the content-match
  in criterion 1 ties this to plugins/doc-companion/run.ps1:158-159 and
  plugins/qa-walk-companion/run.ps1:108-109).
- ABSENCE: 0 hits for MyQuietShadow, 0 hits for openai (case-insensitive), with the
  same scan shown to fire on known-bad text containing MyQuietShadow/openAiApiKey.

Committed controls: AssertCarriesNoStaleCredentialPath + negative control
StaleCredentialScan_FiresOnThePreRenameScript (PublishedPluginAssetTests.cs:139,229)
- both green in QA's run, and the control proves the scan can go red.

### 4. Install from the registry succeeds end to end; the hash check passes - PASS

Expected: the real registry install path succeeds over the real published bytes.
Actual: PublishedAssets_HashToTheRegistryPins_AndInstallEndToEnd installs each
downloaded zip via PluginPackage.InstallZip(zip, root, entry.Sha256) and asserts the
installed plugin.json id/version equal the registry entry - green in QA's run.
Code-path identity verified: PluginRegistry.InstallAsync does exactly
PluginPackage.InstallZip(zip, Plugins.Root, plugin.Sha256) after its download
(src/AgentEyes.App/PluginRegistry.cs:130), and InstallZip refuses on mismatch
(src/AgentEyes.Core/Plugins/PluginPackage.cs:23-31). Negative control
InstallZip_WrongRegistryHash_RefusesToInstall (PublishedPluginAssetTests.cs:185)
fires the refusal with a hash the real registry pins - green.
No app-code change in this PR, so no heavy smoke is indicated; the runtime surface
(registry install) is exercised over the real published bytes by these tests.

### 5. The plugins release keeps --latest=false - PASS

Expected: /releases/latest still resolves to the app release, not the plugins tag.
Actual (two independent instruments, run by QA):

- gh api repos/thefrederiksen/agenteyes-app/releases/latest --jq .tag_name -> v1.4.9
- GET https://github.com/thefrederiksen/agenteyes-app/releases/latest with redirects
  off -> 302, Location: https://github.com/thefrederiksen/agenteyes-app/releases/tag/v1.4.9

Pinned by ReleasesLatest_IsNotThePluginsRelease (PublishedPluginAssetTests.cs:265)
with negative control LatestReleasePin_FiresWhenLatestIsThePluginsRelease
(PublishedPluginAssetTests.cs:293) proving the pin trips on
/releases/tag/plugins - both green.

### 6. Build clean, dotnet test Failed: 0 - PASS

See "Gate runs" above: Build succeeded, 0 Error(s); Failed: 0, Passed: 826,
Skipped: 0.

## Fail-open review of the new tests (Section 6c)

- No Skip anywhere in PublishedPluginAssetTests.cs; all are [Fact]. Offline or on a
  missing asset, DownloadPublished throws naming the URL
  (PublishedPluginAssetTests.cs:92-113) - a run that cannot reach github.com FAILS
  LOUDLY, it does not report green. A response of 1000 bytes or less is rejected so
  a hash over an error page cannot pass.
- ReadRegistry asserts both known plugin ids are PRESENT before iterating
  (PublishedPluginAssetTests.cs:71-88), so no pin can pass over an empty list.
- Every scan/pin has a committed negative control, and QA additionally ran the
  registry-pin mutation above.
- Honest limit, stated in the file: the content match covers run.ps1 + plugin.json
  (currently the plugins' entire content); the latest-flag pin reads the redirect,
  the same notion the updater's API call reads.
- PluginRegistryChannelTests now derives expected versions from
  plugins/<id>/plugin.json via SourcePluginVersion, which throws on a missing
  version (PluginRegistryChannelTests.cs:333-342) - a derivation over nothing cannot
  green-light anything. The RetiredZipUrl known-bad constant correctly keeps 1.0.0.

## Notes for the Review Gate (non-blocking)

- The 1.0.0 assets are deliberately still on the plugins release so main's current
  registry keeps resolving until this PR merges. The handoff
  (docs/cencon/proof/issue-1/handoff.md) makes deleting them a REQUIRED post-merge
  step, with the exact commands. QA confirms both 1.0.0 assets are still present
  today, alongside the 1.0.1 ones - the cleanup must follow the merge.
- All changed files are pure ASCII (byte-level scan, instrument verified against a
  known-bad probe).
- docs/plugins.md realignment matches the code's actual default registry location
  (src/AgentEyes.App/PluginRegistry.cs:14-21).

QA Agent, CenCon Development Method.

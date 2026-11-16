# Issue #193 - Developer handoff: cut v1.12.0

Branch `issue-193-release-1-12-0`, from main `b0e6ae8`. Deliverables:

- the version bump, 1.11.11 -> 1.12.0, in five project files
- the release notes, `docs/releases/v1.12.0.md`

No product code, no tests, no workflow change. No tag was pushed: the tag is the owner's to push
after the merge, because it triggers a publish.

I believe this is finished.

## Criterion 1 - the version is 1.12.0 everywhere it is declared, and the tag check passes

How the declarations were found: `git grep -nI -E "<(Version|AssemblyVersion|FileVersion|InformationalVersion|PackageVersion)>" -- '*.csproj' '*.props' '*.targets'`
and `git grep -nI "1\.11\.11" -- ':!docs/cencon/proof'`. After the change, the second command
returns nothing.

    src/AgentEyes.App/AgentEyes.App.csproj:8:                <Version>1.12.0</Version>
    src/AgentEyes.Core/AgentEyes.Core.csproj:19:              <Version>1.12.0</Version>
    src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj:45:  <Version>1.12.0</Version>
    tools/AgentEyes.Setup.Cli/AgentEyes.Setup.Cli.csproj:12:  <Version>1.12.0</Version>
    tools/AgentEyes.Setup/AgentEyes.Setup.csproj:11:          <Version>1.12.0</Version>

There are no `Directory.Build.props` or `.targets` files, and no AssemblyVersion, FileVersion or
InformationalVersion elements (the SDK derives those from `<Version>`). `src/AgentEyes.LinuxApp` holds
only stale `bin/` and `obj/` folders, with no project file. The macOS `Info.plist` carries `__VERSION__`,
which `scripts/mac/build-app.sh` fills from the DesktopApp `<Version>`. `dotnet msbuild
src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj -getProperty:Version` prints `1.12.0`. The plugin
`version` fields (`plugins/*`) are each plugin's own version, not the product's, and are untouched.

The check in `.github/workflows/release.yml`, lines 34-36:

    $version = ([xml](Get-Content src/AgentEyes.App/AgentEyes.App.csproj)).Project.PropertyGroup.Version
    $tag = "${{ github.ref_name }}".TrimStart('v')
    if ($version -ne $tag) { throw "tag ${{ github.ref_name }} does not match csproj version $version" }

I ran the same expression in pwsh on this branch with both a good and a bad tag, so the check is
shown passing and also shown firing:

    PASSES for v1.12.0 (csproj 1.12.0)
    FIRES for v1.11.11 : tag v1.11.11 does not match csproj version 1.12.0

The App csproj has exactly one `<PropertyGroup>`, so `.Version` is a single string, not an array.

The built assembly carries the new version. In `src/AgentEyes.Core/bin/Release/net8.0/agenteyes.dll`,
built on this branch, the strings are `1.12.0` and `1.12.0+b0e6ae8213c9777781ba28b588c5edc04c0a85fb`.

**A finding for the owner, not fixed here:** `scripts/new-release.ps1` bumps only FOUR of the five
files. It omits `src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj`, which #118 made the source of
the Mac bundle's `CFBundleShortVersionString`. I bumped all five by hand. I did not use the script,
because it also commits and creates the tag. If the script is used for a later release, the Mac
bundle will keep the old version unless the script gains that line. I left it alone because this
issue rules out changes outside the version declarations and the notes.

## Criterion 2 - the notes list every item, grouped by platform, with the macOS signing state plain

`docs/releases/v1.12.0.md` has six sections:

1. what this release is on each platform
2. install route per platform
3. KNOWN DEFECTS by platform: all ten, plus "other open work"
4. what is new
5. every change since v1.11.11
6. what was verified

macOS: the notes say ad-hoc signed, no Developer ID, no hardened runtime, no notarisation, Gatekeeper
refuses it from an unidentified developer when downloaded or copied, and #181 is the fix. They also
say that Screen Recording is approved by the user for **"agenteyes" (the service), not "AgentEyes"
(the app)**. That distinction is the one the install script itself prints.

## Criterion 3 - no defect in the list is wrong (each issue opened and read, with comments)

Corrections to the one-line descriptions in #193. The issue's text was trusted wherever it differed.

| # | #193's line | What the issue actually says | Change in the notes |
|---|---|---|---|
| **#176** | "an ad-hoc Mac app loses its Screen Recording grant on every update" | **Wrong, by the issue's own correction comment (17:43, 16 Nov).** The service, which holds the grant, does NOT lose it on an ordinary update. Its ad-hoc identity is the apphost's code hash, and the apphost was measured byte-identical across builds. The `.app` changes identity on every release but holds no grant and needs none. The real risk is an apphost change (different SDK or apphost pack, or an assembly rename). | Rewritten: the grant survives an ordinary update; the fragile case is named; if recordings come out black after an update, check "agenteyes" |
| **#165** | "a crashed recording may not be playable" | Measured BEFORE #113 merged: a plain MP4 with no `moov`, unplayable. #113 (in this release) writes fragmented MP4 designed to play up to the last fragment. Nobody has re-measured the systemd kill since #113. | Says both, and says the combination is unmeasured |
| **#166** | "a headless Windows recorder cannot be stopped without losing the take" | The issue is mainly about the orphan reaper: a capture whose recorder died is killed outright on Windows, because there is no graceful signal. The headless half is in a comment: `agenteyes serve` ignores `taskkill` and `taskkill /F` lost the take. **`agenteyes serve` is not what the Windows installer runs.** The installer ships the WPF app. #113's effect on this is unmeasured. | Both halves stated; the headless one marked as not the installed configuration |
| **#187** | "writes a Linux file on macOS and Windows" | True of the new cross-platform app, but **the Windows installer ships the WPF app**, whose run-at-login uses the Run registry key (`src/AgentEyes.App/Autostart.cs`). On Windows, the defect reaches only someone running the new app from source. | macOS entry is a real defect; the Windows note says the installed app is not affected |
| #149 | "a Windows camera recording crashes the mux" | Correct, but measured BEFORE #146 merged and not re-measured. The lead's own comment on #149 says #146 might explain it. | Says it is unknown whether #146 changed it |
| #157 | "audio collapses rather than being placed when samples are dropped" | Correct in mechanism. In user terms: audio runs progressively EARLY from the first gap. Measured on Linux under load 32-43 (5.6 s on a 105 s take). Provoked on macOS by a virtual input (#142). Not provoked on Windows. | Written as drift, with both measurements |
| #188 | "at 1 fps on a many-core machine the watchdog can end a healthy capture" | Correct. Reachable only via the CLI or API; the presets are 15-60 fps. The issue also corrects the "at most two seconds" loss figure. | Added the CLI/API-only scope and the corrected loss figures |
| #184 | "at 5 fps on Windows the mic track ends ~4.5 s before the video" | Correct (39.4 s audio in a 44.0 s video). But it is **not established** whether the audio is truncated or the video over-runs. 30 fps was clean; 15 fps, macOS and Linux are unmeasured. CLI/API only. | Added those limits |
| #186 | "clicking the Mac app while it runs in the tray does nothing" | Correct. It is specifically the Launch Services routes (Dock, Finder, Spotlight, `open`). Running the executable directly works. | Gives the executable path as the route that was shown to work |
| #159 | "/status says idle while the mux is still running" | Correct. Measured on macOS. About 2.3% of take length, 40.6 s after a 30-minute take. | As stated, with "measured on macOS" |

Four of the ten were corrected (#176, #165, #166, #187), and three more were narrowed (#149, #184,
#188).

**Two corrections outside the ten**, to #193's "What is new" framing:

- "One app on all three platforms" is true of the source, but the Windows installer still ships the
  WPF app. The cutover is #160, which is open. The notes say this in section 1 and again under
  "what is new".
- On "Gatekeeper will call it an unidentified developer": `scripts/mac/build-app.sh` says the
  ad-hoc bundle runs on the Mac that built it, and Gatekeeper refuses it on any other Mac once it
  has been downloaded. The notes state exactly that. **I did not measure** whether a locally built
  copy shows a Gatekeeper prompt on first launch, and the notes do not claim either way.

**A publishing detail the owner needs:** `release.yml` publishes with a hard-coded one-line
`--notes` body. These notes reach the GitHub release page only by editing the release after the
workflow finishes. The command is in section 1 of the notes:
`gh release edit v1.12.0 --repo thefrederiksen/agenteyes-app --notes-file docs/releases/v1.12.0.md`.

## Criterion 4 - the unreleased history is complete

    git fetch origin
    git merge-base --is-ancestor v1.11.11 origin/main && echo ANCESTOR    -> ANCESTOR
    git rev-list --count --first-parent v1.11.11..origin/main            -> 23
    git rev-list --count v1.11.11..origin/main                           -> 28
    git log --first-parent --reverse --format='%h %cs %s' v1.11.11..origin/main

There are 23 first-parent commits. The 5 extra in the full count are the commits under the one
real merge, `8976f4b` (PR #105). The notes list all 23 in five groups: 5 features, 5
recorder/audio/mux fixes, 3 macOS fixes, 2 Control API/CLI fixes, and 8 documentation-only commits.
They sum to 23, and that sum is printed in the notes. The five commits under #105 are described in
its row. Every row's text comes from that commit's subject line. Today's six product changes (#146,
#151, #116, #156, #118, #113) are all in the list.

## Criterion 5 - gates

Run on this Mac at the branch head (version bump applied). **This Mac has no .NET 8 SDK or runtime:**
SDK 10.0.403 built net8.0 and ran it with `DOTNET_ROLL_FORWARD=LatestMajor`. So these numbers come
from the .NET 10 runtime, not .NET 8.

    dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0      -> 0 Error(s) (1 warning on the first incremental build, 0 on the rebuild)
    dotnet test tests/AgentEyes.Tests.Mac/AgentEyes.Tests.Mac.csproj -c Release
      Passed!  - Failed: 0, Passed: 430, Skipped: 2, Total: 432, Duration: 3 s
    dotnet test tests/AgentEyes.Tests.DesktopApp/AgentEyes.Tests.DesktopApp.csproj -c Release
      Passed!  - Failed: 0, Passed: 23, Skipped: 0, Total: 23, Duration: 116 ms

The two skips are opt-in tests:

- `OwnedFfmpegTests.KillOwned_FailurePath_...` needs `AGENTEYES_FFMPEG_TESTS=1`
- `ProcessIdentityTests.PackageCli_SandboxedProberLiveWriter_...` needs `AGENTEYES_REAL_CLI_TESTS=1`

I left both unset on purpose. The second one writes the person's real AgentEyes log (#161), and an
hour-long drift recording was running on this machine. The builds ran at `nice -n 19` with `-m:1`.
That recording's `/status` read `recording` before, during and after the runs. No ffmpeg was
touched, nor the service or the launchd agent.

**Not run here:** `AgentEyes.sln` (it cannot build on macOS) and the Windows suite. The Linux suite
was not run either: running it on a Mac would not be a Linux run. Both are covered by CI on the PR
(`ci.yml` has Windows, Linux and macOS jobs). QA should quote CI on the merge result. A version and
docs change cannot change test behaviour, unless a test asserted the old version, and none does
(`git grep "1\.11\.11"` outside the proof folders is empty).

## Criterion 6 - nothing else changes

`git diff --stat origin/main...HEAD` shows the five csproj lines, `docs/releases/v1.12.0.md`, and this
handoff note. Nothing else.

## For QA

- Re-run the enumeration command and compare it with section 5 of the notes, row for row.
- Open each of the ten issues and check the notes' sentence against what the issue says was measured.
  Do not check it against #193's one-liners.
- Re-run the pwsh tag check above, and confirm it fires for a mismatched tag.
- Grep the notes for identity: email address, machine names, user home paths. I found none. The
  only hit for the attribution grep is the filename CLAUDE.md, quoted from a commit subject.
- No running-app proof is needed. There is no runtime change, so no smoke applies.

CenCon impact: no drift. No component map or privacy-posture change.

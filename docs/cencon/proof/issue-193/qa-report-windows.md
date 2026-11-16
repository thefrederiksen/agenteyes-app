# Issue #193 / PR #195 - QA report (Windows seat): the v1.12.0 release and its defect list

**Verdict: FAIL - flow:qa-failed.** Three sentences in the known-defect list say more than their
issues measured: #157, #188 and #186. Each one states as observed something that was inferred, or
measured on a stand-in. The other seven defect entries are accurate. The version bump, the history,
the per-platform shape, the gates and CI on the merge ref all pass. The fix is three sentence edits
in `docs/releases/v1.12.0.md`. The replacement text is below, ready to paste.

- Head tested: `9d8e0fec3efeaf3ed296a4fe72277e361ceafd2f` (branch `issue-193-release-1-12-0`), on top of main `b0e6ae8`.
- Merge ref CI ran on: `a8adaa7` = "Merge 9d8e0fe into b0e6ae8". `mergeable_state: clean`.
- Machine: the Windows laptop, Windows 11 26200. SDK 10.0.301 building net8.0, run on runtime 8.0.21. Windows PowerShell 5.1 (no pwsh installed).

> **OPERATIONAL - THE LEAD MUST NOT MISS THIS.** `.github/workflows/release.yml:185-190` publishes the
> GitHub Release with a HARD-CODED body: `--notes "AgentEyes ${{ github.ref_name }}. Download
> AgentEyes-Setup-win-x64.exe and run it."`. Nothing in the workflow reads `docs/releases/`. After the
> `v1.12.0` workflow finishes, someone must run:
>
>     gh release edit v1.12.0 --repo thefrederiksen/agenteyes-app --notes-file docs/releases/v1.12.0.md
>
> **If that step is forgotten, the owner's release page has a one-line boilerplate body, and the defect
> list exists only in the repository.** Confirmed by reading the workflow. It is the last step in the
> job ("NOTHING FOLLOWS"). The only `gh release edit` in the repo is in `scripts/package-plugin.ps1`, and
> that one only sets `--latest`.

---

## Job 1 - the defect list, sentence by sentence against each issue

Method: I fetched each issue's body and every comment with `gh issue view`. Where the note cites a
figure the issue does not hold, I traced it to its source (#142, #113, and the Review Gate comment
6929032227). I also checked code-level claims against the tree. All ten issues are OPEN, which
matches the note's "All ten are open".

| # | Verdict | Basis |
|---|---|---|
| #157 | **WRONG - overstated (one sentence)** | see D1 |
| #188 | **WRONG - overstated (one sentence)** | see D2 |
| #186 | **WRONG - overstated (one sentence)** | see D3 |
| #187 | Accurate | Code confirms the macOS half. `src/AgentEyes.DesktopApp/Model/AppSettings.cs:58-79` is a single XDG `~/.config/autostart/agenteyes.desktop` implementation with no platform branch and no launchd call, so "nothing registers with launchd" holds. The Windows correction is also right. The installer ships `src/AgentEyes.App` (`scripts/build-release.ps1:61`, `release.yml:117` asserts `app/AgentEyesApp.exe`), and that app's run-at-login is the HKCU Run key (`src/AgentEyes.App/Autostart.cs:9`). **Developer's correction confirmed: the installed Windows app is unaffected.** |
| #176 | Accurate | Matches the issue's own correction comment (16 Nov 17:43). The service's TCC identity is the apphost cdhash, measured byte-identical, so an ordinary update keeps the grant. The risk is an apphost change. The `.app` changes cdhash on every release, but `CGPreflightScreenCaptureAccess` read False inside it and it needs no grant. "On 16 November the apphost rebuilt from main was byte-identical, same hash" is backed by #142's macOS re-run (sha256 `348b93b8...` and cdhash `0b2bc9d9` on both). **Developer's correction confirmed.** |
| #165 | Accurate | `KillMode=mixed` is at `scripts/linux/agenteyes.service:21`. The 2-4 s figure, the 142.5 s SIGTERM control and the missing moov all match. #156 (d0d0a27) merged before #113 (b0e6ae8), so "the issue's measurement predates #113" is right. Reboot and shutdown are named and unmeasured, as stated. **Developer's correction confirmed.** |
| #166 | Accurate | The orphan-reaper half is the issue body, and the log string is at `src/AgentEyes.Core/Video/FfmpegChildRegistry.cs:207`. "Clean-up on the next start" is right, and it applies to the INSTALLED Windows host too: the WPF app constructs `RepairService` (`src/AgentEyes.App/App.xaml.cs:124`), whose `Start` runs `ReapOrphans` (`src/AgentEyes.Core/RepairService.cs:153,170`). The `serve` half is the 17:53 comment, and the note correctly marks it as not what the installer runs. "#113's effect unmeasured on Windows" is honest. **Developer's correction confirmed.** |
| #149 | Accurate | 323 frames, `0xC0000005`, stall on retry, measured before #146 and not re-measured. The lead's sequencing comment says #146 "may well explain part of this". "A preset option in the Windows app" holds: `PresetEditor.xaml` has a Camera tab, and `CapturePreset.cs:53` has `CameraFps`. **Narrowing confirmed.** |
| #159 | Accurate | 0.50 s / 31.7 s, 13.68 s / 605.7 s and 40.62 s / 1801.9 s match. Measured on macOS. The note's "shared code, not measured separately on Windows or Linux" is honest. The deferred mux is shared: Windows app logs show `deferredMux=True` in #113's evidence. |
| #184 | Accurate | 39.4 s in 44.0 s, at 5 fps on Windows. 30 fps was clean. 15 fps, macOS and Linux are unmeasured. Truncated versus over-running is explicitly unestablished. CLI/API only: the WPF preset fps box offers 15/24/30/60 (`src/AgentEyes.App/PresetEditor.xaml:137-140`), and the desktop app shows a fixed 30 fps. **Narrowing confirmed.** |

### D1 - #157: the macOS sentence claims an observation that was an inference

Note, section 3: *"The same mechanism showed up on macOS with a virtual input that delivers about 78
percent of its samples."*

Source (#142, macOS rows 4-6 re-run on b0e6ae8): *"This is inferred from the timestamps and the sample
accounting, not seen in the file: the audio is digital silence."* The note defines "Measured" as "a QA
or developer seat saw it on a real machine", and "showed up" reads as seen. Also, the section heading
is "All three platforms", but Windows never provoked it (#142 Windows: "Neither take dropped audio
samples (0 gaps)"). The note does not say so.

**Replace with:** *"On macOS the same mechanism was inferred, from the capture timestamps and the sample
count, on a virtual input that delivers about 78 percent of its samples. It was not heard in the file,
which was silence. It has not been provoked on Windows."*

### D2 - #188: a stand-in measurement is presented as a measurement on a 24-core machine

Note: *"Measured: first bytes after 51.7 s with 36 encoder threads (about 24 logical cores)."*

Source (#188 and the Review Gate comment 6929032227): the 51.7 s came from a **10-core Mac** with a
**synthetic source** and **the thread count passed by hand** as *"a stand-in for a machine I do not
have"*. No machine with about 24 logical cores was measured, and the watchdog kill itself was not
observed. It follows from 51.7 s > 45 s. This matters tonight: the owner's Windows desktop has 24
logical CPUs (#142), so the sentence reads as "measured on hardware like yours".

**Replace with:** *"Measured on a 10-core Mac with a synthetic source and the encoder forced to 36
threads, standing in for a machine with about 24 logical cores: first bytes after 51.7 s. The
watchdog kill follows from that, but it has not been observed on a real many-core machine."*

### D3 - #186: four routes stated as failing, one measured

Note: *"opening it from the Dock, Finder, Spotlight or `open` shows no window, starts no process and
writes nothing to the log."*

Source (#186): only `open` (Launch Services) was measured. The issue's own scope says the four routes
must be *"tested as separate routes, because they do not all arrive the same way"*.

**Replace with:** *"Measured through `open`: no window, no new process, nothing in the log. The Dock,
Finder and Spotlight go through the same Launch Services route and are expected to fail the same way,
but they have not been tested separately."*

**Count: 3 of the 10 defect sentences need correcting. 7 are accurate, including all four of the
developer's corrections and all three narrowings.**

## Job 2 - the version, in every place

Release-gate check, run here in Windows PowerShell 5.1 with the expression from `release.yml:34-36`
unchanged apart from the tag. pwsh is not installed on this machine, and `[xml]` parsing is the same
in both.

    PASS for v1.12.0 (csproj 1.12.0, type String)
    FIRES for v1.11.11 : tag v1.11.11 does not match csproj version 1.12.0

The search was for every version declaration, not a known list:
`git grep -E "<(Version|VersionPrefix|AssemblyVersion|FileVersion|InformationalVersion|PackageVersion|ApplicationVersion|ApplicationDisplayVersion)>"`
over `*.csproj *.props *.targets *.wixproj *.vdproj *.nuspec`. It finds exactly five, all `1.12.0`:
App:8, Core:19, DesktopApp:45, Setup.Cli:12, Setup:11. Positive control: `git grep "1\.12\.0"`
outside docs gives 5 hits. There are no `Directory.Build.*` files. `git grep -E "1\.11\.[0-9]+"`
outside docs finds only test fixtures (`1.11.2`, `1.11.5` as stand-in "old build" strings), and no
declaration. Other version-like fields: the Info.plist values are `__VERSION__` (filled from
DesktopApp by `build-app.sh`), the plugin `version` fields belong to the plugins, and
`AgentEyes.Setup.Engine` declares no version. It is an internal library at the SDK default
1.0.0, unchanged from main. The release manifest version is read from the App csproj
(`scripts/build-release.ps1:37`).

Built-binary check, from my own build at this head (ProductVersion via `FileVersionInfo`):
`agenteyes.dll`, `AgentEyesApp.dll` and `AgentEyesDesktop.dll` under `bin\x64\Release\...` and
`AgentEyes.Setup.dll` and `agenteyes-setup.dll` under `bin\Release\net8.0-windows\` all read
`1.12.0+9d8e0fec...`, timestamped 17:44. **Path note:** the two Setup projects build to
`bin\Release\`, not `bin\x64\Release\`. The `bin\x64\Release\` copies there are stale (`1.11.11+14b2fa6`,
14:24), which is the x64 trap in reverse.

Already known and not re-filed: `scripts/new-release.ps1:28-31` lists four csproj files and misses
DesktopApp.

**PASS.**

## Job 3 - the unreleased history

    git merge-base --is-ancestor v1.11.11 origin/main      -> ANCESTOR   (v1.11.11 = 01ada18, 2026-10-11)
    git rev-list --count --first-parent v1.11.11..origin/main  -> 23
    git rev-list --count v1.11.11..origin/main                 -> 28
    git log --first-parent --reverse --format='%h %cs %s' v1.11.11..origin/main

I extracted the 23 hashes from that output and the hashes in the notes' section 5 tables, and
`diff`ed the two sorted sets: **identical, 23 = 23**. Dates and subjects agree row by row. The 5
extra in the full count are the five commits under `8976f4b`
(`git log 8976f4b^2 --not 8976f4b^1`), and the notes' row describes all five. The "docs only"
classification holds: for each of the 8, `git diff-tree --name-only` minus `docs/`, the skills
folder and `CLAUDE.md` is empty. Positive control: the same filter on `b0e6ae8` lists `src/` files. 15
product + 8 docs = 23. Nothing invented. **PASS.**

## Job 4 - the honest shape

- Windows only from the pipeline: `release.yml:23` `runs-on: windows-latest`, and the asset list
  (`release.yml:172-179`) is six Windows files only. The notes say this in the section 1 table and
  in the prose, with "Nothing to download" for Linux and macOS and #194 named.
- Unsigned: signing is gated on `vars.SIGNING_CERT_PROFILE` (`release.yml:80`), and
  `gh api .../actions/variables` returns `total_count: 0`. That is a well-formed response, so the
  variable is unset. The notes say unsigned, with SmartScreen "Unknown publisher".
- macOS: ad-hoc, no Developer ID, no hardened runtime, no notarisation, Gatekeeper refuses it on
  another Mac, the owner approves Screen Recording for "agenteyes" (the service). This matches the
  steps `scripts/mac/install.sh:128-139` prints. #181 is named.
- No implied Linux or macOS download anywhere in the notes. Both install routes start "From a
  checkout of the `v1.12.0` tag".
- Spot-checked install facts: the README one-line install exists and updates (README:41-47); the
  Linux script uses sudo or pkexec for ffmpeg; mac `com.agenteyes.recorder` and `~/Movies/AgentEyes/`
  match the script.
- Identity scrub of the notes: no email, no machine name (none of the fleet's three test-machine
  names), no user home path. The only hits for the identity pattern are the public repo slug in two
  URLs. Positive control: the same pattern on #149's text gives 5 hits. Attribution grep: one hit,
  the filename CLAUDE.md in a commit subject, which is the allowed exception. Non-ASCII lines: 0
  in both the notes and the handoff (positive control: an em-dash test string is caught).

**PASS.**

## Job 5 - gates and CI on the tree that would land

Local, at `9d8e0fe` (the same tree as the merge ref, because main is still `b0e6ae8`):

    dotnet build AgentEyes.sln -c Release   -> Build succeeded. 0 Error(s), 24 Warning(s), 49.7 s
    dotnet test  AgentEyes.sln -c Release   -> AgentEyes.Tests: Passed 2523, Failed 0, Skipped 16, Total 2539 (1 m 23 s)
                                               AgentEyes.Tests.DesktopApp: Passed 23, Failed 0, Total 23

CI on the **merge ref**: run 43972439841, event `pull_request`. Every job's checkout log reads
`HEAD is now at a8adaa7 Merge 9d8e0fe... into b0e6ae8...`.

| job | result |
|---|---|
| windows | Tests 2523/0/16 of 2539; with `AGENTEYES_FFMPEG_TESTS=1`, 2542/0/1 of 2543; DesktopApp 23/0 (twice) |
| linux | 472/0/2 of 474; with ffmpeg tests 473/0/1 of 474 |
| macos | 431/0/1 of 432 |

The branch push run (43972406783) is also green, with the same counts. **PASS.**

Criterion 6 (nothing else changes): `git diff --stat origin/main...HEAD` shows five 1-line csproj
changes, the notes and the handoff. No product code and no tests. **PASS.**

## What I did NOT cover

- No running-app proof and no heavy smoke. There is no runtime change.
- I did not re-measure any defect. Every verdict above is the note checked against the issue record
  and the code, not a fresh measurement.
- I did not run the tag check in pwsh (it is not installed here). I ran it in Windows PowerShell 5.1.
- I did not build the release assets (`build-release.ps1`) or check the produced exes' versions. The
  versions were checked on the solution build outputs.
- I did not read the notes' section 4 ("what is new") and section 6 line by line against each feature
  PR. Section 6 was checked against #142's closing table, and it matches.
- No tag pushed. No ffmpeg, recording or install touched.

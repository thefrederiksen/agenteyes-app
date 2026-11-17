# Issue #197 - Developer handoff: corrections to the published v1.12.0 release notes

Branch `issue-197-release-notes-corrections`, from main `02a4622`. One product file changes:
`docs/releases/v1.12.0.md`. No version change, no tag, no release, no code, no tests, no workflow.

**The published release page still carries the old text.** `gh release edit` was NOT run. Refreshing
the published body (acceptance criterion 3) is the lead's step after the gate merges:

    gh release edit v1.12.0 --repo thefrederiksen/agenteyes-app --notes-file docs/releases/v1.12.0.md

## The spec

Issue #197: the body's two items, plus four from the Linux install seat (comment of 2026-11-16 23:27Z,
evidence in PR #198 `docs/cencon/proof/issue-193/qa-report-linux-install.md`) and four from the
Windows install seat (comment of 23:38Z, evidence in PR #199
`docs/cencon/proof/issue-193/qa-report-windows-install.md`). Ten corrections:

| # | Correction | Source of the new wording |
|---|-----------|---------------------------|
| 1 | SmartScreen: the first screen is "Windows protected your PC" with only "Don't run"; "More info" must be clicked before "Run anyway" exists | Windows install report, SmartScreen section and section 6 items 1-2 (screenshots 06, 07, 01) |
| 2 | #157: the post-processing sentence marked Linux only; macOS place of loss read, not tested | #197 body item 1; gate comment 6932209492 |
| 3 | Section 6: #187 was found by code reading, not by running the app rows | #197 body item 2 |
| 4 | #159 on Linux: file missing 3.9 s after `idle`, `/record/stop` returns a path that does not exist yet, no `.muxing.tmp` at first | Linux install report N1 (timeline table) |
| 5 | Linux install: Wayland refusal, xrandr, libvlc as root, sign-in failing the install, an update stops a live recording | Linux install report N2, checked against `scripts/linux/install.sh` lines 55, 58-65, 68, 79, 154-160 at the tag |
| 6 | Section 4: Linux polite stop does not save the installed service's capture (#165), marked inferred | Linux install report N3 |
| 7 | Smart App Control: unmentioned and unmeasured | Windows install report section 6 item 3 |
| 8 | Section 1: maintainer instruction to edit the release page | Linux N4, Windows item 4 |

The ten reported items (body 2, Linux 4, Windows 4) are eight edits: Windows items 1 and 2 are both
row 1, and Windows item 4 is the same paragraph as Linux N4 (row 8).

Three places where the new wording deliberately says LESS than a seat's summary did:
- **Update stops a recording.** The #197 comment says an update "loses the take". The script
  (`install.sh:79`) prints "a running recording is stopped and kept", and the seat installed while
  idle, so neither losing nor keeping was observed. The note says the recording is stopped, that the
  script says it is kept, and that this has not been tested.
- **Sign-in failure.** The seat's report says the service AND the app are running by then. By the
  script, the service is restarted and healthy before sign-in (lines 141-152), but the app is started
  only after it (line 163), and `fail` exits under `set -e`. So the note says only that the recorder
  is installed and running.
- **SmartScreen colour / route.** The note does not say "blue" (not in the report). It names the
  Explorer route the seat measured, the "Open File - Security Warning" route it also measured, and
  that the README one-liner showed no warning on the test machine. The Wayland refusal was read from
  the script, not seen; the note states it as what the script does.

## The downloader's-eye pass

Read the whole file once as the person who downloads it. Beyond the ten, it changed three things,
all internal working-document language:
- D1: header "Prepared 16 November 2026 for issue #193." -> "Released 16 November 2026."
- D2: "a QA or developer seat" -> "a tester or developer" (the method's jargon).
- D3: "Windows code signing: the repository variable is unset." -> "the Windows exes are not signed yet."
- The section 1 table cell's "the repository's signing variable" and the docs/signing.md pointer went
  with correction 1, since that cell was rewritten.

Read and left alone: section 5's commit list (verifiable history; the `CLAUDE.md` commit subject is a
filename), section 6's QA wording (it describes the verification honestly), the macOS install section,
and "this issue" inside the #176 entry (reads as #176 in context).

## The other eight defect entries

Re-read #188, #149, #184, #166, #165, #186, #187 (both halves) and #176 for "found", "seen",
"observed", "measured", "tested", against each issue (`gh issue view`) and the proof files the gate
cited. **Nothing to change.** Each figure is in its source: #188 51.7 / 30.8 / 7.7 s in #188; #149's
323 frames and `0xC0000005` in #149; #184's 39.4 s in #184 and 39.06 s at
`docs/cencon/proof/issue-113/pass5/report.md:226`; #166's "may not be recoverable" and the `taskkill`
result in #166; #165's 142.5 s in #165 and "transient user unit" at
`docs/cencon/proof/issue-156/qa-report-linux.md:142`; #186's `open`-only measurement in #186; #176's
byte-identical apphost in #176. One observation outside this issue's scope: **issue #187's own body**
says "found while running #142's application rows", which overstates it the same way; the notes now
say code reading. The issue text is not this issue's to edit.

## Verification

- **Nothing outside the edited text moved.** A script applies the 13 replacements in reverse to the
  new file (and re-inserts the deleted paragraph) and gets the original byte for byte: `True`.
  Control: the same script with one character changed outside the edits ("open." -> "open!") gives
  `False`.
- `git diff --stat origin/main`: only `docs/releases/v1.12.0.md` and this handoff.
- **ASCII:** 0 non-ASCII lines in the notes and in this handoff. Control: a string with one `e`-acute
  is counted as 1.
- **Identity:** the pattern (owner name and domain, `/Users/`, `/home/`, machine names) has 0 hits in
  the notes. Control: the same pattern finds 3 `/Users/` hits in
  `docs/cencon/proof/issue-142/qa-report-macos.md`, which is #173's and was not touched.
- **Attribution:** the only hit is the filename `CLAUDE.md` in section 5's commit table, unchanged.
- **No `gh release` command** remains in the notes (0 hits).
- **Tests on macOS** (AgentEyes.sln cannot build on a Mac; `DOTNET_ROLL_FORWARD=Major` on the .NET 10
  runtime, no .NET 8 runtime here): `tests/AgentEyes.Tests.Mac` 430 passed, 2 skipped, 0 failed, 432
  total; `tests/AgentEyes.Tests.DesktopApp` 23 passed, 0 failed. Docs-only change; quoted for the gate.
  `/status` was `idle` before the run.

Nothing of the owner's was touched: no installed service, no launchd agent, no recording, no ffmpeg.

## How QA verifies

1. Read each before/after below against its source (table above).
2. Re-run the reverse-replacement check, or read `git diff origin/main -- docs/releases/v1.12.0.md`: 10 hunks
   holding the 13 replacements listed below.
3. Read the Windows section as someone who has never seen SmartScreen: can they get from "Don't run"
   to the installer?
4. ASCII and identity greps as above.

I believe this is finished. The published page is refreshed only when the lead runs `gh release edit`.

## Every before and after

### 1 - SmartScreen, section 1 table

Before:

    **No.** Code signing is switched off until the repository's signing variable is set, so the exes are unsigned. SmartScreen will show "Unknown publisher" (docs/signing.md). |

After:

    **No.** The exes are not code-signed, so Windows warns before it runs the installer. Section 2 says exactly what to click. |

### 1/7 - SmartScreen steps and Smart App Control, section 2 Windows

Before:

    install is per-user and needs no admin rights. Because the exes are unsigned, Windows SmartScreen
    will warn about an unknown publisher, and you have to choose to run it anyway.

After:

    install is per-user and needs no admin rights.
    
    **Windows will warn you before the installer runs, because it is not signed. Here is what to click.**
    If you downloaded it with a browser and open it from File Explorer, a window says **"Windows
    protected your PC"**, and its only button is **"Don't run"**. Do not click that button. Click the
    words **"More info"** under the message: they are a link, though they do not look like one. The
    window then shows "Publisher: Unknown publisher" and a second button, **"Run anyway"**. Click
    **"Run anyway"** and the installer opens. If the installer is started some other way, you may see an
    "Open File - Security Warning" box instead, saying the publisher could not be verified. Click
    **"Run"**. On the test machine, the one-line terminal install showed no warning at all.
    
    **Smart App Control has not been tested.** If it is switched on (Windows 11), it is expected to
    block the unsigned installer outright, with no "Run anyway" button. This is not measured: Smart
    App Control was off on the machine the installer was tested on.

### 2 - #157 post-processing sentence

Before:

    On Linux the raw
      capture is in sync. The post-processing (the two-pass mux from #136) closes the gaps.

After:

    On Linux the raw
      capture is in sync, and the gaps are closed by the post-processing (the two-pass mux from #136).
      That was shown on Linux only. On macOS, the capture command suggests the timing is lost earlier,
      at capture, but that was read, not tested.

### 3 - Section 6 last line

Before:

    The
    app rows found #186 and #187 on macOS.

After:

    Running
    the app rows on macOS found #186. #187 was found by reading the code while those rows were being
    run; it has not been run.

### 4a - #159 opening

Before:

    `/status` goes to `idle` at once, but `recording.mp4` is still being written. For a while only
      `recording.mp4.muxing.tmp` is on disk.

After:

    `/status` goes to `idle` at once, but `recording.mp4` is still being written. For a while it is
      not on disk at all. On Linux, at first there is not even a temporary file, only the raw capture;
      later `recording.mp4.muxing.tmp` appears.

### 4b - #159 where the file was looked for

Before:

    The missing file itself was
      looked for on macOS only. On Windows and Linux the status read `idle` while the manifest still
      said the mux was running.

After:

    The missing file itself was
      looked for on macOS, and on Linux on this release: after a 32 s take, `recording.mp4` was still
      missing 3.9 s after `/status` read `idle`, and the `File` path that `/record/stop` returned did
      not exist yet. On Windows the status read `idle` while the manifest still said the mux was
      running; the file itself was not looked for there.

### 4c - #159 finished signal

Before:

    Until this is fixed,
      the only reliable "finished" signal is `PostProcessing.mux.State` in the recording's manifest.

After:

    Until this is fixed,
      the only reliable "finished" signal is `PostProcessing.mux.State` in the recording's manifest.
      Waiting on the temporary file does not work, because it is not there at first.

### 5 - Linux install section

Before:

    `~/Videos/AgentEyes/`. To update, check out the newer tag and run the script again.

After:

    `~/Videos/AgentEyes/`.
    
    Before you run it, know that the script:
    
    - **Refuses to run on Wayland.** AgentEyes on Linux records X11 only. Current Ubuntu logs in to
      Wayland by default, so log in to an X11 session first (on Ubuntu, choose "Ubuntu on Xorg" at
      the login screen).
    - **Needs `xrandr`**, and stops if it is missing (`sudo apt install -y x11-xserver-utils`).
    - **Installs the video playback library as root** if it is missing (`libvlc-dev`,
      `vlc-plugin-base`, `vlc-plugin-video-output`), asking for your permission the same way as for
      ffmpeg.
    - **Signs you in to DevThrottle**, which transcribes and titles the recordings, if you are not
      signed in already. If sign-in does not complete, the install ends with `[FAIL]`, although the
      recorder is already installed and running by then. Run `agenteyes signin` to finish.
    
    To update, check out the newer tag and run the script again. **Updating stops any recording that is
    in progress.** The script says the recording is kept, but that has not been tested. Do not update
    in the middle of a recording you need.

### 6 - Section 4 (#156), Linux polite stop

Before:

    cleaned up at start (on Linux and macOS with a polite stop first; on Windows see #166).

After:

    cleaned up at start (on Linux and macOS with a polite stop first; on Windows see #166). On Linux
      this does not save a capture made by the installed recorder service: systemd kills that capture
      a few seconds after the service dies, before the service restarts (#165). That is inferred from
      the installed service's settings and #165's measurement. It has not been measured.

### 8 - Section 1 maintainer instruction (deleted)

Before:

    **The GitHub release page will not show these notes on its own.** `release.yml` publishes the release
    with a fixed one-line body ("Download AgentEyes-Setup-win-x64.exe and run it."). To show these notes
    there, edit the release after the workflow finishes:
    
        gh release edit v1.12.0 --repo thefrederiksen/agenteyes-app --notes-file docs/releases/v1.12.0.md

After:

    (paragraph removed)

### D1 (downloader pass) - header

Before:

    Prepared 16 November 2026 for issue #193. The previous release is v1.11.11 (11 October 2026).

After:

    Released 16 November 2026. The previous release is v1.11.11 (11 October 2026).

### D2 (downloader pass) - 'seat'

Before:

    "Measured" means a QA or developer seat saw it on
    a real machine.

After:

    "Measured" means a tester or developer saw it on
    a real machine.

### D3 (downloader pass) - Other open work

Before:

    - Windows code signing: the repository variable is unset.

After:

    - Windows code signing: the Windows exes are not signed yet.


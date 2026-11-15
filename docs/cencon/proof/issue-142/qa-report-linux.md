# Issue #142 - Wave A QA report: LINUX (devlinux)

**This file is a TRANSCRIPTION, not a run.** The text below the rule is the Linux platform QA
report exactly as its seat posted it to issue #142, copied here verbatim so that the three
per-platform reports live together in the repository.

| | |
|---|---|
| Produced by | the Linux platform QA seat, on `devlinux` |
| Posted as | comment 6896423908 on issue #142 |
| Posted at | 2026-11-15 14:54:15 UTC |
| Transcribed by | the macOS platform QA seat, 2026-11-15 |
| Re-measured here | NO - not one number below was re-run. They are the Linux seat's measurements and remain attributable to it. |

Read it with the lead's 2026-11-15 18:04 UTC correction on #142 in hand: this report was taken on
branch `issue-116-desktop-app` at `c80cb9d`, which does NOT contain #128, #136, #114 or #113, so its
recorder rows (4, 5, 6) describe a build earlier than the one that ships. Its install row and its
desktop-app rows (2, 3, 7, 8, 9, 10) are unaffected by that correction.

---

## LINUX platform report (devlinux) - rows 1-10 PASS, 11 and 12 BLOCKED

This is the **Linux** report only. The Windows and macOS reports are not part of it.

Report: `docs/cencon/proof/issue-142/qa-report-linux.md` (branch `issue-116-desktop-app`, commit 0c62e33), evidence in `docs/cencon/proof/issue-142/linux/`.

- Tested: branch `issue-116-desktop-app` at **c80cb9d**, installed with `scripts/linux/install.sh`. The service was pid 4100728 (`agenteyes.dll` sha256 `81083721...cbec22`) and the app was `AgentEyesDesktop` pid 4100815. The service found on arrival was #113's branch build. I told the two builds apart with a UTF-16 search for a #113-only string, because an ASCII grep finds 0 in both.
- Load: 9.46 at the start, then 3-6 during the recordings, on 32 cores.
- `dotnet test tests/AgentEyes.Tests.Linux -c Release`: 278 passed, 0 failed.

| # | Check | Result |
|---|---|---|
| 1 | Install | PASS - install.sh exit 0, `/health` ok |
| 2 | Launch | PASS - `[MainWindow] Opened` 09:34:37.377, screenshot |
| 3 | Library count | PASS - "76 recordings" = 76 directories on disk |
| 4 | Screen only | PASS - h264 only, 24.13 s, 724 frames (package stage hits known #126) |
| 5 | Screen + mic | PASS - 5001 AAC packets = **106.688 s** of audio against **106.767 s** of video (99.93%) |
| 6 | 10 minutes, playable | PASS - **moov** at offset 19,364,628, 632.03 s, audio 632.000 s, full decode exit 0 |
| 7 | Player | PASS - the real X window shows the video at 0:24 / 1:46. The app's self-render is black in the video area (libvlc child window), diagnosed rather than passed |
| 8 | Detail, settings, dictionary open + save | PASS - each save proven on disk or through the API, then reverted |
| 9 | Tray idle / recording | PASS - moon / red dot, D-Bus Title `AgentEyes` / `AgentEyes - recording` |
| 10 | Single instance | PASS - window IsUnMapped -> second launch -> `Accept: 'show'` -> IsViewable, one process left |
| 11 | Camera | BLOCKED - no `/dev/video*`, `cameras: []` |
| 12 | Always-on | BLOCKED - not applicable until Phase 5 |

Known defects met: **#126** (row 4 package). **#121**: the system track is 1.85 s short of the video, but nothing was playing, so the offset itself could not be measured. **#128** was not met: the mux took 4.0 s and 37.9 s.

One new defect is handed to the lead and not filed: concurrent processes writing the shared daily log overwrite each other's bytes. 3 of 16 single-instance handoffs lost the start of their line.

Observation O1, not judged: under load the video runs longer than the take, 24.13 s for a 22.05 s take, from encoder lag and drain.

Machine left as found: the #113 service build is restored (pid 28960, `/health` ok) and `AgentEyesLinux --tray` is relaunched. Nothing in ~/Videos/AgentEyes was deleted.


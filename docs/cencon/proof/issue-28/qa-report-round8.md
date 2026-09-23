# QA report - issue #28, ROUND 8 (PR #32)

**Verdict: PASS - 17/17 acceptance criteria verified against the AMENDED spec.**

Branch `issue-28-camera-failure-boundaries`, head `f11abdd`, product fix `aa92426`
("Derive the camera stop kind from a recorded termination history").
Developer handoff: `docs/cencon/proof/issue-28/handoff-round8.md`.
Gate verdict answered: `docs/cencon/review/pr32-issue28-gate-round6.md` (REJECT).

Everything below was run by this QA agent, in its own isolated worktree
(`D:\ReposFred\agenteyes-qa28-r8`, detached at `f11abdd`) with its own `dotnet restore`
before the first build. The human ran nothing.

Proof artifacts committed with this report:

- `docs/cencon/proof/issue-28/qa-round8-probe.cs.txt` - my independent probe (its own fake,
  its own assertions; nothing shared with the branch's tests).
- `docs/cencon/proof/issue-28/qa-round8-probe-results.txt` - the rejected-tree run, the head
  run, the new-call-site attack, my 15-mutation matrix, and what the probe cannot see.

---

## 1. Round 8 is a design change, so the DESIGN PROPERTY was tested, not just the two bugs

The property the redesign claims: **a call site must not be able to record a stop kind it
has not earned.** I attacked it three ways.

### 1a. Structurally, over the type itself

`ANewCallSite_CannotSayAnyStopKind_ItCanOnlyReportWhatItDid` is written as if I were a
developer adding a call site today.

| What I tried | What happened |
|---|---|
| Find any member of `CameraTerminationRecord` that ACCEPTS a `CameraStopKind` | `NEW_CALL_SITE members taking a CameraStopKind: (none)` |
| Find a settable stop-kind field on `FfmpegCameraRecorder` | `NEW_CALL_SITE settable stop-kind fields on the recorder: (none)` |
| Report a quit DELIVERED that was never attempted | `InvalidOperationException` |
| Report a kill outcome with no kill issued (both `KillRefused` and `KillConfirmedProcessGone`) | `InvalidOperationException` |
| Report TWO outcomes in one round | `InvalidOperationException` |
| Report an exit as a termination outcome before anything was attempted | `InvalidOperationException` |
| Attempt a quit / a kill outside any round | `InvalidOperationException` |
| Earn `abandoned` from ONE refused round | kind stays **ABSENT** |
| Write `exited-early` over a fight already in progress | `ProcessGoneWithoutAnyAttempt` records nothing |
| Take back an earned `abandoned` with a later clean quit AND a later landed kill | `kind=abandoned` both times |

The two instrument checks that make those "(none)" answers mean something: the type still
exposes `StopKind` as `CameraStopKind?` (so the reflection query is looking at the right
type), and the same grep that returns 0 hits for `_stopKind` on head returns **13 hits on
the rejected tree**. The five assignment sites are genuinely gone, not renamed.

### 1b. Behaviourally, through unusual public orders

Every one of these drives only `Open` / `Stop` / `Dispose` and then asserts the derived kind
against the quits and kills my fake actually counted:

| Sequence | quits/kills | kind | complete |
|---|---|---|---|
| Dispose with no earlier Stop, process survives everything | 2 / 2 | `abandoned` | `unknown` |
| Dispose with no earlier Stop, the RETRY's kill lands | 2 / 2 | `force-killed` | `no` |
| Dispose with no earlier Stop, process dies BETWEEN rounds | 1 / 1 | **ABSENT** | `unknown` |
| Stop then Dispose (the normal path) | 2 / 2 | `abandoned` | `unknown` |
| ONE refused round only | 1 / 1 | **ABSENT** | `unknown` (and `IsAbandoned` already true) |
| TWELVE refused Stops in a row | 12 / 12 | `abandoned` from round 2 on, never anything else | `unknown` |
| earned abandoned, then a recovery QUIT that lands (exit 0) | 3 / 2 | `abandoned` | `unknown` |
| earned abandoned, then a recovery QUIT that lands (exit 255) | 3 / 2 | `abandoned` | `unknown` |
| earned abandoned, then a recovery KILL that lands | 3 / 3 | `abandoned` | `unknown` |
| earned abandoned, then Dispose four more times | 5 / 4 | `abandoned` | `unknown` |
| Stop, Dispose, Stop, Stop (kill finally lands), Dispose | 4 / 4 | `abandoned` | `unknown` |
| clean Stop, then Dispose twice, then Stop again | 1 / 0 | `clean-quit` | `yes` |
| force-killed, then Dispose/Stop/Dispose | 1 / 1 | `force-killed` | `no` |
| died before the stop, then Dispose twice | 0 / 0 | `exited-early` | `no` |

In every row the LIVE flag `IsAbandoned` follows the process (false once it really ends)
while the DURABLE kind follows the history. That is exactly the split the gate asked for.

### 1c. The probe was run against the REJECTED tree first, and it FIRED

29 scenarios against `6d90ba0` in a second worktree: **20 passed, 9 FAILED**, and the two
failures the gate named came back with the gate's own numbers:

```
DIRECT_DISPOSE_ONE_KILL        quits=1 kills=1 exited=False kind=abandoned  complete=unknown abandoned=True
RECOVERY_AFTER_ABANDONED_QUIT  quits=3 kills=2 exited=True  kind=clean-quit complete=yes     abandoned=False
```

The second line is the compounding harm too: `complete=yes` after the same recorder had
established `abandoned` / `unknown`. On head both read `quits=2 kills=2 ... kind=abandoned`
and `kind=abandoned complete=unknown` respectively.

A third rejected-tree failure is worth recording because it shows the root cause rather than
a symptom: `MANY_STOPS` on the rejected tree earned NO kind at all after **twelve** refused
rounds, while a single direct `Dispose()` earned `abandoned` after one. Same guarantee, two
opposite answers, decided by which call site you came through. That is the design error, and
it is gone.

---

## 2. Every acceptance criterion

AC1-AC9, AC11, AC12 stand as written; AC10 is the amended one; AC13-AC17 are the new ones.

| # | Expected | What I observed | Verdict |
|---|---|---|---|
| **AC1** | `GET /devices` 200 with a `cameras` array of exact DirectShow names | `{"cameras":["HD Webcam eMeet C960","OBS Virtual Camera"]}` from **my** build (`Get-Process -Id ...).Path` = my worktree's `AgentEyesApp.exe`). The no-camera arm cannot be run here (2 cameras attached); the enumerator that both surfaces read is run instead - an audio-only ffmpeg listing gives `cameras=0 mics=1`, and `RestServer.Devices()` has no `[]` fallback (RestServer.cs:405). | PASS |
| **AC2** | `agenteyes screens` prints a Cameras section | `CAMERAS: DirectShow video devices (used by 'video' mode --camera)` / `"HD Webcam eMeet C960"` / `"OBS Virtual Camera"`. `(none found)` arm is code-verified at Commands.cs:71 (`if (cams.Count == 0)`). Header reads `CAMERAS:`, the file's own convention. | PASS |
| **AC3** | One dir with both files; `camera.mp4` one video stream, no audio; durations within 1.0s | REST run `2026-08-28_223851_video`: `ffprobe` recording.mp4 **19.600000s**, camera.mp4 **20.066647s** -> delta **0.467s**; camera.mp4 streams = `0,video` only (61 MB). CLI run `2026-08-28_223516_video`: **10.833333s** vs **11.333322s** -> delta **0.500s**; camera.mp4 = `0,video`. | PASS |
| **AC4** | `cameraFile`, numeric `cameraStartOffsetSeconds`, `camera.mp4` in `files` | `"CameraFile": "camera.mp4"`, `"CameraStartOffsetSeconds": -0.55`, `"Files": ["recording.mp4","camera.mp4"]`. Keys are PascalCase, the manifest file's own convention throughout. | PASS |
| **AC5** | `/status` reports the resolved camera; null without one | With a camera: `State=recording Camera='HD Webcam eMeet C960'`. Without: `State=recording Camera=None`. Both arms run. | PASS |
| **AC6** | Preset keeps its camera across a restart (presets.json + reopened editor via UIA); starting it from the launcher gives the AC3 directory | Driven by UIA against my build: editor `CameraBox` BEFORE `(None)` -> AFTER `HD Webcam eMeet C960`; presets.json `Camera = 'HD Webcam eMeet C960'`; after a full app restart the reopened editor reads `HD Webcam eMeet C960`; launcher `REC` -> `State=recording Camera=HD Webcam eMeet C960`, new dir with `recording.mp4` + `camera.mp4`, manifest `StopKind=clean-quit Complete=yes`. `AC6_RESULT PASS`. presets.json/config.json restored afterwards. | PASS |
| **AC7** | CLI parity | `agenteyes video --screen 1 --camera "eMeet" --seconds 8` -> the same two-file dir and the same manifest fields as AC3/AC4. | PASS |
| **AC8** | Unknown camera: CLI error naming it, non-zero exit, NO directory; REST 400 with the fragment; status idle | CLI: `[error] no DirectShow camera matches "no-such-device". Run 'agenteyes screens' to list cameras.` `EXITCODE=1`, `%USERPROFILE%\Videos\AgentEyes` **34 dirs before and 34 after**. REST: `HTTP=400`, body `no DirectShow camera matches "no-such-device"...`, `/status State=idle`. | PASS |
| **AC9** | Busy camera fails the start, no directory, never records screen-only | The holder was PROVEN to hold the device first (`Input #0, dshow, from 'video=HD Webcam eMeet C960':` in the holder's own stderr). Then: `[error] the camera "HD Webcam eMeet C960" could not be opened (ffmpeg exited with code -5). Likely cause: ... already in use by another application.` `EXITCODE=1`, recordings **2 before, 2 after**. | PASS |
| **AC10 (amended)** | Camera dies mid-run: screen recording survives and is valid; `CameraStopKind: exited-early`, `CameraComplete: no`, the observed seconds; a WARNING naming the camera | Real: camera ffmpeg PID 50004 killed 5s into a 20s run. `recording.mp4` ffprobe **23.300000s**, valid. Manifest: `CameraStopKind=exited-early`, `CameraComplete=no`, `CameraCapturedSeconds=3.13`. App log `22:37:35.986 [WARN] [FfmpegCameraRecorder] the camera "HD Webcam eMeet C960" stopped during the recording ... camera.mp4 is truncated at 3.1s`. camera.mp4 has no moov atom - correctly reported as `no`, not claimed complete. | PASS |
| **AC11** | No camera: one `recording.mp4`, no `camera.mp4`, no `cameraFile` key | CLI dir `2026-08-28_223551_video`: files = manifest.json, recording.mp4, log, shots/. **0** occurrences of "Camera" in manifest.json (the same grep finds 8 in an AC4 manifest). REST dir `2026-08-28_223920_video`: `Camera keys present: []`, `Files=['recording.mp4']`. | PASS |
| **AC12** | Build clean + tests pass | `dotnet build AgentEyes.sln -c Release` -> `Build succeeded.` **0 Error(s)**, 4 pre-existing analyzer warnings. `dotnet test AgentEyes.sln -c Release` -> **Failed: 0, Passed: 947, Skipped: 0, Total: 947**; with my probe added, **982/982**. Camera/parser/resolver tests present and exercised (QA-M4/M5/M14/M15 each fire them). | PASS |
| **AC13** | One tick then stall is never `yes` | `AC13_ONE_TICK_STALL ... kind=clean-quit complete=unknown` - a healthy quit, but a single advance, so completeness refuses. QA-M14 (remove the two-advance/freshness clause) fires 5 tests including this one. | PASS |
| **AC14** | Force-killed: `force-killed`, never `yes`, and the stop SURFACES it to its caller | `AC14_FORCE_KILLED ... kind=force-killed complete=no threw=CameraForceKilledException device=QA PROBE CAM`; manifest `force-killed` / `no`. The throw is asserted as a PRESENCE, and QA-M13 (delete the throw) fires it. | PASS |
| **AC15** | Incomplete stderr: `CameraStderrComplete` false, never `yes` | `AC15_INCOMPLETE_STDERR ... complete=unknown stderrComplete=False`; manifest `CameraStderrComplete=false`, complete not `yes`. Also with an exit 255 that DOES earn the stop kind: `clean-quit` / `unknown` - the exit code buys the kind, not the completeness. QA-M15 fires 4 tests. | PASS |
| **AC16** | Abandoned camera stays reachable; `/status` reports it with its PID; the stop reports failure; manifest `abandoned` / `unknown`; the claim is NOT released | Through the real `StrandedCameraOwner`, the real `RecordStatus` and the real `RestServer` serializer options: `"CameraStuck": true`, `"StuckCameras":[{"Device":"QA PROBE CAM","Pid":40404,...}]`. `ReleaseClaimUnlessStranded` returned **true** (retained), and a second `RecordingWorkset.TryClaim` on that directory was **REFUSED** while the process lived - then succeeded after `Recover()` saw it end, with `StopKind` still `Abandoned`. Manifest `abandoned` / `unknown`. The stop itself throws `CameraStopFailedException` out of `KillOrThrow`. QA-M11 (never retain) fires 12 tests including mine. | PASS |
| **AC17** | POSITIVE CONTROL: a good recording still says `yes`, in the same build | **Three real recordings** in the same build that passes AC13-AC16: CLI `2026-08-28_223516_video`, REST `2026-08-28_223851_video`, launcher-from-preset `2026-08-28_224028_video` - each `"CameraStopKind": "clean-quit"`, `"CameraStderrComplete": true`, **`"CameraComplete": "yes"`**. At the seam, exit 0 and exit 255 both reach `clean-quit` / `yes`. The anti-cheat was RUN, not read: QA-M8 makes `Completeness` answer `unknown` unconditionally and **14 tests go red, including both AC17 positive controls**. | PASS |

---

## 3. Nothing the gate confirmed fixed has regressed

Re-established on head by my own probe, not by trusting the developer's table:

| Gate-confirmed item | Head |
|---|---|
| Round-5 blocker `DELIVERED_QUIT_THEN_EXIT_1` | stop kind **ABSENT**, `complete=unknown` |
| Round-5 blocker `DIED_BEFORE_DISPOSE_RETRY` | stop kind **ABSENT**, `complete=unknown` |
| AC17 control: healthy quit, exit 255, two advances, complete stderr | `clean-quit` / `yes` |
| Exit 255 earns only the STOP KIND | 255 + incomplete stderr -> `clean-quit` / `unknown` |
| The genuine three-clause control | survives explicit Stop AND the Dispose retry -> `abandoned` / `unknown`, quits=2 kills=2 |
| The enumerated set `{0, 255}` - still ENUMERATED, not ranged | `CameraTerminationRecord.cs:106`. Tested across **1, 2, 69, 137, 254, 256, int.MaxValue and -5**: every one gives an ABSENT kind and `unknown`. QA-M4 (admit 1) fires 3 tests; QA-M5 (drop the delivery clause) fires 2. |
| AC3/AC9 timing balance on real hardware | AC3 deltas 0.467s and 0.500s; AC9 still fails the start with no directory |
| The 942 pre-existing tests | unchanged and green inside 947/947 |

`git diff --check origin/main...HEAD` across the full head: **clean**.
ASCII-only: all **57** changed files under `src/`, `tests/`, `docs/` scanned byte-by-byte -
**0** bytes above 0x7F (the scanner was proved to fire on a known-bad byte sequence first;
an earlier `grep -P` attempt errored out in this locale and was discarded as a broken
instrument rather than read as a clean run).
No AI-assistant attribution trailers or footers in any commit on this branch (checked for every
banned marker the repo policy lists).

## 4. The stack above

Merged this head into `origin/issue-36-circular-camera-overlay` (`75e62ad`) in a throwaway
detached worktree with `--no-commit --no-ff`: **no conflicts** (only
`FfmpegCameraRecorder.cs` auto-merged), `dotnet restore` + `dotnet build -c Release`
succeeded with 0 errors and the same 4 pre-existing warnings, and the suite was
**1159 passed, 0 failed, 0 skipped** - matching the developer's claim, verified myself.
I then added my probe to that merged tree and re-ran: **1192 passed, 0 failed** (1159 + my
33 scenarios), so the design property holds after the merge as well. Worktree removed.

## 5. Findings for the Review Gate - not criterion failures

1. **A guard the branch's own tests could not see.** Mutation QA-M10 deletes the `Require`
   precondition on `CameraTerminationRecord.QuitDelivered` and, on the branch as delivered,
   **fired nothing** - 982/982 still green. The handoff's claim that "every observation is
   validated against what the record already knows, and a violation THROWS" was therefore
   true but UNTESTED. It is a guard against a future call site, so no reachable path
   violates it and it is not a defect against any criterion; but it was one edit from
   silently disappearing. This round's QA probe covers it, and with the probe present
   QA-M10 fires exactly that test. If the gate wants that guarantee pinned by the product's
   own suite rather than by a QA artifact, that is a follow-up issue, not a blocker.
2. **A `clean-quit` is still reachable after a refused kill.** If round 1's quit is ignored
   and its kill is REFUSED, and the retry's quit is then answered with 0 or 255, the derived
   kind is `clean-quit`. I judged this correct rather than defective: only ONE round was
   refused so `abandoned` is not earned, the quit really was delivered, and ffmpeg really
   did choose an exit code that means it ran its own exit path. Recording it here because it
   is the one path where a kill was issued and the outcome is still `clean-quit`.
3. **Spelling deviations carried from earlier rounds** (unchanged this round, listed so the
   gate does not have to rediscover them): manifest keys are PascalCase where the issue
   writes lower-camel, and `agenteyes screens` prints `CAMERAS:` where the issue writes
   `Cameras:`. Both match the file's and the command's own existing conventions.
4. **QA litter.** This verification left four real recordings in
   `%USERPROFILE%\Videos\AgentEyes` (223851, 223920, 224028 and the AC6 launcher run). They
   are the AC3/AC5/AC6/AC11/AC17 evidence; I did not delete the user's recordings.

## 6. Recorded honestly

- **One of my own instruments was wrong and was fixed before it was believed, twice.** The
  AC9 camera holder's first two forms never opened the device (PowerShell split the
  `video=HD Webcam eMeet C960` argument); it printed
  `HOLDER NEVER OPENED THE DEVICE - BROKEN INSTRUMENT` and I did not run AC9 until the log
  said `Input #0, dshow, from 'video=HD Webcam eMeet C960':`. And my first AC16 probe leaked
  a process-global `RecordingWorkset` capture claim, which showed up as unrelated
  `Drain_*` failures under mutation; I fixed the probe rather than reading around the noise.
- **Not claimed as passed by running it:** AC1's "no camera attached -> `[]`" and AC2's
  `(none found)`. This machine has two cameras. The enumerator half is RUN
  (`cameras=0 mics=1` on an audio-only listing, with the mic count proving the parser
  matched something in that same input); the two surfaces that read it are code-verified.
- **AC13, AC15 and AC16 are not producible with a physical webcam** (Windows terminates a
  normal process) and are established at the `ICameraProcess` seam - the same route the
  gate used to reproduce them - plus, for AC16, through the real `RestServer` serializer
  over the real internal `RecordStatus`.
- **The build trap was avoided.** The tray app running on this machine at the start was the
  INSTALLED binary from 18:50, older than the round-8 commit at 22:21; nothing it reported
  was used as evidence. Every runtime check ran against
  `D:\ReposFred\agenteyes-qa28-r8\...\bin\x64\Release\...`, and for the REST checks I stopped
  the installed app, started MY build (confirmed by `(Get-Process).Path`), and restored the
  installed app afterwards.

**VERIFIED - all 17 acceptance criteria met.**

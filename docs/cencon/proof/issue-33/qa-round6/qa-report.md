# Issue #33 - QA round 6 (PR #39, head `8e1babe`): FAIL

**Verdict: FAIL - one blocking defect.** The Review Gate's round-2 defects 1 and 3 are genuinely
closed and I could break neither. Defect 2 is closed in one direction and broken in the other: the
resize canary now reaches the log on the ordinary Stop path, and it also reaches the log on EVERY
ordinary stop when nothing at all is unaccounted for. A warning that fires on every recording is not
a report.

Verified against the real running app, in an isolated worktree at the PR head, with a second
worktree built at the round-2 head `c1eb48e` as the known-bad arm for every runtime check. The
developer's report was read as context and not as evidence.

- Gate (build, tests, hashes, environment restoration): `gate.txt`
- Defect 1 runtime probes: `d1-shipped-startstall.txt`, `d1-knownbad-startstall.txt`,
  `d1-shipped-stopstall.txt`, `d1-knownbad-stopstall.txt`, `logstall-shipped-armed.txt`
- Defect 2 runtime probes: `d2-canary-runs.txt`
- Drivers: `qa33r6-lib.ps1.txt`, `qa33r6-d1.ps1.txt`, `qa33r6-logstall.ps1.txt`,
  `qa33r6-hud.ps1.txt`, `qa33r6-main.ps1.txt`

---

## THE BLOCKING DEFECT - the resize canary is now a permanent false alarm

**Where:** `src/AgentEyes.App/HudPreviewSizing.cs:89-101` (`HidePanel` reports the canary itself and
then auto-sizes the window), reached from `src/AgentEyes.App/HudWindow.cs:390-409` (`SetStatus`),
which the stop flow calls **more than once**: `HudWindow.RunOnce` at `HudWindow.cs:375-381`
(`SetStatus("Stopping...")` / `"Saving..."`) and then `MainWindow.xaml.cs:1162-1166`
(`StopProgress.Saving = text => _hud?.SetStatus(text)`) for each staged label.

**Reproduce (measured twice, 2/2, `d2-canary-runs.txt` runs 3 and 4):**

1. Fresh config, `HudPreviewVisible: true` (the shipped default), so the preview panel is up.
2. Start a recording with the main window's REC button. The panel opens at its default 520x400 -
   `hud: preview panel opening at 520x400 (the default)`.
3. **Resize nothing. Touch nothing.**
4. Press the HUD's Stop button.

**Expected:** silence. Nothing was resized, no route was missed. This is precisely the case the
branch's own known-good test asserts:
`HudPreviewSizingOrderTests.AnOrdinaryStopWithNothingUnaccountedFor_SaysNothingAboutMissingRoutes`
(`tests/AgentEyes.Tests/HudPreviewSizingOrderTests.cs:504-519`) -
`Assert.DoesNotContain("A resize route is unaccounted for", appended)`.

**Actual** (`%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-20260829.log`, 09:43:23 - 09:43:29):

```
09:43:23.845 [INFO] hud: status -> Saving...              <- SetStatus #1
09:43:23.878 [INFO] hud: preview panel down; the HUD is back to its pill size, remembering no size
09:43:23.900 [INFO] hud: status -> Saving video...        <- SetStatus #2
09:43:23.902 [WARN] hud: the HUD ended up at 260x52 but the last size anything attributed to a
                    person was 520x400. A resize route is unaccounted for (issue #33, AC7): ...
09:43:23.902 [INFO] hud: preview panel down; the HUD is back to its pill size, remembering no size
09:43:29.885 [INFO] hud: status -> Saving audio...        <- SetStatus #3
09:43:29.886 [WARN] hud: the HUD ended up at 260x52 but the last size anything attributed to a
                    person was 520x400. A resize route is unaccounted for (issue #33, AC7): ...
09:43:29.887 [INFO] hud: preview panel down; the HUD is back to its pill size, remembering no size
```

**Mechanism.** `260x52` is the HUD's own pill size. The FIRST `HidePanel` is correct: it compares
520x400 against the accounted 520x400, says nothing, and then does
`window.SizeToContent = SizeToContent.WidthAndHeight`, which collapses the window to the pill.
`HudSizeMemory._accountedWidth/_accountedHeight` still hold 520x400
(`HudSizeMemory.cs:136-142` - only `NoteOpenedAt` and `RecordUserResize` ever move them, and
`HidePanel` calls neither). So the SECOND and THIRD `HidePanel` calls compare the pill's 260x52
against 520x400 and report a missing resize route. The canary is reporting the fix's own auto-size
as if a person had resized the window by an unknown route.

**Why this is blocking, not log noise.**

1. **The instrument is now uninformative.** In run 1 of `d2-canary-runs.txt` a genuine unaccounted
   980x640 produced the warning at 09:36:20.953 - and then the same warning text appeared twice more
   at 09:36:20.988 and 09:36:26.495 for 260x52, in the same stop. A signal that is present on every
   stop cannot distinguish "a route was missed" from "a recording ended." AC7's completeness claim
   is not closed by this; it is inverted. The Gate's round-2 finding was that the canary "reports to
   nobody"; it now reports to everybody, always.
2. **It is the same defect shape the Gate rejected twice.** The committed test's own comment says
   `rig.StopRecording()` is "exactly what `HudWindow.SetStatus` does"
   (`HudPreviewSizingOrderTests.cs:489, 699-706`). It is what ONE call does. The rig calls
   `HidePanel` once; production calls it three times per stop - visible as three
   `hud: preview panel down` lines in EVERY run in `d2-canary-runs.txt`, on both builds. Round 1's
   defect was a canary the production caller dropped; round 2's was a rig that captured a return
   value the production caller dropped; this is a rig that makes one call where production makes
   three. The suite is 1135/1135 green over it.
3. **It is a regression introduced by this round.** On the round-2 head the same sequence logs
   nothing at all (`d2-canary-runs.txt` run 2), because `SetStatus` discarded the value. This round
   turned that silence into a WARN on every recording stop with the preview panel up - which is the
   default configuration.

**Not asserted here:** that persistence itself is wrong. It is not. In every run the HUD correctly
refused to remember an unattributed size (`remembering no size`), which is the behaviour AC7 wants.
The defect is in the completeness instrument the Gate asked for, and in the test that is supposed to
hold it shut.

---

## Defect 1 - preview I/O on the recording's start and stop paths: FIXED, verified with real stalls

The Gate was explicit that round 5's injected eight-second write stalled only `_writeFrame` while
the real `RemoveFrameFile` and the shared logger ran on healthy local paths. Nothing here is
injected. `%LOCALAPPDATA%\AgentEyes\preview` was replaced with a **directory symbolic link onto a
blackholed UNC path** (`mklink /D ... \\<ip>\share`, a routed public address that silently drops
SMB), so the redirector sits in a TCP connect and `Directory.CreateDirectory` / `File.Exists` /
`File.Delete` on the real preview path neither return nor throw. Measured independently before the
runs: **21052 ms** for a single `Directory.CreateDirectory` on such a link.

Each run used a **fresh, never-contacted blackhole IP**, because the SMB client caches an
unreachable server and a warmed path does not stall. No pre-probe was taken for that reason; the
instrument check is the known-bad arm, run on an equally fresh IP.

### 1a. Recording START with the preview path stalled

| build | `POST /record/start` | reached `recording` | files | evidence |
|---|---|---|---|---|
| round-2 head `c1eb48e` | **23.57 s** | yes, after the stall | 16.03s / 16.27s, `CameraComplete: yes` | `d1-knownbad-startstall.txt` |
| PR head `8e1babe` | **5.47 s** | yes | 14.9s / 15.2s, `CameraComplete: yes` | `d1-shipped-startstall.txt` |

Known-bad, the stall on the start thread, in its own log:

```
09:25:56.391 [INFO] [PreviewTap] TryCreate: track=screen frame=...\preview\screen.jpg
09:26:17.429 [INFO] [PreviewTap] TryCreate: track=screen ready          <- 21.04 s later
```

Shipped, the same path, bounded and reported:

```
09:27:04.900 [INFO] [PreviewTap] TryCreate: track=screen frame=...\preview\screen.jpg
09:27:06.906 [WARN] [PreviewChores] Prepare for ...\preview\screen.jpg did not finish within
                    2000ms. The preview filesystem is not answering, so this recording carries on
                    without waiting for it (issue #33, AC10).
09:27:06.907 [WARN] [PreviewTap] TryCreate: no preview for the screen track - ... The recording is
                    unaffected and proceeds without a preview.
09:27:08.916 [INFO] [RecordingService] StartVideo: preview armed=True screenTap=False cameraTap=False
```

The 5.47 s is two tracks x the 2000 ms budget (they share one chores worker, so the second queues
behind the first) plus the normal ~1.5 s start. Bounded, and the exact log lines the handoff
promised.

### 1b. Recording STOP with the preview path stalled mid-recording

Started healthy so both taps and both publisher threads really exist, then the preview directory was
replaced mid-recording by a link onto a fresh blackhole, then Stop.

| build | `POST /record/stop` | back to `idle` | files | evidence |
|---|---|---|---|---|
| round-2 head `c1eb48e` | **24.31 s** | yes, after the stall | 19.07s / 19.27s, `CameraComplete: yes` | `d1-knownbad-stopstall.txt` |
| PR head `8e1babe` | **8.92 s** | yes | 20.2s / 20.8s, `CameraComplete: yes` | `d1-shipped-stopstall.txt` |

Known-bad: `Dispose` at 09:28:17.789, publisher-join warning at 09:28:20.797, then an **18.04 s gap
on the stop thread** before 09:28:38.837 - the synchronous `FlushNotes` + `RemoveFrameFile` on the
wedged path, exactly as the Gate described.

Shipped: `Dispose(screen)` 09:29:33.170 -> `[PreviewChores] Remove ... did not finish within 2000ms`
09:29:35.173 -> `Dispose(camera)` -> the same at 09:29:37.188. Four seconds of bounded budget for two
tracks, no unbounded call on the stop thread, and the service returned to `idle` in 8 ms after the
response.

Worst case is still bounded and I state it rather than leave it implied: per tap, publisher join
3000 ms + pump join 3000 ms + (only when no publisher was ever started) a 2000 ms `Remove` budget,
x2 taps.

### 1c. The shared logger's own path stalled

`%LOCALAPPDATA%\AgentEyes\logs` was parked aside and replaced by a link onto a blackholed UNC for the
whole recording, and swapped onto a **second** never-contacted blackhole immediately before Stop
(`logstall-shipped-armed.txt`). Start 1.73 s, stop 3.74 s, back to `idle` in 8 ms, 15.4s / 15.7s
files, `CameraComplete: yes` - indistinguishable from healthy.

**The limit of that check, stated rather than discovered:** I could not prove that a *fresh*
21-second logger stall landed inside the stop window itself. The SMB client's unreachable-server
cache makes the hang a one-shot per server, and a pre-probe would consume it. So this run shows the
app tolerating a dead logger path end to end; it does not show it tolerating a logger stalled at the
exact instant of Stop. What closes that half is structural and I checked it myself:
`PreviewLog.Say` (`src/AgentEyes.Core/Preview/PreviewLog.cs:101-114`) is a count check, a
`ManualResetEventSlim.Reset`, a `ConcurrentQueue.Enqueue` and an `AutoResetEvent.Set` - no I/O and
no lock the appender could hold - and the appender thread is created in the type initializer
(`PreviewLog.cs:63`), so `Loop` is not reachable from any caller's call graph.

I also grepped the preview-owned code for the shared logger directly, and it is clean:
`HudPreviewSizing.cs`, `PreviewFrameFeed.cs`, `HudUserResize.cs` and `AgentEyes.Core/Preview/*` have
**no** `Log.Info/Warn/Error` outside `PreviewLog`'s own appender. The remaining direct `Log.` calls
in `HudWindow.cs` are the non-preview lines (`hud: status ->`, `hud: discard clicked`,
`hud: saving position`, `hud window styles`), unchanged by this branch. Note in passing, not as a
defect: `SetStatus`'s own `Log.Info` at `HudWindow.cs:392` is still a synchronous shared-logger
append on the dispatcher that serves Stop. It pre-dates this feature and the branch does not touch
it; the developer's carve-out to a separate issue is honest.

---

## Defect 3 - the config writer could revert a newer setting: FIXED

I could not construct an ordering in which an older snapshot lands last, and the reason is
structural rather than a passing race:

1. `Config.Save()` no longer writes. It is `Writer.WriteNow(Serialize(), 2000)`
   (`src/AgentEyes.App/Config.cs:120-125`), and `WriteNow` is `Queue(text)` then `Flush(ms)`
   (`src/AgentEyes.App/BackgroundFileWriter.cs:132-136`). The wait is what makes it blocking; it is
   no longer what makes it write.
2. **There is exactly one writer.** I grepped the whole product: `Config::WriteJson` is named in
   exactly one place, the field initializer that hands it to the `BackgroundFileWriter`
   (`Config.cs:79`). Nothing else in `src/` writes `config.json`. Every save path in the app -
   `MainWindow.xaml.cs:432,449,719,1261`, `ManagePresetsDialog.cs:169,180`,
   `PluginManagerWindow.cs:179`, `SettingsDialog.cs:169`, `TrayHost.cs:111,147`, and the HUD's
   `HudWindow.cs:704,751` - goes through it.
3. **One slot, one thread, so last-queued is last-written.** `Queue` is a single
   `Interlocked.Exchange(ref _pending, text)` (`BackgroundFileWriter.cs:101-109`); the loop takes
   `_pending` and writes it (`:175-191`). A later `Queue` either replaces the earlier text before the
   loop takes it (the earlier one is never written and is counted in `Superseded`), or arrives after
   it was taken, in which case the loop writes the earlier one and then loops and writes the later
   one. There is no interleaving in which an earlier-queued text lands after a later-queued one.

The branch's test for this is genuinely fail-closed and I read it rather than trusting the summary:
`HudResponsivenessTests.ANewerBlockingSaveAfterAQueuedOne_LandsLast_AndTheOldShapeDoesNot`
(`tests/AgentEyes.Tests/HudResponsivenessTests.cs:142-200`) asserts BOTH arms in one test - the
known-bad direct-write shape must end up holding `"older queued snapshot"` and the shipped shape
`"newer blocking snapshot"` - and it fails rather than passes if the writer never took the older
snapshot ("the run measured nothing").

**Residual, stated rather than hidden:** the snapshot is serialised on the caller's thread before
`Queue`, so "last queued" equals "last serialised" only if two savers cannot interleave between
`Serialize()` and `Queue()`. In this app they cannot: every `Config.Save` /
`SaveWithoutBlockingTheUiThread` call site above is a WPF dispatcher (or WinForms tray) handler on
the one UI thread. Live sanity check on the real app: after a HUD preview toggle and stop, the
background-queued snapshot landed correctly (`HudLeft=2247 HudTop=219` on disk). I did not
reproduce a live race, because without a way to stall `config.json`'s write a live run could pass by
luck, and a check that can pass without the condition being true is not a check.

---

## The four extra instances the developer audited: all four confirmed

| Instance | Confirmed |
|---|---|
| `HudPreviewSizing.ShowPanel` / `HidePanel` | `HudPreviewSizing.cs` - no `Log.` remains; both lines are `PreviewLog` |
| `PreviewFrameFeed.Want` / `Start` / `Dispose` / reader loop | `PreviewFrameFeed.cs` - no `Log.` remains (5 lines converted) |
| `HudUserResize` (five lines) + `HudWindowAutomationPeer.Move` | `HudUserResize.cs` - no `Log.` remains |
| `HudWindow.TogglePreview` / `ChooseMode` / `ChooseCorner` | `HudWindow.cs:466,474,478,485` are `PreviewLog`; the four `Log.` lines that remain are non-preview and pre-existing |

---

## Regression checks - the Gate's confirmed-working list is intact

| Item | Result |
|---|---|
| Build / tests | `Build succeeded. 0 Error(s)`; `Passed: 1135, Failed: 0` (`gate.txt`) |
| AC1 - panel opens at 520x400, Image not zero-sized | PASS - `HUD rect W=520 H=400`, `IMAGE rect=909,247,490,276` (`d2-canary-runs.txt` run 4) |
| AC3/C1 - one DirectShow open | PASS - no diff in `FfmpegArgs.cs` / `CameraCapture` on this branch; `CameraComplete: yes` on all 7 recordings made |
| AC5 - manifest | PASS - manifests read on every run; `DurationSeconds`, `CameraStopKind: clean-quit`, `CameraComplete: yes` |
| AC6/C5 - `WDA_EXCLUDEFROMCAPTURE` | PASS - `HudWindow.cs:760-785` unchanged |
| AC10 - a broken/stalled preview never harms the recording | PASS - four stalled runs, every one a valid pair with `CameraComplete: yes` |
| AC11 - no regression with the preview never enabled | PASS - preview-disarmed start/stop unchanged |
| Opt-in per recording | Not failed - the Gate ruled it honest and acceptable |
| Drain isolated from publishing and logging | PASS - `PreviewTap.Drain` reaches only framing and `Offer`; `Offer` is an interlocked swap plus event set |

## What I could not close, said plainly

- A fresh 21-second stall of the SHARED logger landing exactly inside the Stop window (section 1c).
  Closed structurally, not behaviourally, and the structural argument is stated above.
- A live reproduction of the config write-ordering race (defect 3). Closed structurally and by the
  branch's two-armed test, not by a live run.

## Verdict

**FAIL (`flow:qa-failed`).** Defects 1 and 3 are closed and survived real stalls and a known-bad
control. Defect 2 is not: the canary now fires on every ordinary stop with the preview panel up,
including stops where nothing was resized, and the committed known-good test that was meant to
prevent exactly that does not exercise the production caller.

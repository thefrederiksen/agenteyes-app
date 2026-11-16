# Issue #116 - QA, LINUX: criterion 11 as amended (clean quit), and #142 row 10 re-confirmed

A short, targeted QA pass. It checks the macOS quit-hang fix (`SingleInstance.Dispose`, commit 729059e)
in the running app on Linux, because the Mac cannot reach the tray menu right now. It also re-confirms
#142 Linux row 10, which passed on c80cb9d, before `SingleInstance.cs` changed.

| | |
|---|---|
| Machine | devlinux - Ubuntu, GNOME on Xorg, DISPLAY=:0, 32 cores |
| Date | 2026-11-15, 10:25 - 10:31 EST |
| Commit under test | `issue-116-desktop-app` (PR #141) at **0e1b88998feca76e6244e93a83297819ff667c35** |
| App under test | `AgentEyesDesktop`, published from this commit into a scratch folder (not installed). `AgentEyesDesktop.dll` sha256 `5e4509e729ec68d296eb7a9c2f4d50d9fd2294ffebf3ffc80e3e1a1576f1d51b`. The fix's own log string, `could not wake the accept thread`, is in that dll: 1 UTF-16LE hit |
| Load | 1-min load 2.40 at the start, 2.83 at the first launch, 2.00 - 2.21 during runs 2-4. 32 cores |
| Test gate | `dotnet test tests/AgentEyes.Tests.Linux -c Release`: **Passed 279, Failed 0, Skipped 0** |
| Evidence | `linux-criterion11-evidence.txt`: the tray-quit helper, the console output of all four runs, and every log line the app wrote in this session |

## Summary

| Check | Result |
|---|---|
| Criterion 11 as amended: a second launch hands off and exits, AND the first instance exits cleanly and removes its socket | **PASS**, 4 of 4 runs |
| The wake-up connection is not taken for a command | **PASS**, 4 of 4 runs: one empty `Accept: ''` per quit, nothing acted on |
| #142 row 10 at 0e1b889 | **STILL PASSES** (addendum added to `../issue-142/qa-report-linux.md`) |
| #143 log overwrite | **Seen, 1 of 16** handoff lines malformed. Not failed, as instructed |

## How Quit was reached: the real tray menu, not a kill

`App.Quit` -> `desktop.Shutdown()` (`App.axaml.cs:100`) is reached only from the tray menu's Quit entry.
I clicked that entry through the menu the app itself publishes: the StatusNotifierItem's
`com.canonical.dbusmenu` object. The script reads the item id from `GetLayout`, where the label is
`'Quit AgentEyes (the recorder keeps running)'`, and sends `Event(id, "clicked")`. That is the same call
GNOME's tray extension makes when someone clicks the entry. No focus was taken and no input was faked.
The app logged `[App] Quit (the recorder service keeps running)` each time, so the click went through
the real handler. No signal was sent to any process.

## Criterion 11 - run 1, in full

The first instance was started with `--tray`. The second launch had no arguments.

Socket path `/run/user/1000/agenteyes-app.sock`:

```
before the first launch   ls: cannot access '/run/user/1000/agenteyes-app.sock': No such file or directory
while running             srwxrwxr-x 1 soren soren 0 2026-11-15 10:27:39.346944963 -0500 /run/user/1000/agenteyes-app.sock
                          ss -xlp: u_str LISTEN ... /run/user/1000/agenteyes-app.sock ... users:(("AgentEyesDeskto",pid=132923,fd=48))
after the tray Quit       ls: cannot access '/run/user/1000/agenteyes-app.sock': No such file or directory
                          ss -xlp: no listener on that path
process                   exit=0 at 10:27:58.000 (the wrapper shell's $?); kill -0 132923 failed at 10:27:58.019
```

The log of the first instance (pid 132923) and of the second launch (pid 133253):

```
10:27:39.332 [pid 132923] [INFO] [Program] Main: AgentEyes desktop app starting on linux (tray=True, pid=132923)
10:27:39.348 [pid 132923] [INFO] [SingleInstance] TryAcquire: listening on /run/user/1000/agenteyes-app.sock
10:27:47.474 [pid 133253] [INFO] [Program] Main: AgentEyes desktop app starting on linux (tray=False, pid=133253)
10:27:47.491 [pid 133253] [INFO] [SingleInstance] TryAcquire: another instance is running - sent it 'show'
10:27:47.491 [pid 132923] [INFO] [SingleInstance] Accept: 'show'
10:27:47.491 [pid 132923] [INFO] [App] Run: show
10:27:47.492 [pid 132923] [INFO] [App] ShowWindow
10:27:48.235 [pid 132923] [INFO] [MainWindow] Opened
10:27:56.310 [pid 132923] [INFO] [App] window closed to the tray
10:27:57.867 [pid 132923] [INFO] [App] Quit (the recorder service keeps running)
10:27:57.924 [pid 132923] [INFO] [SingleInstance] Dispose: socket /run/user/1000/agenteyes-app.sock
10:27:57.924 [pid 132923] [INFO] [SingleInstance] Accept: ''
10:27:57.924 [pid 132923] [INFO] [SingleInstance] Dispose: socket listener closed and /run/user/1000/agenteyes-app.sock removed
10:27:57.925 [pid 132923] [INFO] [Program] Main: exited
```

The second launch exited 0 by itself. Afterwards `wmctrl -lp` showed window `0x02400013 ... 132923 ... AgentEyes`.

Against the three things the amended criterion asks for:

- **`[Program] Main: exited` is present**, at 10:27:57.925, 58 ms after the Quit click.
- **`[SingleInstance] Dispose:` is not the last line.** It is followed by the empty accept, by
  `Dispose: socket listener closed and ... removed`, and by `Main: exited`. On macOS before the fix, the
  `Dispose:` entry was the last line written.
- **The socket is gone.** The listing above shows it present at 10:27:39 and owned by pid 132923, then
  absent after the quit, with no listener in `ss`.

## Criterion 11 - runs 2, 3 and 4

Each run did the same steps. Start the app with no arguments, so its window opens. Close the window to
the tray. Run a second launch with no arguments (`show`). Run four more launches: `--view library`,
`--view record`, `--view library`, `--view record`. Close the window to the tray again. Then click Quit
in the tray menu.

| run | pid | socket while running (ls + ss owner) | second launches | Quit click | `Main: exited` | exit | socket after |
|---|---|---|---|---|---|---|---|
| 2 | 135675 | present 10:28:26.654, pid=135675 | 5, all exit 0 | 10:28:33.981 | 10:28:34.052 | 0 | absent, 0 listeners |
| 3 | 136999 | present 10:28:39.718, pid=136999 | 5, all exit 0 | 10:28:46.838 | 10:28:46.914 | 0 | absent, 0 listeners |
| 4 | 138248 | present 10:28:52.544, pid=138248 | 5, all exit 0 | 10:28:59.925 | 10:29:00.001 | 0 | absent, 0 listeners |

All three runs ended with the same four lines as run 1, for example run 4:

```
10:28:59.925 [pid 138248] [INFO] [App] Quit (the recorder service keeps running)
10:29:00.001 [pid 138248] [INFO] [SingleInstance] Dispose: socket /run/user/1000/agenteyes-app.sock
10:29:00.001 [pid 138248] [INFO] [SingleInstance] Accept: ''
10:29:00.001 [pid 138248] [INFO] [SingleInstance] Dispose: socket listener closed and /run/user/1000/agenteyes-app.sock removed
10:29:00.001 [pid 138248] [INFO] [Program] Main: exited
```

**Criterion 11 as amended: PASS on Linux, 4 of 4.**

## The risk flagged by the lead: the wake-up connection is not taken for a command - PASS

What the lead asked for, checked on this machine for each of the four quits:

- **Every line the instance wrote after `[App] Quit`, with no filter applied:** exactly 4 lines in each
  of the 4 runs. They are `Dispose: socket ...`, `Accept: ''`, `Dispose: socket listener closed ...` and
  `Main: exited`. There is no `[App] Run:`, no `ShowWindow`, no `MainWindow` line and no `RunCommand`.
  The instrument can see those lines when they happen: the same log has 16 `Accept: '<cmd>'` lines
  from real handoffs, each followed by `[App] Run:` and `ShowWindow`.
- **Empty accepts:** 4 in the session, one per quit, each between the two `Dispose:` lines. Nothing
  else came in on the socket during shutdown.
- **No window was raised during the quit.** A watcher sampled `wmctrl -lp` for the quitting pid every
  50 ms across each quit: 120 samples in run 1 and 60 in each of runs 2-4. The count was 0 in every
  sample. The same command counted 1 right after each `show` handoff (the "after second launch:
  mapped=1" lines in the evidence), so it does see this app's window when it is mapped.
- **No WARN or ERROR** from any process in the session, the service included. In particular, the
  fix's `could not wake the accept thread` warning never appeared.

Not covered here: this is Linux, where a blocked `accept()` wakes up on close by itself. So these runs
prove that the waker does no harm on the shared code path (no spurious command, a clean exit, the
socket removed). They cannot prove the waker cures the macOS hang. That proof is the macOS unit test
(mutation M5) and the standalone repro. The clean quit through the tray in the running app on macOS
is still to be done.

## #142 row 10 at 0e1b889 - STILL PASSES

Row 10 asks that a second launch raise the first window and exit. It was run the same way as the
platform report: hide the window first, so that "raised" is something the check can observe. Runs 2-4,
from the evidence file:

```
after close-to-tray: mapped=0
second launch (show) 10:28:29.458
  exit=0
after second launch: mapped=1
10:28:29.556 [pid 135675] [INFO] [SingleInstance] Accept: 'show'
10:28:29.556 [pid 135675] [INFO] [App] Run: show
10:28:29.557 [pid 135675] [INFO] [App] ShowWindow
```

Runs 3 and 4 match (10:28:42.400 and 10:28:55.484). In run 1 the window had never been shown
(`--tray`), and the second launch mapped it (`MainWindow ctor`, then `Opened`). Across all four runs
there were 16 second launches, and every one exited 0 by itself. `pgrep` found no `AgentEyesDesktop`
afterwards. I added a short addendum to `../issue-142/qa-report-linux.md`. Nothing else in that report
was changed.

## #143 (two processes overwrite each other's log bytes) - seen, 1 of 16

Of the 488 lines this session added to `AgentEyes-20261115.log`, 487 are well formed. One is not:

```
10:28:57.110 [pid 138720] [INFO] [SingleInstance] TryAcquireSocket: /run/user/1000/agenteyes-app.sock command='view:record'
10:28:57.122 [pid 138248] [INFO] [SingleInstance] Accept: 'view:record'
stance is running - sent it 'view:record'
10:28:57.122 [pid 138248] [INFO] [App] Run: view:record
```

pid 138720's `sent it` line lost its start in the same millisecond as pid 138248's `Accept` line. That
is the pattern #143 describes. Rate here: **1 of 16** second-launch `sent it` lines (15 intact). In
the platform sweep it was 3 of 16. Nothing was failed for it.

## State of devlinux after this pass

- **Recorder service:** not touched. Still pid **28960** (`systemctl --user` MainPID), the #113 branch
  build: `agenteyes.dll` sha256 `77586071...298284e`. `/health` returns `ok: true, platform: linux`.
- **Desktop app:** to free the default socket path, the pre-session `AgentEyesLinux --tray` (pid
  29068) was quit at 10:27:26 by the same tray-menu click. It exited and removed its socket. Afterwards
  I relaunched it from `~/.local/lib/agenteyes-app/AgentEyesLinux --tray`. It is pid **140228** and owns
  `/run/user/1000/agenteyes-app.sock`. Nothing under `~/.local/lib` was changed.
- **My processes:** the four first instances (132923, 135675, 136999, 138248) quit through the tray
  menu. All 16 second launches exited by themselves. `pgrep AgentEyesDeskto` found none. No process of
  mine is left running.
- **`~/Videos/AgentEyes`:** nothing deleted and nothing recorded. The app only listed it (77 recordings).

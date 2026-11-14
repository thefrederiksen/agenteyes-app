# QA report - issue #123 on Linux (devlinux) - independent live verification

- Seat: QA, devlinux (Linux 6.8.0-147, dotnet SDK 8.0.134, ffmpeg /usr/bin/ffmpeg, DISPLAY :0)
- Branch: `issue-123-bodyless-post-411`, commit `9b0ec68` (code commit `668a1ac`)
- Date: 2026-11-14, times are local (EST)
- Scope: the live behaviour of the framing guard on .NET-on-Unix, the only platform where it is
  reachable. The developer's handoff was treated as a claim, not as evidence.

**Verdict: PASS on Linux.** Every check below has a quoted response, a measured number or a named
artefact. One test-quality follow-up and one out-of-scope recorder observation are noted at the end.
Neither blocks.

## 0. Which build is live (established, not trusted)

The service another seat left running (PID 3526377, up since 09:55:00) was replaced by my own build:

```
git checkout issue-123-bodyless-post-411  -> HEAD 9b0ec6864e926a1a387b747bbe0d40106782b9d8, clean tree
10:23:32 bash scripts/linux/install.sh   (exit 0, 10:23:52)
[OK] built /tmp/tmp.2FUAu6JmTx/agenteyes
[OK] installed /home/soren/.local/lib/agenteyes, linked /home/soren/.local/bin/agenteyes, unit ...agenteyes.service
[OK] enabled at login (graphical-session.target)
[OK] installed the app (/home/soren/.local/lib/agenteyes-app) ...
[OK] service is up: {"ok":true,"app":"AgentEyes","host":"headless","platform":"linux"}
[OK] signed in as soren@centerconsulting.com
```

Tying the running process to that build:

| Evidence | Value |
|---|---|
| installed `~/.local/lib/agenteyes/agenteyes.dll` mtime | 2026-11-14 10:23:38.89 (during my install run) |
| sha256 of that dll | `240e44c2f05c08f1c7fc6de0167265629759e1a1a47d55dd9dabdc862dd596d1` |
| new-code string `REJECTED before any effect` in that dll | present (1 hit, UTF-16) - this string exists only in the PR's code |
| service MainPID / started / exe | 35489, Sat Nov 14 10:23:50, `/home/soren/.local/lib/agenteyes/agenteyes` |
| service log | every log line quoted below is `[pid 35489]` and includes the PR's `REJECTED before any effect` / `200 answer written` messages, which `main` does not have |

`GET /version` answers `1.11.11` on both builds, so it cannot tell them apart. PID, mtime, and the
new log strings can.

## 1. Bodyless stop while recording: rejected, and the recording keeps running

Recording started with `-H 'Content-Type: application/json' -d '{"source":"none"}'` at 10:25:08 -> 200.

```
[10:25:12.262] GET /status -> State= recording Elapsed= 3.24 Dir= /home/soren/Videos/AgentEyes/2026-11-14_102508_video
[10:25:12.744] curl -i -X POST http://127.0.0.1:7882/record/stop
HTTP/1.1 411 Length Required
Content-Type: text/html
Server: Microsoft-NetCore/2.0
Content-Length: 24
Connection: close

<h1>Length Required</h1>
[10:25:12.755] GET /status immediately after -> State= recording Elapsed= 3.51 Dir= /home/soren/Videos/AgentEyes/2026-11-14_102508_video
```

Service log for that request:
```
10:25:12.752 [pid 35489] [INFO] [HeadlessControlServer] POST /record/stop
10:25:12.752 [pid 35489] [WARN] [HeadlessControlServer] POST /record/stop REJECTED before any effect: no Content-Length and no chunked body (411 Length Required). ...
10:25:12.752 [pid 35489] [INFO] [HeadlessControlServer] POST /record/stop: the listener had already answered 411 itself (ObjectDisposedException); no action taken
```

| Expected | Actual | Result |
|---|---|---|
| 411, and /status still `recording` with the same Dir | 411, and /status `recording`, same Dir, elapsed still counting (3.24 -> 3.51 -> 6.03 s at 10:25:14.846) | PASS - response and state agree |

## 2. Bodyless start does NOT start a recording (the worst case of the defect)

```
[10:24:23.025] ls -1 ~/Videos/AgentEyes | tail -5   (count=43)
2026-11-14_093312_video
2026-11-14_093418_video
2026-11-14_095115_video
2026-11-14_095518_video
2026-11-14_095541_video
[10:24:23.033] curl -i -X POST http://127.0.0.1:7882/record/start
HTTP/1.1 411 Length Required
Server: Microsoft-NetCore/2.0
<h1>Length Required</h1>
[10:24:23.044] GET /status -> State= idle Dir= None
[10:24:25.084] ls -1 ~/Videos/AgentEyes | tail -5   (count=43)
2026-11-14_093312_video
2026-11-14_093418_video
2026-11-14_095115_video
2026-11-14_095518_video
2026-11-14_095541_video
```
```
10:24:23.041 [pid 35489] [INFO] [HeadlessControlServer] POST /record/start
10:24:23.041 [pid 35489] [WARN] [HeadlessControlServer] POST /record/start REJECTED before any effect: ...
10:24:23.044 [pid 35489] [INFO] [HeadlessControlServer] POST /record/start: the listener had already answered 411 itself (ObjectDisposedException); no action taken
```
There is no `StartFrom:` line for that request. A dispatched start always logs one; see 10:25:08 and
10:25:32, where it logs `StartFrom: mode=video screen=1 source=None ...`.

A bodyless start sent while a recording was running (10:25:12.832) was also rejected: 411,
`REJECTED before any effect`, and the recording carried on with the same Dir.

| Expected | Actual | Result |
|---|---|---|
| 411, /status idle, no new directory | 411, /status idle, directory count 43 -> 43, same last 5 entries, no `StartFrom` in the log | PASS |

## 3. All seven POST routes (found in the code, not taken from the handoff)

`src/AgentEyes.Core/Headless/HeadlessControlServer.cs`:

| # | Route | Where | Bodyless result live (time) | Effect check |
|---|---|---|---|---|
| 1 | POST /account/refresh | :133 | 411, REJECTED (10:24:25.115) | no `[AccountState] Refresh:` line follows it. Control: the framed refresh at 10:25:49.800 logs `[AccountState] Refresh: signedIn=True` at .859 |
| 2 | POST /record/start | :140 | 411, REJECTED (10:24:23.041 idle; 10:25:12.874 while recording) | no new dir, still idle (check 2) |
| 3 | POST /record/shot | :144 | 411, REJECTED (10:24:25.104 idle; 10:25:12.829 while recording) | while recording: shots/ 0 -> 0. Control: a framed shot (`-d ''`) at 10:25:35.897 wrote `shots/00m03s.png` (0 -> 1), so the 0 is a real measurement |
| 4 | POST /record/stop | :147 | 411, REJECTED (10:25:12.752) | still recording (check 1) |
| 5 | POST /recordings/{id}/rename | :198 | 411, REJECTED (10:24:25.139) | title of `2026-11-14_095541_video` still `None` |
| 6 | POST /recordings/{id}/subtitles | :207 | 411, REJECTED (10:24:25.151) | no `AnswerFromWorker: subtitles` line, folder unchanged |
| 7 | POST /recordings/{id}/walkthrough | :221 | 411, REJECTED (10:24:25.127) | no `AnswerFromWorker: walkthrough` line; folder still `manifest.json recording.mp4 recording.mp4.ffmpeg.log shots thumb.jpg` 5 s later, no `walkthrough.html` |

How I enumerated them: the `case ("POST", ...)` entries in the switch (:133-:147) give 4, and
`RecordingChange` (:153, reached for POST/DELETE under `/recordings/`) gives `rename`, `subtitles` and
`walkthrough` for POST (:194 `method != "POST" || seg.Length != 2`). DELETE `/recordings/{id}` (:188)
is not a POST. By design it is not guarded (DELETE is outside the guard's POST/PUT contract), and I did not send it.
Seven POST routes, which matches the Windows seat's count. PUT /record/start was also sent bodyless:
411, `PUT /record/start REJECTED before any effect` (10:24:25.163).

Result: PASS, 7 of 7.

## 4. A bodied stop still works, and the file is real

```
[10:25:18.305] curl -i -X POST -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:7882/record/stop
HTTP/1.1 200 OK
Content-Type: application/json
Content-Length: 188

{
  "Dir": "/home/soren/Videos/AgentEyes/2026-11-14_102508_video",
  "File": "/home/soren/Videos/AgentEyes/2026-11-14_102508_video/recording.mp4",
  "DurationSeconds": 9.06,
  "Shots": 0
}
[10:25:20.734] GET /status -> State= idle
```
```
10:25:20.721 [pid 35489] [INFO] [HeadlessControlServer] Stop: /home/soren/Videos/AgentEyes/2026-11-14_102508_video saved; post-processing running in the background
10:25:20.730 [pid 35489] [INFO] [HeadlessControlServer] POST /record/stop: 200 answer written (188 bytes)
```
ffprobe on `recording.mp4` (113343 bytes): `codec_name=h264 width=1920 height=1080 nb_read_frames=340 duration=11.333333`.

| Expected | Actual | Result |
|---|---|---|
| 200, recording saved, a real file | 200, then idle, h264 1920x1080, 340 frames, 11.33 s | PASS |

## 5. Content-Length: 0 (curl -d '') is still served

```
[10:25:32.104] start (-d '{"source":"none"}') -> HTTP/1.1 200 OK ; Dir 2026-11-14_102532_video
[10:25:35.897] curl -i -X POST -d '' http://127.0.0.1:7882/record/shot
HTTP/1.1 200 OK
{ "file": "/home/soren/Videos/AgentEyes/2026-11-14_102532_video/shots/00m03s.png" }      shots/ 0 -> 1
[10:25:39.234] curl -i -X POST -d '' http://127.0.0.1:7882/record/stop
HTTP/1.1 200 OK
Content-Length: 188
{
  "Dir": "/home/soren/Videos/AgentEyes/2026-11-14_102532_video",
  "File": "/home/soren/Videos/AgentEyes/2026-11-14_102532_video/recording.mp4",
  "DurationSeconds": 6.46,
  "Shots": 1
}
[10:25:49.017] GET /status -> State= idle
[10:25:49.791] curl -i -X POST -d '' http://127.0.0.1:7882/account/refresh -> HTTP/1.1 200 OK
```

| Expected | Actual | Result |
|---|---|---|
| `-d ''` is framed and served | shot 200 + PNG written, stop 200 + saved + idle, account/refresh 200 | PASS (see observation B about this file's length) |

## 6. Test quality: can the new tests fail?

I disabled the guard at its call site only, leaving the pure predicate `IsUnframedBody` as it was, so
its own unit tests do not inflate the count:

```
-            if (IsUnframedBody(method, ctx.Request.Headers["Content-Length"], ctx.Request.Headers["Transfer-Encoding"]))
+            if (false && IsUnframedBody(method, ctx.Request.Headers["Content-Length"], ctx.Request.Headers["Transfer-Encoding"]))
```

`dotnet test tests/AgentEyes.Tests.Linux -c Release` (10:27:15):
```
Failed AgentEyes.Tests.Linux.HeadlessControlServerFramingTests.RecordStop_NoBodyNoContentLength_Rejected411BeforeHandlerAndStatusAgrees
Failed AgentEyes.Tests.Linux.HeadlessControlServerFramingTests.OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(path: "/recordings/some-id/rename")
Failed AgentEyes.Tests.Linux.HeadlessControlServerFramingTests.OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(path: "/account/refresh")
Failed AgentEyes.Tests.Linux.HeadlessControlServerFramingTests.OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(path: "/record/shot")
Failed AgentEyes.Tests.Linux.HeadlessControlServerFramingTests.OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(path: "/recordings/some-id/walkthrough")
Failed AgentEyes.Tests.Linux.HeadlessControlServerFramingTests.OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(path: "/recordings/some-id/subtitles")
Failed AgentEyes.Tests.Linux.HeadlessControlServerFramingTests.RecordStart_NoBodyNoContentLength_Rejected411AndNoRecordingStarted
Failed!  - Failed:     7, Passed:   107, Skipped:     0, Total:   114
```
All 7 fail with `Assert.Equal() Failure: Expected: 0  Actual: 1` (the dispatch counter).

Restored with `git checkout -- src/AgentEyes.Core/Headless/HeadlessControlServer.cs`: `git diff` was 0
lines, and line 116 reads `if (IsUnframedBody(...))` again. Re-run (10:27:28):
```
Passed!  - Failed:     0, Passed:   114, Skipped:     0, Total:   114
```

| Expected | Actual | Result |
|---|---|---|
| the guard-off build fails the regression tests, and the restored build is green | 7 named failures with the guard off (one per bodyless route); 114/114 after restoring | PASS - the developer's claim of 7 is reproduced |

## 7. Linux gate on the branch

```
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64
  Build succeeded.   1 Warning(s) (CS8602 AlwaysOnEngine.cs:901, pre-existing, not in this diff)   0 Error(s)
dotnet test tests/AgentEyes.Tests.Linux -c Release   (10:26:23 - 10:27:02)
  Passed!  - Failed: 0, Passed: 114, Skipped: 0, Total: 114
```
114 = 94 before + 20 new framing tests (11 predicate cases + stop + start + 5 other routes + bodied
stop + Content-Length 0). Result: PASS.

## Observations (non-blocking)

**A. Test-quality follow-up.** Each of the 7 tests fails ONLY through the internal
`RequestsDispatched` counter. The effect assertions that sit beside it cannot discriminate.
`RecordStop_...StatusAgrees` runs against an idle server, so a dispatched stop leaves it `idle` too.
`RecordStart_...NoRecordingStarted` sends no body, so a dispatched start defaults to `source=mic` with
no mic and fails with 400 `mic is required for this source` (seen live at 10:24:46), which also leaves
it `idle`. The counter is a sound proxy, because it is incremented on the line right after the guard
and before the route switch (:121). But the unit tests prove "not dispatched", not "no effect". The
effect is proven only live, by checks 1-3 above. A cheap hardening would be a stop test against a
service that is actually recording. That is a suggestion, not a defect.

**B. Recorder, out of scope (not the framing guard).** The `-d ''` recording `2026-11-14_102532_video`
reported `DurationSeconds 6.46`, but the file is `nb_read_frames=43 duration=1.433333`. The log has
`[FfmpegRecorder] Stop: pipeline did not reach 6860ms within 5s (reached 0ms) - the end of this
recording may be truncated` (10:25:44.256). The stop request was framed, dispatched and answered
correctly. The capture pipeline itself was short. It looks related to #113 (stalled capture). I am
handing it to the lead rather than filing it, as instructed.

**C. #126 seen as expected.** The silent recording logged `[PostRecording] stage package FAILED ...
ffmpeg failed during extract audio (exit 234)` (10:25:51.429, and 10:25:21.579 for 102508). This is
already filed as #126.

**D. One void attempt.** My first bodied start at 10:24:46 used `-d '{}'` and got 400 `mic is required
for this source`, so the bodyless stop/shot I sent at 10:24:49 ran against an idle service. Those
results are not counted. Checks 1 and 4 were redone with `{"source":"none"}` and are the ones reported.

## State left behind

- Service `agenteyes` active, MainPID 35489, my branch build (installed 10:23:38), `/health` ok,
  `/status` idle.
- Working tree: product code restored (0-line diff). Only this report was added.
- Nothing under `~/Videos/AgentEyes` was deleted. Two new recordings from this pass:
  `2026-11-14_102508_video` and `2026-11-14_102532_video`. `2026-11-14_093032_video` (#122 evidence) is
  untouched.

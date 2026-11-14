# Issue #123 - QA report, Windows seat

Issue: #123 `[ControlApi] A bodyless POST /record/stop stops the recording and then answers 411`
PR: #124, branch `issue-123-bodyless-post-411`, head `668a1ac`.
Machine: SORENLAPTOP, Windows 11 Home 10.0.26200, PowerShell 5.1.26100.9818, curl from Git Bash.
dotnet SDKs 9.0.306 / 10.0.103 / 10.0.301; .NET 8 runtimes 8.0.6 / 8.0.7 / 8.0.21.
Date: 14 November 2026 (times below are local, UTC-5).

## Verdict

**PASS on Windows, with criterion 5 PARTIAL by coverage, not by defect.**

- Every criterion I can measure on Windows passes, with quoted responses and log lines below.
- Criterion 5 (all three platforms) is only partly covered. Windows is QA-verified (this report).
  Linux is **developer-verified only**: the Linux evidence in `handoff.md` was produced by the
  developer, so it is not independent verification. macOS is **unverified**: nobody has run it; the
  Mac seat is occupied. The lead decides whether that gap blocks the merge.
- Caller audit: **no caller in the repo sends an unframed POST.** Nothing here breaks.
- No defect found in the change.

## The two open questions, answered

### Q1. Does Windows http.sys deliver an unframed POST to our code, or answer 411 itself?

**http.sys answers 411 itself and never delivers the request.** So the Windows path never had the
"acted, then answered 411" defect, and on Windows the new guard is never reached.

Measured two ways.

(a) A bare `HttpListener` (no AgentEyes code) on 127.0.0.1:7999, driven with raw socket requests.
The handler prints a line the moment it receives a context:

```
=== A bodyless POST, no Content-Length, no TE
    31.3ms client sends
    45.7ms client got: HTTP/1.1 411 Length Required | Content-Type: text/html; charset=us-ascii |
           Server: Microsoft-HTTPAPI/2.0 | ... | HTTP Error 411. The request must be chunked or have a content length.
=== B POST Content-Length: 0
   355.3ms client sends
   360.7ms HANDLER entered: POST /record/stop CL='0' TE=''
   367.4ms client got: HTTP/1.1 200 OK | ... | {"ok":true}
=== C POST JSON {} Content-Length: 2
   672.9ms HANDLER entered: POST /record/stop CL='2' TE=''
   673.4ms client got: HTTP/1.1 200 OK
=== D bodyless PUT, no Content-Length
   989.6ms client got: HTTP/1.1 411 Length Required ... Server: Microsoft-HTTPAPI/2.0
=== E HTTP/1.0 bodyless POST
  1304.4ms client got: HTTP/1.1 411 Length Required ... Server: Microsoft-HTTPAPI/2.0
```

The instrument is shown working: B and C each print `HANDLER entered`. A, D and E print nothing,
even though later requests on the same listener were delivered in order, so it is not a late delivery.

(b) The real product. `agenteyes.exe serve --port 7899` (the PR build, from `bin\x64\Release`), pid
5588. The server logs `[HeadlessControlServer] <METHOD> <path>` as its first line for every request
it receives. Ten bodyless POST/PUT requests were sent (stop x1, shot, start, account/refresh,
rename x2, subtitles x2, walkthrough x2, PUT /record/stop). Every bodied request in the same run has
its log line; none of the bodyless ones has one. For example, the bodyless stop was sent at
10:15:52.284, and the log goes straight from the earlier GET to the next GET:

```
10:15:48.045 [pid 5588] [INFO] [HeadlessControlServer] GET /status: 200 answer written (776 bytes)
10:15:52.572 [pid 5588] [INFO] [HeadlessControlServer] GET /status
```

Counts over all 393 lines from pid 5588: `REJECTED` = 0, `ObjectDisposed` = 0. Zero `REJECTED`
lines is the expected result here, not a missing log: (a) shows the request never reaches managed code.

Consequence for the WPF app: its own `RestServer` (out of scope, unchanged) also sits on http.sys, so
it never sees an unframed POST either. The developer's concern that the app would answer 200 where the
headless host answers 411 does not happen. On Windows both answer with the same http.sys 411.

### Q2. Is "no body at all" different from "Content-Length: 0"?

**Yes, on Windows too.** No body and no Content-Length gets http.sys's 411. `Content-Length: 0`
(curl `-d ''`) is delivered and served normally. Live, with a recording running:

```
10:16:26.139 == stop with Content-Length: 0 (curl -d '')
HTTP/1.1 200 OK
Content-Length: 210
  "Dir": "C:\\Users\\soren\\Videos\\AgentEyes\\2026-11-14_101622_video",
  "File": "C:\\Users\\soren\\Videos\\AgentEyes\\2026-11-14_101622_video\\recording.mp4",
  "DurationSeconds": 3.25,
10:16:28.581 status:   "State": "idle",
```

## Acceptance criteria - Expected vs Actual (Windows)

### 1. A bodyless stop is one thing or the other

Expected: with a recording running, `curl -X POST .../record/stop` (no body) returns either 200 and
the saved recording, or an error with the recording still running; `/status` agrees.

Actual: an error (411), and the recording was still running. Response first, then `/status`:

```
10:15:46.863 == start (bodied) -> HTTP/1.1 200 OK, "State": "recording",
             "Dir": "C:\\Users\\soren\\Videos\\AgentEyes\\2026-11-14_101546_video"
10:15:52.284 == 1. bodyless POST /record/stop (curl -X POST, no -d)
HTTP/1.1 411 Length Required
Content-Type: text/html; charset=us-ascii
Server: Microsoft-HTTPAPI/2.0
Connection: close
Content-Length: 344
<BODY><h2>Length Required</h2><hr><p>HTTP Error 411. The request must be chunked or have a content length.</p>
[curl http_code=411 time_total=0.001790s]
10:15:52.467 == 2. GET /status immediately after
  "State": "recording",
  "ElapsedSeconds": 5.08,
  "Dir": "C:\\Users\\soren\\Videos\\AgentEyes\\2026-11-14_101546_video",
```

**PASS.** Note: on Windows the 411 body is http.sys's HTML, not the server's JSON
`length_required` envelope, because http.sys answers before our code runs (Q1).

### 2. The state afterwards is unambiguous

Expected: `/status` right after criterion 1 says `recording` if nothing stopped, consistent with the
response.

Actual: `"State": "recording"` at 10:15:52.467, 183 ms after the 411, same `Dir`, `ElapsedSeconds`
still increasing (0.5 -> 5.08). The recording then kept going and was stopped by the bodied call
in criterion 3 at 19.75 s, which proves the bodyless call did not end it. **PASS.**

The same "rejected means nothing happened" result for `/record/shot`, measured during the same
recording:

```
10:16:04.167 == bodyless POST /record/shot   -> [http_code=411]
10:16:05.366 PNGs in shots\ after bodyless:  0
10:16:05.580 == bodied POST /record/shot -d '{}' -> {"file": "...\\2026-11-14_101546_video\\shots\\00m18s.png"} [http_code=200]
10:16:06.952 PNGs in shots\ after bodied:    1
```

and the stop result below reports `"Shots": 1`: only the bodied shot happened.

### 3. The bodied call still works

Expected: `-H 'Content-Type: application/json' -d '{}'` returns 200 with directory, file and duration.

Actual:

```
10:16:07.129 == 3. bodied stop
{
  "Dir": "C:\\Users\\soren\\Videos\\AgentEyes\\2026-11-14_101546_video",
  "File": "C:\\Users\\soren\\Videos\\AgentEyes\\2026-11-14_101546_video\\recording.mp4",
  "DurationSeconds": 19.75,
  "Shots": 1
}
[http_code=200 time_total=1.793581s]
10:16:09.059 == GET /status after:  "State": "idle",  "LastStopFailed": false,
```

**PASS.**

### 4. No ObjectDisposedException for that call, shown as a presence

Expected: the log lines the request wrote show it handled and its response written.

Actual, service log `%LOCALAPPDATA%\AgentEyes\logs\AgentEyes-20261114.log`, pid 5588. The bodied
stop:

```
10:16:07.211 [pid 5588] [INFO] [HeadlessControlServer] POST /record/stop
10:16:09.001 [pid 5588] [INFO] [HeadlessControlServer] Stop: C:\Users\soren\Videos\AgentEyes\2026-11-14_101546_video saved; post-processing running in the background
10:16:09.003 [pid 5588] [INFO] [HeadlessControlServer] POST /record/stop: 200 answer written (211 bytes)
```

The `Content-Length: 0` stop:

```
10:16:26.239 [pid 5588] [INFO] [HeadlessControlServer] POST /record/stop
10:16:28.518 [pid 5588] [INFO] [HeadlessControlServer] Stop: C:\Users\soren\Videos\AgentEyes\2026-11-14_101622_video saved; post-processing running in the background
10:16:28.518 [pid 5588] [INFO] [HeadlessControlServer] POST /record/stop: 200 answer written (210 bytes)
```

The bodyless stop wrote no line on Windows because it never reached the process (Q1, with the
surrounding lines quoted there). So it cannot raise an `ObjectDisposedException` on this platform.
Count over all pid 5588 lines: 0. The new `200 answer written` line is present for every 200
response, so the guard in `Json(...)` is running. **PASS on Windows.**

Not checked by me: the developer's Linux wording question (on Linux the rejected request's attempted
411 write does catch an `ObjectDisposedException` and names it in an INFO line). That is a Linux
matter and stays with the lead, as the handoff note says.

### 5. Same behaviour on all three platforms

| Platform | Result | Who verified |
|----------|--------|--------------|
| Windows | bodyless stop -> 411 (http.sys), `/status` recording; bodied -> 200, idle | **QA, this report** |
| Linux | bodyless stop -> 411 (managed listener), `/status` recording; bodied -> 200, idle | **developer only** (`handoff.md`), NOT independent |
| macOS | not run | **unverified** |

The caller-visible result on Windows matches the developer's Linux result: 411, nothing done,
`/status` agrees. The mechanism differs. On Windows http.sys stops the request. On Linux the new
guard does. The only visible difference is the 411 body (http.sys HTML vs the listener's
`<h1>Length Required</h1>`).

Coverage gap to note: CI's `macos` job runs `tests/AgentEyes.Tests.Mac`, which does not contain the
new framing tests (they are only in `tests/AgentEyes.Tests.Linux`). So no automated or manual run
has exercised this change on macOS.

**PARTIAL.** This is a coverage gap, not a defect. Windows is QA-verified, Linux is
developer-verified, macOS is unverified.

### 6. Every other optional-body POST is checked

I read the route table myself (`HeadlessControlServer.cs:125-153`, and `RecordingChange`
at `:180-232`). The POST routes are exactly: `/account/refresh`, `/record/start`, `/record/shot`,
`/record/stop`, `/recordings/{id}/rename`, `/recordings/{id}/subtitles`,
`/recordings/{id}/walkthrough`. That is 7 routes, plus `DELETE /recordings/{id}`. The developer's
table lists all 7 plus DELETE. **No endpoint missed.**

Windows behaviour, measured against `serve --port 7899`:

| Endpoint | Bodyless on Windows (before and after: http.sys answers, handler never runs) | Measured |
|----------|------|----------|
| `POST /record/start` (idle) | 411 | 411, then `/status` `"State": "idle"` |
| `POST /record/shot` (recording) | 411 | 411, 0 PNGs written |
| `POST /record/stop` (recording) | 411 | 411, still recording (criterion 1) |
| `POST /account/refresh` | 411 | 411 in 0.0015 s |
| `POST /recordings/2026-11-14_101622_video/rename` | 411 | 411 in 0.0014 s, folder name unchanged |
| `POST /recordings/2026-11-14_101622_video/subtitles` | 411 | 411 in 0.0014 s |
| `POST /recordings/2026-11-14_101622_video/walkthrough` | 411 | 411 in 0.0013 s |
| `PUT /record/stop` | 411 | 411 |
| `DELETE /recordings/no-such-id-123` | not a body method | 404 `{"error": "no recording with id: no-such-id-123", "code": "not_found"}` |

"Before" on Windows is the same as "after". Http.sys behaviour does not depend on this PR (Q1(a)
used no AgentEyes code). **PASS.**

### 7. A regression test exists

`tests/AgentEyes.Tests.Linux/HeadlessControlServerFramingTests.cs`: 20 tests that drive the real
server over a raw socket with no body and assert 411, zero dispatches, and `/status` idle. Mutation
evidence (guard disabled -> 7 fail) is from the developer's handoff on Linux. I did not re-run it.

I ran the project on Windows:

```
dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release --filter "FullyQualifiedName~HeadlessControlServerFraming"
  Passed RecordStop_NoBodyNoContentLength_Rejected411BeforeHandlerAndStatusAgrees [450 ms]
  Passed RecordStart_NoBodyNoContentLength_Rejected411AndNoRecordingStarted [8 ms]
  Passed OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler x5
  Passed RecordStop_ContentLengthZero_IsDispatched [43 ms]
  Passed RecordStop_WithJsonBody_IsDispatchedAndAnswersItself [7 ms]
  Passed IsUnframedBody_MethodAndFraming_MatchesTheListenersRule x11
Total tests: 20
```

What this does NOT prove: on Windows these tests pass trivially for the 411 cases. Http.sys never
delivers the request, so the dispatch counter stays 0 whether or not the guard exists. The guard is
only really exercised on Linux (CI `linux` job, which is green on #124). The presence arms
(`ContentLengthZero_IsDispatched`, `WithJsonBody_IsDispatched...`) do run for real on Windows and pass.
Also, this project is not in `AgentEyes.sln`, so the Windows gate below does not include it. CI runs
it in the `linux` job. **PASS** (the test exists and is green). The Windows run does not exercise the
guard.

### 8. Gates green, with counts

Run on the PR branch at `668a1ac`:

```
dotnet build AgentEyes.sln -c Release
  Build succeeded.
      0 Warning(s)      (an incremental re-run; the first full build reported 22 analyzer warnings, all in tests/AgentEyes.Tests, none in changed files)
      0 Error(s)
dotnet test AgentEyes.sln -c Release
  Passed!  - Failed: 0, Passed: 2143, Skipped: 0, Total: 2143, Duration: 1 m 4 s - AgentEyes.Tests.dll (net8.0)
```

The suite is green, so there are no inherited failures to sort out and I did not need a `main`
baseline. The count is 2143, not the last recorded 2135. The PR changes no file under
`tests/AgentEyes.Tests` (it touches only `handoff.md`, `HeadlessControlServer.cs` and the Linux test
file), so the extra 8 tests came from `main` before this branch, not from this change.

PR #124 CI checks: `linux` pass, `macos` pass, `windows` pass (two runs each).

**PASS.**

## Regression risk: caller audit

The change makes an unframed POST/PUT fail before any handler runs on Linux/macOS. On Windows,
http.sys already failed them (Q1). Every caller in the repo that POSTs to the Control API:

| Caller | File:line | Sends a body? | Framing on the wire (measured) | Still works? |
|--------|-----------|---------------|-------------------------------|--------------|
| API smoke `Post` helper | `scripts/api-smoke.ps1:40-42` (used at 60, 63, 71, 80, 82, 85, 89, 108) | yes: `-Body ($obj \| ConvertTo-Json)`; `@{}` serialises to `{}` | `Content-Length: 6` from PS 5.1 | **yes**: start/shot/stop run live against `serve`, 200, `/status` idle |
| Python client smoke | `scripts/py-client-smoke.ps1:52` | yes, JSON body (`/capture`, against the WPF app only) | PS 5.1 with `-Body` | yes. Also targets the unchanged WPF `RestServer` |
| Python client `_request` | `clients/python/agenteyes_client/client.py:193-213` (callers 157, 166, 175, 179, 186 all pass a dict) | yes, `json.dumps(body)` | `Content-Length: 2` for `{}` | **yes**: `record_start`/`record_shot`/`record_stop` run live against `serve`, Shots 1, then idle |
| agenteyes-control skill driver | `drive.py` in the repo's `agenteyes-control` skill folder | goes through the Python client above | same as above | yes |
| Avalonia/Linux desktop app | `src/AgentEyes.LinuxApp/Services/ControlClient.cs:77,83,90,102,109,116` via `CallAsync` `:119-125` | yes, every POST passes a non-null body (`body`, `new { }`, `new { name }`, `new { language }`) -> `StringContent` | `.NET StringContent("{}")` = `Content-Length: 2` | yes, by code plus measured framing. Not run (Linux desktop app) |
| `agenteyes record start/stop` CLI client | `src/AgentEyes.Core/Headless/HeadlessCli.cs:38,47` via `Call` `:120-125` | yes: serialized start body, and literal `"{}"` for stop | `.NET StringContent` = `Content-Length: 2` | yes, by code plus measured framing. Not run against a server: its port comes from the live app's config (7882, the user's running tray app), which I did not touch |
| WPF app (`src/AgentEyes.App`) | none | n/a | n/a | the app does not call the Control API. Its only `HttpClient` is `PluginRegistry.cs:75` (plugin registry, not 7882). `RestServer` is a server, unchanged |
| Plugins | `plugins/*/run.ps1:156,198` | n/a | n/a | POST to an external `/chat/completions` endpoint, not the Control API |
| `scripts/linux/install.sh:139-145`, `scripts/mac/install.sh` | GET `/health` only | n/a | n/a | unaffected |

A wider finding on which client stacks are even capable of sending an unframed POST. I measured
each against a header-echo listener on Windows:

```
.NET HttpClient, StringContent("{}")          CL='2'
.NET HttpClient, Content = null               CL='0'
PowerShell 5.1 Invoke-RestMethod -Body {}     CL='6'
PowerShell 5.1 Invoke-RestMethod, no -Body    CL='0'
Python urllib (3.13), repo client, {}         CL='2'
Python urllib (3.13), Request(data=None)      CL='0'
```

Every library stack in use frames a bodyless POST with `Content-Length: 0`. That request is served
(Q2), even where a caller forgets the body. Only a raw `curl -X POST url` with no `-d` (or a
hand-written socket request) is unframed. So the regression surface is limited to people typing curl
by hand, and they now get an honest 411 with nothing done.

**Caller audit result: no caller sends a bodyless (unframed) POST. No blocking defect.**

## Other observations (not defects in this change)

- Post-processing of my four test recordings logged `[PostRecording] stage package FAILED ...
  ffmpeg failed during extract audio (exit -22) ... Output file does not contain any stream`. The
  recordings used `source=none` (no audio track), so audio extraction has nothing to extract. This is
  post-processing, after the response was written, and not request handling. It may be worth a
  separate look: a silent video recording reports a post-processing ERROR.
- Test recordings created during this run, left in place (not deleted):
  `%USERPROFILE%\Videos\AgentEyes\2026-11-14_101546_video`, `2026-11-14_101622_video`,
  `2026-11-14_101740_video`, `2026-11-14_101745_video`. Each is a few seconds of silent screen video.
- The user's installed tray app (pid 16508, port 7882) was left alone: GET `/status` only, `idle`
  before and after.
- The "send `-d '{}'`" workaround is still correct guidance for hand-typed curl on every platform.
  On Windows it was always needed (http.sys 411s the bare form).

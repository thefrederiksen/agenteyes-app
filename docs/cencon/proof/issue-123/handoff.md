# Issue #123 - Developer handoff: a bodyless POST is rejected before it has any effect

Branch: `issue-123-bodyless-post-411`. Machine: devlinux (Linux, dotnet SDK 8.0.134).
Measured on 14 November 2026 against the installed systemd --user service.

## The contract chosen, and why it is not the lead's first preference

The issue allows two contracts: a bodyless POST either **succeeds with 200**, or is **rejected
before it has any effect**. The lead preferred the first. On Linux and macOS the first is not
possible with the managed `HttpListener`, and I measured why:

- A 20-line standalone `HttpListener` (no AgentEyes code) on this machine, given
  `curl -X POST http://127.0.0.1:7999/x` (no `-d`): **curl had its `411 Length Required` back in
  41 ms, BEFORE the handler's first line ran** (curl finished at 09:47:58.860, the handler logged
  its first timestamp at 09:47:58.866). The context is still handed to the application, with its
  response already sent and disposed; writing to it throws `ObjectDisposedException`.
- The listener does this for any POST or PUT with neither a `Content-Length` header nor a
  chunked body. `Content-Length: 0` (curl `-d ''`) is fine and answers 200.

So on Linux and macOS no code of ours can put a 200 in front of that caller - the 411 is already on
the wire. The only contract that is achievable, and the same on every platform, is the second:
**reject an unframed POST/PUT before any handler runs.** The server now does exactly that. On
Windows (http.sys), if the request reaches us at all, we write our own JSON 411; either way the
caller gets 411 and nothing happens. This argument is also posted on the issue.

What a caller who sends no body at all sees on Linux is the listener's HTML 411
(`<h1>Length Required</h1>`), not our JSON envelope - we cannot change that body because the
listener sends it before we run. Where our own 411 does reach the caller, it is
`{"error": "... rejected and NOTHING was done ...", "code": "length_required"}`.

## What changed

`src/AgentEyes.Core/Headless/HeadlessControlServer.cs`:

1. `IsUnframedBody(method, contentLength, transferEncoding)` - true for POST/PUT with no
   Content-Length and no chunked Transfer-Encoding (the listener's own rule).
2. `Handle` checks it FIRST, before the route table. A hit goes to `RejectUnframed`, which logs
   `... REJECTED before any effect ...`, tries to write a JSON 411, and if the listener already
   answered, logs `the listener had already answered 411 itself (...); no action taken`. No endpoint
   handler runs.
3. `Json(...)` (every 200 answer) now catches a failed write and logs it distinctly as
   `<METHOD> <path> HANDLED - the action completed - but its 200 answer could NOT be written`,
   instead of escaping to the loop's generic "request handler FAILED" + 500 path. A successful write
   logs `<METHOD> <path>: 200 answer written (N bytes)`. (This is the "a failed response write cannot
   be mistaken for a failed action" guard from the Scope.)
4. `RequestsDispatched` (internal counter) - requests that passed the guard and reached a handler.
   It exists so a test can prove the handler did not run, which the client cannot see.

`tests/AgentEyes.Tests.Linux/HeadlessControlServerFramingTests.cs` - 20 new tests (below).

Not changed: `src/AgentEyes.App/RestServer.cs` (the Windows WPF app's own Control API). The issue
scopes this to `HeadlessControlServer` and says `AgentEyes.App` is not involved. RestServer runs on
http.sys only; whether http.sys delivers an unframed POST to it is unmeasured - see criterion 5.

## Acceptance criteria

### 1 and 2. A bodyless stop is one thing or the other, and /status agrees

Implemented: the guard above. Measured on the reinstalled service, with a recording running
(`POST /record/start -d '{"source":"none"}'` -> 200, Dir `.../2026-11-14_095518_video`):

```
== 2. bodyless POST /record/stop (curl -X POST, no -d)
HTTP/1.1 411 Length Required
Content-Type: text/html
Server: Microsoft-NetCore/2.0
Content-Length: 24
Connection: close

<h1>Length Required</h1>
  GET /status -> State: recording Dir: /home/soren/Videos/AgentEyes/2026-11-14_095518_video
```

Response says "not done"; /status says still recording. They agree. Before the fix, the same call
returned the same 411 and `/status` said `idle` (the stop had run and saved
`2026-11-14_095115_video`), and the retry got `409 not recording`.

QA: start a recording with a body, then `curl -i -X POST http://127.0.0.1:7882/record/stop`
(no `-d`), then `curl -s http://127.0.0.1:7882/status`. Expect 411 and `"State": "recording"`.

### 3. The bodied call still works

```
== 4. bodied stop -H 'Content-Type: application/json' -d '{}'
{
  "Dir": "/home/soren/Videos/AgentEyes/2026-11-14_095518_video",
  "File": "/home/soren/Videos/AgentEyes/2026-11-14_095518_video/recording.mp4",
  "DurationSeconds": 6.17,
  "Shots": 0
}  -> HTTP 200
  GET /status -> State: idle Dir: None
```

Also `-d ''` (Content-Length: 0, no JSON) on a second recording: 200, Dir
`.../2026-11-14_095541_video`, DurationSeconds 3.02, then `/status` idle.

### 4. No ObjectDisposedException for that call - shown as a presence

Log lines the requests DID write (service log `~/.local/share/AgentEyes/logs/AgentEyes-20261114.log`,
pid 3526377):

```
09:55:23.140 [INFO] [HeadlessControlServer] POST /record/stop
09:55:23.141 [WARN] [HeadlessControlServer] POST /record/stop REJECTED before any effect: no Content-Length and no chunked body (411 Length Required). Send a JSON body, or Content-Length: 0.
09:55:23.145 [INFO] [HeadlessControlServer] POST /record/stop: the listener had already answered 411 itself (ObjectDisposedException); no action taken
...
09:55:25.243 [INFO] [HeadlessControlServer] POST /record/stop
09:55:39.593 [INFO] [HeadlessControlServer] Stop: /home/soren/Videos/AgentEyes/2026-11-14_095518_video saved; post-processing running in the background
09:55:39.596 [INFO] [HeadlessControlServer] POST /record/stop: 200 answer written (188 bytes)
```

Note for QA, stated plainly: on Linux the listener still disposes the response of an unframed
request, so our attempted 411 write DOES hit an `ObjectDisposedException` - it is caught and named in
the INFO line above, no stack trace reaches the log, and it is not logged as an error. The bodied stop
logs `200 answer written` with no exception of any kind. If QA reads criterion 4 as "the exception
type may not appear in the log line at all", that is a wording call for the lead, not something I
will redefine; the exception is caught by design because it is the listener's doing.

### 5. All three platforms

- **Linux: verified** (above, and the test suite).
- **macOS: NOT verified.** Built only (`-p:AgentEyesUnixPlatform=mac`, clean). macOS uses the same
  managed `HttpListener` as Linux, so I expect identical behaviour, but I have not run it. Needs a
  Mac seat: same curl sequence against `agenteyes serve`.
- **Windows: NOT verified.** I cannot build or run Windows here. Unknowns QA must measure on Windows:
  (a) does http.sys deliver an unframed POST to the headless server at all, or answer 411 itself?
  Either way the caller must get 411 and `/status` must be unchanged; if delivered, the body should be
  our JSON with `"code": "length_required"`. (b) The WPF app's `RestServer` (port 7882 when the app
  runs, out of this issue's scope) was not changed - if http.sys delivers unframed POSTs, the app
  answers them 200 and so DIFFERS from the headless host. That would be a follow-up, not a defect in
  this change.

### 6. Every other optional-body POST, before and after (Linux, measured)

Body optional = the handler would act with no body. Before = old service (pid 2350406), after =
reinstalled (pid 3526377). In both columns the caller sees the listener's HTML 411.

| Endpoint | Body | Before: did the handler act? | After |
|----------|------|------------------------------|-------|
| `POST /record/start` | optional | **Yes** - log `StartFrom: mode=video screen=1 source=Mic`; it then failed with a 400 for this machine's mic, but with a valid default it would have started a recording behind the 411 | 411, rejected before handler, `/status` idle |
| `POST /record/shot` | none | **Yes** - shot taken behind the 411 (log: handler ran, then `ObjectDisposedException` in `Json`) | 411, rejected; shots folder count 0 before and 0 after |
| `POST /record/stop` | none | **Yes** - stopped and saved `2026-11-14_095115_video` behind the 411; retry 409 | 411, rejected, `/status` still recording |
| `POST /account/refresh` | none | **Yes** - credential re-read behind the 411, then `request handler FAILED` + ODE | 411, rejected |
| `POST /recordings/{id}/walkthrough` | none | **Yes** - `AnswerFromWorker: walkthrough ... started`, ran ffmpeg packaging behind the 411 | 411, rejected |
| `POST /recordings/{id}/rename` | required (`name`) | handler ran, failed with 400 (empty name), no effect | 411, rejected before handler |
| `POST /recordings/{id}/subtitles` | required (`language`) | handler ran, failed with 400, no effect | 411, rejected before handler |
| `DELETE /recordings/{id}` | not a body method | the listener does not 411 DELETE; served normally (404 for an unknown id) | unchanged: 404 for `no-such-id-123` |

That is where the rest of the defect was hiding: `/record/start`, `/record/shot`, `/account/refresh`
and `/walkthrough` all had the same "acted, then said 411" shape as stop.

### 7. Regression test

`tests/AgentEyes.Tests.Linux/HeadlessControlServerFramingTests.cs` starts the real
`HeadlessControlServer` on a free loopback port over an idle `RecordingService` and writes requests on
a raw socket (an HTTP library would add `Content-Length: 0` itself and never exercise the defect):

- `RecordStop_NoBodyNoContentLength_Rejected411BeforeHandlerAndStatusAgrees` - 411, zero requests
  dispatched, `/status` idle.
- `RecordStart_NoBodyNoContentLength_Rejected411AndNoRecordingStarted` - 411, zero dispatched,
  `/status` idle.
- `OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler` x5 (shot, account/refresh, rename,
  subtitles, walkthrough).
- Presence arm: `RecordStop_WithJsonBody_IsDispatchedAndAnswersItself` and
  `RecordStop_ContentLengthZero_IsDispatched` - the same endpoint WITH framing reaches the handler
  (one dispatch) and gets its own `409 not recording`.
- `IsUnframedBody_MethodAndFraming_MatchesTheListenersRule` x11.

What the tests cannot see: they never start a real recording (that needs X and ffmpeg), so "the stop
did not happen" is proved by the dispatch counter, not by a recording surviving. The live
stop-while-recording case is the measurement under criteria 1-2.

**Mutation evidence.** With the guard disabled (`if (false && IsUnframedBody(...))`), the first
version of these tests STILL PASSED - the listener's 411 reaches the client before our loop handles
the request, so reading the counter straight after the response raced the handler. That is a check
failing open; fixed by a `GET /status` barrier (the loop serves one request at a time in arrival
order) before reading the counter. Re-run with the guard disabled:

```
Failed ...RecordStop_NoBodyNoContentLength_Rejected411BeforeHandlerAndStatusAgrees
Failed ...OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(path: "/recordings/some-id/rename")
Failed ...OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(path: "/account/refresh")
Failed ...OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(path: "/record/shot")
Failed ...OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(path: "/recordings/some-id/walkthrough")
Failed ...OtherPosts_NoBodyNoContentLength_Rejected411BeforeHandler(path: "/recordings/some-id/subtitles")
Failed ...RecordStart_NoBodyNoContentLength_Rejected411AndNoRecordingStarted
Failed!  - Failed:     7, Passed:   107, Skipped:     0, Total:   114
```

Guard restored: `Passed!  - Failed: 0, Passed: 114, Skipped: 0, Total: 114`.

### 8. Gates (Linux machine)

```
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:Platform=x64
  Build succeeded. 1 Warning(s) 0 Error(s)   (the warning is pre-existing: AlwaysOnEngine.cs(901) CS8602)
dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release
  Passed! - Failed: 0, Passed: 114, Skipped: 0, Total: 114   (was 94 before this change; +20 new)
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release -f net8.0 -p:AgentEyesUnixPlatform=mac
  Build succeeded. 1 Warning(s) 0 Error(s)   (same pre-existing warning)
```

`dotnet build/test AgentEyes.sln` is Windows-only (the solution contains the WPF app) and was not run
here; the Windows gate is for the Windows seat.

## Where the "always send -d '{}'" workaround still appears

The workaround is still REQUIRED after this change for a caller that sends no body at all - they now
get a clean "nothing was done" 411 instead of a lie, but they do not get a 200. Any documentation
should say: send a body, or `Content-Length: 0` (curl `-d ''`). Places found:

- `docs/cencon/proof/issue-111/handoff.md:171`, `issue-111/runtime-macos.txt:152`,
  `issue-111/qa-report.html:378`, `issue-102/qa-report.md:196-203` - historical proof records; I did
  not edit them.
- The macOS developer-loop comment on #114 (GitHub, not in the repo) - not edited.
- The repository's root project-instructions file - does not contain it.
- `clients/python/agenteyes_client` always sends `{}` on every POST, so it was never affected.

## Other observations (not chased, per the OUT list)

- Background post-processing logged several `ffmpeg failed during poster thumbnail / extract audio
  (... moov atom not found)` errors around the stops; these are recording/post-processing behaviour,
  not request handling.
- The bodied stop at 09:55:25 took 14.3 s to answer (the pre-fix stop at 09:51:21 took 2.4 s, the
  09:55:45 one 8.3 s). The framing guard is not on that path; noted for whoever owns stop latency.
- Every 200 answer now writes one extra INFO line (`200 answer written`), including the app's
  once-a-second `GET /status` poll.

## Smoke scope

Area: Control API (headless). No GUI change. QA should drive the curl sequence above on Linux and, on
the Windows and macOS seats, the same sequence plus the two Windows unknowns under criterion 5.
Reminders: use the REST API; never force-foreground the app; the HUD is capture-excluded, so assert
state via `/status`. Do not delete anything under `~/Videos/AgentEyes` - in particular
`2026-11-14_093032_video` is evidence for #122.

I believe this is finished for Linux; macOS and Windows are unverified as stated above.

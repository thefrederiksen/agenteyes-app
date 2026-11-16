# Isolating a REAL AgentEyes process from the person's library

**Issue:** #161. **Read this before you start ANY real `agenteyes` / `AgentEyesApp` process to verify
something** - a repair pass, a mux, a stop path, a package run - on a machine where a person keeps
real recordings.

A test host isolates itself in-process (`AppDataPaths.RedirectForThisProcess`, issue #78). A REAL
process does not, unless you tell it to with the two variables below. Without them it acts on the
person's real state root (config, log, DevThrottle credential, ffmpeg registry) and their real
recordings - including the startup repair pass, which goes looking for other people's recordings.

## The switch: two environment variables, both or neither

| Variable | Stands in for | Root it produces |
|----------|---------------|------------------|
| `AGENTEYES_REDIRECT_DATA` | the local app-data folder (`%LOCALAPPDATA%`, `~/Library/Application Support`, `~/.local/share`) | `<it>/AgentEyes` - config, **log**, credential, models, ffmpeg registry |
| `AGENTEYES_REDIRECT_VIDEOS` | the Videos folder (`~/Movies` on macOS) | `<it>/AgentEyes` - recordings |

Read once, at the very start of `agenteyes` (every command), `AgentEyesApp.exe` and the Linux app.
Not to be confused with `AGENTEYES_ROOT`, which is the INSTALLER's variable for its install layout.

**It refuses to start (CLI exit code 5, or a dialog for the tray app), writing NOTHING, when:**

- only one of the two is set (a partial redirect would mix a scratch root with a real one);
- a value is not an absolute path;
- any other `AGENTEYES_REDIRECT_*` variable exists (a typo would otherwise leave you on the real
  library while you believe you are redirected);
- the two roots overlap each other, or either one is, contains or sits inside the machine's REAL state
  or recordings root - **symbolic links resolved**, so `<scratch>/videos/AgentEyes -> ~/Movies/AgentEyes`
  is refused.

**It also refuses (exit 1):**

- a redirected `agenteyes serve` on the default port **7882** - that is the person's real service's
  address. Pass `--port N`, or put `{"Port": N}` in `<DATA>/AgentEyes/config.json`.
- a redirected `agenteyes record start|stop|status` whose service reports a DIFFERENT library on
  `/status` (or none - a service older than #161), and an UNredirected one whose service reports it is
  redirected. Nothing but the `/status` probe is sent to it. (Two unredirected processes are never
  compared - on Linux a systemd service and a shell can see different `XDG_DATA_HOME`.) The redirected
  CLI finds its service through `Port` in the scratch `config.json`, so write that file.
- the redirected Linux app (exit 5): it always drives the service at 127.0.0.1:7882, the real one.
  Drive a redirected service with the redirected CLI instead.

## It is loud - check that it is, every time

- stderr of every redirected command: `[redirect] LIBRARY REDIRECTED by ...: state -> ... ; recordings -> ... ; log -> ...`
- the redirected log (`<DATA>/AgentEyes/logs/AgentEyes-YYYYMMDD.log`): a `[WARN] [LibraryRedirect]` line,
  and every command's `[CliDispatcher] Run:` line carries `state=... recordings=... redirect=...`
- `serve` stdout: `recordings ->`, `data ->`, and `[redirect] ... this is NOT the person's real library`
- `GET /status`: `DataRoot`, `RecordingsRoot`, `LibraryRedirected`, `LibraryRedirect`
  (`"environment (AGENTEYES_REDIRECT_DATA, AGENTEYES_REDIRECT_VIDEOS)"`)

**If you do not see the banner, you are NOT redirected. Stop.**

## THE HOME TRAP (macOS) - an isolated HOME does NOT isolate AgentEyes

Measured on macOS (devthrottle-mac-mini, .NET 10.0.15 via roll-forward, 2026-11-16, issue #161 proof):

```
HOME=<scratch>/home  ->  UserProfile=<scratch>/home
                         MyVideos=/Users/soren/Movies                      <- NOT moved
                         LocalApplicationData=/Users/soren/Library/Application Support   <- NOT moved
```

and the product itself, started with `HOME=<scratch>/home` and no redirect, reported on its own
`/status`: `"RecordingsRoot": "/Users/soren/Movies/AgentEyes"`. .NET resolves those two folders
through the platform's own lookup, not `$HOME`. **Never use `HOME=` as isolation on a Mac.**

On **Linux**, .NET builds these folders from `$XDG_DATA_HOME` / `$HOME` and the XDG user-dirs, so a
changed `HOME` probably DOES move them - not measured for #161 (no Linux run from the Mac). On
**Windows**, .NET asks the shell for known folders, so overriding `%LOCALAPPDATA%` / `%USERPROFILE%`
is not expected to move them either - not measured. Neither matters: use the two variables on every
platform, and check the banner.

## How to isolate, per platform

Pick a FREE port (not 7882) - check it first.

**macOS / Linux (zsh / bash):**

```sh
S=$(mktemp -d)/ae161
mkdir -p "$S/data/AgentEyes" && echo '{"Port": 17961}' > "$S/data/AgentEyes/config.json"
export AGENTEYES_REDIRECT_DATA="$S/data" AGENTEYES_REDIRECT_VIDEOS="$S/videos"
agenteyes serve --port 17961 &          # look for the [redirect] banner on stderr
curl -s http://127.0.0.1:17961/status   # DataRoot / RecordingsRoot under $S, LibraryRedirected true
agenteyes record status                 # the redirected client checks the service is on $S
kill -TERM %1
unset AGENTEYES_REDIRECT_DATA AGENTEYES_REDIRECT_VIDEOS   # do not leave the shell redirected
```

From a source build, run the engine as `DOTNET_ROLL_FORWARD=Major dotnet src/AgentEyes.Core/bin/Release/net8.0/agenteyes.dll ...`
when the machine has no .NET 8 runtime, and build it with `-p:AgentEyesUnixPlatform=mac|linux` FIRST:
both configurations write to the same `bin/Release/net8.0`, so the last build wins.

On macOS, a shell has no Screen Recording grant: a real screen capture from it is refused and HANGS.
`serve` itself only enumerates displays, which works; do not `record start` a screen from a shell.

**Windows (PowerShell):**

```powershell
$S = Join-Path $env:TEMP "ae161-$(Get-Random)"
New-Item -ItemType Directory -Force "$S\data\AgentEyes" | Out-Null
Set-Content "$S\data\AgentEyes\config.json" '{"Port": 17961}'
$env:AGENTEYES_REDIRECT_DATA = "$S\data"; $env:AGENTEYES_REDIRECT_VIDEOS = "$S\videos"
& src\AgentEyes.Core\bin\x64\Release\net8.0-windows10.0.19041.0\agenteyes.exe serve --port 17961
# ... GET http://127.0.0.1:17961/status ...
Remove-Item Env:AGENTEYES_REDIRECT_DATA, Env:AGENTEYES_REDIRECT_VIDEOS
```

A redirected `AgentEyesApp.exe` cannot run beside the person's own: the single-instance lock is
machine-wide, not per library. Use `agenteyes serve --port N` for a second, redirected service.

## What the redirect does NOT cover (say so; do not discover it)

- **The log IS separable**: it lives under the state root, so a redirected process logs only into
  `<DATA>/AgentEyes/logs`. A REFUSED start logs nowhere (stderr / dialog only) - by design, the only
  log it could reach is the real one.
- **Windows crash log** `%TEMP%\AgentEyes-crash.log` (the tray app's last-resort crash file) is NOT
  under the state root and is NOT redirected.
- **Machine-wide names**: the tray app's single-instance mutex and the Linux app's socket in
  `$XDG_RUNTIME_DIR` are shared with the person's own copy.
- **ffmpeg** is still found through `PATH` / `QA_RECORD_FFMPEG_DIR`, and a redirected process can see
  (not own) every ffmpeg on the machine. Never kill an ffmpeg you did not start.
- A redirected process is **signed out** (the credential is per state root): hosted transcription and
  titles are skipped until you `agenteyes signin` under the same redirect.
- **The real AgentEyes folders themselves are not link-resolved** (only their parents are), so that a
  normal redirected start never even stats the real library. A scratch path that reaches the real
  library only through the TARGET of a `~/Movies/AgentEyes` symlink is not caught.
- A refused start whose redirected root IS (or links to) the real root does one metadata read
  (`stat`) of that real root folder while resolving links - measured under a sandbox in the #161 proof.

## Belt and braces on macOS: let the kernel enforce it

For anything risky, also run the process under `sandbox-exec` with the real roots denied, and PROVE
the profile blocks before relying on it:

```sh
P='(version 1)(allow default)(deny file-read* file-write* (subpath "/Users/<you>/Movies/AgentEyes") (subpath "/Users/<you>/Library/Application Support/AgentEyes"))'
sandbox-exec -p "$P" ls "/Users/<you>/Movies/AgentEyes"   # MUST say: Operation not permitted
sandbox-exec -p "$P" env AGENTEYES_REDIRECT_DATA=... AGENTEYES_REDIRECT_VIDEOS=... dotnet agenteyes.dll serve --port N
log show --start "<when you started>" --style compact --predicate 'eventMessage CONTAINS "Sandbox:" AND eventMessage CONTAINS "deny("'
```

Start the process with `&` directly - not through a shell function - or `$!` is a subshell's pid and
your `kill` misses the service (this happened once while producing the #161 proof).

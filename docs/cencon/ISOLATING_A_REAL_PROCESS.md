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

Read once, at the very start of `agenteyes` (every command), `AgentEyesApp.exe` and the desktop app
(`AgentEyesDesktop`, `src/AgentEyes.DesktopApp` - the Linux app until #116 renamed it).
Not to be confused with `AGENTEYES_ROOT`, which is the INSTALLER's variable for its install layout.

**It refuses to start (CLI exit code 5, or a dialog for the tray app), writing NOTHING, when:**

- only one of the two is set (a partial redirect would mix a scratch root with a real one);
- a value is not an absolute path;
- any other `AGENTEYES_REDIRECT_*` variable exists (a typo would otherwise leave you on the real
  library while you believe you are redirected);
- the two roots overlap each other, or either one is, contains or sits inside the person's REAL state
  or recordings root - **symbolic links resolved**, so `<scratch>/videos/AgentEyes -> ~/Movies/AgentEyes`
  is refused;
- a redirected root (`<it>/AgentEyes`) ALREADY HOLDS something and does not carry the scratch marker
  `.agenteyes-scratch-library` (#161 pass 3, rule 2). The first redirected start writes that marker into
  both roots; a real library - relocated or not, wherever it lives - holds files and no marker, so it is
  refused by its CONTENT, which no environment can spoof. **The deliberate cost: a scratch root YOU filled
  by hand with fixtures is refused too** (a legitimate pre-populated scratch root is refused so that a real
  library never is). The refusal names both ways on: start from an EMPTY folder (a redirected start marks
  it), or, if the folder really is your scratch library, `touch <it>/AgentEyes/.agenteyes-scratch-library`;
- a symbolic link INSIDE either redirected root points OUT of that root (#161 pass 3, F2). ANY way out,
  not only into the real library, because the guard cannot know every place a real library lives (next
  section). Judged from the link's target AS WRITTEN, never followed (a target spelled through the root's
  own alias - macOS `/var` for `/private/var` - counts as inside); a target containing `..` is refused
  too. Links that stay inside the root are fine. Fix: remove the link, or copy what it points at in. (A
  legacy `qa-record` folder beside a root is never adopted under a redirect: the marker write creates
  `<it>/AgentEyes` first, so the one-time migration leaves `qa-record` alone);
- a value is not an EXISTING folder this process can write to - checked by really writing and removing a
  probe file, and only after every rule above has passed (so it never probes the real library). A
  redirect never creates its own roots: a typo is refused, not turned into a new, empty library.
  `mkdir -p` both folders first;
- the real library cannot be located (no passwd entry for the effective uid, an unreadable
  `user-dirs.dirs`) - there would be nothing to check the redirect against.

**How "the REAL library" is found (#161 pass 2).** NOT from this process's `HOME` / `XDG_*`: those are
exactly what an isolation attempt changes, and pass 1 trusted them - `HOME=<scratch>` plus
`AGENTEYES_REDIRECT_DATA=~/.local/share` was ACCEPTED and opened the owner's credential and live log.
The protected roots are the union of:

| Platform | From the ACCOUNT (passwd entry of the effective uid, never `$HOME`) | Plus |
|----------|------------------------------------------------------------------|------|
| Linux | state `<home>/.local/share` (XDG default; `$XDG_DATA_HOME` not read); recordings `XDG_VIDEOS_DIR` from `<home>/.config/user-dirs.dirs` (`$XDG_CONFIG_HOME` not read), else `<home>/Videos` | what this process's environment names (.NET special folders) |
| macOS | state `<home>/Library/Application Support`; recordings `<home>/Movies` | the same |
| Windows | - (unchanged: the shell's known-folder lookup) | the known folders |
| all | and whatever answers `GET http://127.0.0.1:7882/status` - the person's RUNNING service names its own `DataRoot` / `RecordingsRoot` (#161 pass 3, rule 1); nothing answering, or a service older than #161 that names none, adds nothing, and the banner says which | |

The union can only ADD refusals: a spoofed environment can add a scratch folder to the list but cannot
remove the account's. Every redirected banner prints the roots it derived and where each came from, so
check them rather than trusting the word "redirected".

**A relocated library (#161 pass 3, F1) - what is now refused, and what is still not.** If a person's
library has been moved (`XDG_DATA_HOME` in a shell profile or in their service's systemd unit, a
`user-dirs.dirs` under a moved `XDG_CONFIG_HOME`), the new place exists only in THAT environment, which the
passwd entry cannot name. Refused BY CONSTRUCTION now:

- a relocated library that holds anything (it always does once used) - by its content (rule 2), from any
  environment, wherever it lives;
- a relocated library, even an empty one, while the person's post-#161 service is running on 7882 - that
  service names it (rule 1);
- a relocated library this process's own environment names (the environment half of the union).

Still NOT refused (documented residue - the banner ends `NOT CHECKED by path: ...` and never says the
scratch "is not the person's real library"):

- a relocated library folder that is EMPTY or not created yet, set only in an environment this process does
  not share (a shell profile), with no service on 7882 - there is nothing yet to damage, but a redirect there
  would put scratch files where the person's library will later live;
- a library whose folder somebody marked with `.agenteyes-scratch-library` by hand;
- a service on a port other than 7882 (its port is in the REAL config.json, which the guard never reads), or
  one older than #161, which names no roots;
- two sources deliberately NOT read (the Lead's ruling, pass 3 - more machinery than this pass carries, and
  Linux-shaped): the running service's `/proc/<pid>/environ`, and the systemd user manager's environment
  (`environment.d`).

**Before pointing a redirect anywhere, make sure the folder is one you created for the job.**

**It also refuses (exit 1):**

- a redirected `agenteyes serve` on the default port **7882** - that is the person's real service's
  address. Pass `--port N`, or put `{"Port": N}` in `<DATA>/AgentEyes/config.json`.
- a redirected `agenteyes record start|stop|status` whose service reports a DIFFERENT library on
  `/status` (or none - a service older than #161), and an UNredirected one whose service reports it is
  redirected. Nothing but the `/status` probe is sent to it. (Two unredirected processes are never
  compared - on Linux a systemd service and a shell can see different `XDG_DATA_HOME`.) The redirected
  CLI finds its service through `Port` in the scratch `config.json`, so write that file.
- the redirected desktop app (exit 5): it always drives the service at 127.0.0.1:7882, the real one.
  Drive a redirected service with the redirected CLI instead.

## It is loud - check that it is, every time

- stderr of every redirected command: `[redirect] LIBRARY REDIRECTED by ...: state -> ... ; recordings -> ... ; log -> ...
  (pid N) ; compared against these real roots and overlaps none (each with where it was derived from): the REAL state root ... [passwd entry for uid N, home ..., XDG default + this process's environment (.NET special folders)] ; the REAL recordings root ... [...] ; the AgentEyes service answering on 127.0.0.1:7882 named its library on /status, and it is in this list (or: names no library / was asked and named none) ; NOT CHECKED by path: a library kept anywhere else (...) is not in this list - it is refused only by its content (an existing, non-empty AgentEyes folder without .agenteyes-scratch-library) - see docs/cencon/ISOLATING_A_REAL_PROCESS.md`
- the redirected log (`<DATA>/AgentEyes/logs/AgentEyes-YYYYMMDD.log`): a `[WARN] [LibraryRedirect]` line,
  and every command's `[CliDispatcher] Run:` line carries `state=... recordings=... redirect=...`
- `serve` stdout: `recordings ->`, `data ->`, and `[redirect] ... - compared against these real roots this process
  derived, and overlaps none:` followed by one `[redirect]   the REAL ... root <path> [<derivation>]` line per root
  and two last lines: what the service on 7882 said, and `[redirect] NOT CHECKED by path: ...`
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
mkdir -p "$S/data/AgentEyes" "$S/videos" && echo '{"Port": 17961}' > "$S/data/AgentEyes/config.json"
touch "$S/data/AgentEyes/.agenteyes-scratch-library"   # you filled this root by hand (config.json): mark it
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
New-Item -ItemType Directory -Force "$S\videos" | Out-Null
Set-Content "$S\data\AgentEyes\config.json" '{"Port": 17961}'
New-Item -ItemType File "$S\data\AgentEyes\.agenteyes-scratch-library" | Out-Null   # filled by hand: mark it
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
- **Machine-wide names**: the tray app's single-instance mutex and the desktop app's socket in
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
- **A relocated library** is refused by content, by the running service, or by this process's environment -
  and NOT in the residue cases listed under "A relocated library" above. The banner says so on every start.
- **The checks are START-UP checks**, and they read: one listing of each redirected root (rule 2), a walk of
  each root's whole tree (the link check - its cost grows with a scratch library's size, and the tray app runs
  it on start-up), and one `GET /status` to 127.0.0.1:7882 with a 2 s timeout. On Windows, with NOTHING on 7882, that GET
  waits the full 2 s (Windows retries a refused loopback connect; measured on CI) - a redirected start there is
  2 s slower when no service runs.
- **The link walk sees only what exists at start-up.** A link created inside a redirected root AFTER the process has
  started is not seen. Do not create links in a scratch library while a process is using it.
- **Hard links and bind mounts are not detected.** A hard link inside the scratch root to a file of the
  real library IS that file; a bind mount (root only) makes a folder BE another. Neither shows up as a
  symbolic link. Copy files into a scratch library; never `ln` them without `-s`, never bind-mount.
- Walking a root, .NET's directory enumerator does one following `stat` of each symbolic link it meets, to
  classify it, before the link is judged and refused. That reads the target's metadata and opens nothing
  (measured, #161 pass 3: a link to the owner's logs folder - 0 opens through it, 1 `stat`).

## Belt and braces on macOS: let the kernel enforce it

For anything risky, also run the process under `sandbox-exec` with the real roots denied, and PROVE
the profile blocks before relying on it:

```sh
P='(version 1)(allow default)(deny file-read* file-write* (subpath "/Users/<you>/Movies/AgentEyes") (subpath "/Users/<you>/Library/Application Support/AgentEyes"))'
sandbox-exec -p "$P" ls "/Users/<you>/Movies/AgentEyes"   # MUST say: Operation not permitted
sandbox-exec -p "$P" env AGENTEYES_REDIRECT_DATA=... AGENTEYES_REDIRECT_VIDEOS=... dotnet agenteyes.dll serve --port N
log show --start "<when you started>" --style compact --predicate 'eventMessage CONTAINS "Sandbox:" AND eventMessage CONTAINS "deny("'
```

On **Linux** the equivalent is Landlock: `docs/cencon/proof/issue-161/linux-pass2/lldeny.c` (build with
gcc), `lldeny <real-state-root> <real-recordings-root> -- <cmd>`. Prove it first - `lldeny R1 R2 -- ls R1`
must say `Permission denied` - and pair it with `strace -f -e trace=%file,%desc`, because Landlock does
not refuse `stat`.

Start the process with `&` directly - not through a shell function - or `$!` is a subshell's pid and
your `kill` misses the service (this happened once while producing the #161 proof).

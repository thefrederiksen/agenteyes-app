# AgentEyes

**The always-on recorder: your machine's memory, yours alone.**

> It was watching, so you don't have to remember.

AgentEyes is a quiet shadow that follows your day on your computer. It runs in the
background, continuously recording screen and audio into a rolling buffer (default: the
last 24 hours), so you never have to remember to hit record. When something matters - a
call, a demo, a bug that flashed by, a sentence someone said - you reach back, scrub the
timeline, and **keep** the part you want. Everything it hears is transcribed into a
timestamped, searchable log.

**Capture is the default; retrieval is the action.** The buffer is ephemeral and
self-overwriting - *keeping* is the explicit act.

The full picture lives in [docs/vision.md](docs/vision.md).

## What works today

The rolling buffer is the destination; the recorder it will be built on is already here
and tested:

- **AgentEyesApp.exe** - WPF tray app: pick a preset and record. OBS-style named
  presets (monitor/region, video/audio/screenshot, mic + system audio mixing with a
  noise gate), recent-recordings list, walkthrough builder (transcript + HTML), run at
  login, and a REST control API on port 7882 (`/status`, `/record/start`,
  `/record/stop`, `/screenshot`, `/devices`, ...).
- **Voice typing** - double-tap Ctrl anywhere, speak, and the transcription is typed
  into whatever has focus. In-process Whisper; the model loads once at startup.
- **agenteyes.exe** - the same engine as a CLI: `agenteyes screens`, `agenteyes shot`, `agenteyes audio`,
  `agenteyes video`, `agenteyes package`, `agenteyes selftest`.

## Install

No repository, no build, no prerequisites - the .NET runtime and ffmpeg are bundled.

**Download and double-click** [the latest installer](https://github.com/thefrederiksen/agenteyes-app/releases/latest/download/AgentEyes-Setup-win-x64.exe),
or pick any version from the [releases page](https://github.com/thefrederiksen/agenteyes-app/releases).

**Or install from a terminal** - one line, no admin:

```powershell
$e="$env:TEMP\agenteyes-setup.exe"; iwr -UseBasicParsing https://github.com/thefrederiksen/agenteyes-app/releases/latest/download/agenteyes-setup-cli-win-x64.exe -OutFile $e; & $e install
```

The same command **updates** an existing install - run it again and it fetches only what
changed, or reports `Nothing to do - all components up to date`. If `agenteyes-setup` is
already on PATH, `agenteyes-setup update` is enough.

Installs per-user (no admin) to `%LOCALAPPDATA%\AgentEyes\app` with a Start Menu
shortcut, optional run-at-login, optional `agenteyes` on PATH, and an uninstaller.
Recordings go to `%USERPROFILE%\Videos\AgentEyes\`.

AgentEyes records **without an account**. Signing in to DevThrottle is optional and only
unlocks the AI stages (transcription, titles, walkthroughs) - capture, camera, preview and
overlay all work signed out.

### Building the installer yourself

Only needed if you are changing AgentEyes; the released installer is built by CI from the
same script.

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1
agenteyes-setup install --release-dir dist\release
```

## Quickstart (from source)

Requires the .NET 8 SDK (Windows) and `ffmpeg` on PATH.

```
dotnet build AgentEyes.sln -c Release

# the app (tray + launcher)
src\AgentEyes.App\bin\x64\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe

# the CLI
src\AgentEyes.Core\bin\x64\Release\net8.0-windows10.0.19041.0\agenteyes.exe screens
```

The `x64` segment is not optional: both projects set `<Platforms>x64</Platforms>`, so a
`-c Release` build lands in `bin\x64\Release\`. An older checkout may also have a
`bin\Release\` directory holding a months-stale binary - running that one silently tests
code you did not build.

Verify everything (build + unit tests + headless selftest + API and GUI smoke tests). This is the
HEAVY, USER-INVOKED full sweep - it launches the app and records, so it refuses without `-Confirm`:

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-all.ps1 -Confirm
```

Recordings land in `%USERPROFILE%\Videos\AgentEyes\`; config, presets, logs, and the
Whisper model live in `%LOCALAPPDATA%\AgentEyes\`.

## Privacy stance

The "My" in the name is the point. This is the non-negotiable posture, learned from the
category's cautionary tales (Microsoft Recall's launch, Rewind.ai's reception):

- **Visible.** An always-on recording indicator; no stealth mode, ever.
- **Controllable.** Hard pause, per-app/window exclusions, bounded disk use.

## Repo layout

```
AgentEyes.sln
src/
  AgentEyes.Core/    capture engine + CLI (agenteyes.exe): screen, audio, mixing,
                         screenshots, region capture, manifests, Whisper transcription,
                         headless selftest
  AgentEyes.App/     WPF tray app (AgentEyesApp.exe): launcher, presets, test panel,
                         REST API
tests/
  AgentEyes.Tests/   xUnit
scripts/                 run-all.ps1 (user-invoked full verification, -Confirm) + smoke tests + try.cmd
docs/                    vision.md + engineering docs (some from the qa-record era)
```

`scripts\run-all.ps1 -Confirm` is the USER-INVOKED full verification - the human runs it when they
decide (it is heavy and refuses without `-Confirm`). Agents never run it; they build only.

## Roadmap

- **Phase A - Rolling capture core.** Segmented continuous capture, retention cap,
  auto-eviction; prove bounded disk use over a full day.
- **Phase B - Retrieve and keep.** Timeline scrubber over the buffer; mark in/out;
  export to mp4/wav/gif.
- **Phase C - Ambient transcription + search.** Continuous transcription into a
  searchable, timestamped log linked to the timeline.
- **Phase D - Privacy and polish.** Exclusions, pause, indicators, settings, installer.

## The name

The product was nearly called Ghost (accurate but spooky), then Echo (a great dog story,
but Amazon owns that ground), then a parrot-shaped detour - before a systematic search of
the "shadow" naming space landed on MyQuietShadow: the shadow that quietly followed your
day and remembered it. It was later renamed to **AgentEyes** (the "shadow" space was
crowded and a same-named competitor ruled out the front-runner). AgentEyes was vetted for
product-name, trademark, and app-store conflicts and ships from the Center Consulting site
rather than its own product domain. The full journey is in [HANDOVER.md](HANDOVER.md).

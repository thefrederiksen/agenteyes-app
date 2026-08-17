# AgentEyes.Core

The AgentEyes capture engine and its CLI, `agenteyes.exe`. Screen + audio recording (mic,
system loopback, or mixed with a noise gate), instant screenshots, region capture,
ffmpeg muxing, manifests, and in-process Whisper transcription. The WPF app
(`../AgentEyes.App`) and the REST API drive the same engine through one shared
`RecordingService`.

Requires the .NET 8 SDK (Windows) and `ffmpeg` on PATH.

## Build

```
dotnet build src/AgentEyes.Core/AgentEyes.Core.csproj -c Release
```

Produces `agenteyes.exe` (target `net8.0-windows10.0.19041.0`, x64).

## Commands

| Command    | What it does |
|------------|--------------|
| `screens`  | List monitors and microphones (NAudio + DirectShow names) |
| `shot`     | Instant screenshot (full monitor or `--region`), saved + clipboard |
| `audio`    | Audio to WAV (`--mic`, `--loopback`, or `--mix`) + on-demand screenshots |
| `video`    | Screen video + audio to MP4 (`--mic`, `--mix`, `--loopback`, `--region`) |
| `package`  | Transcribe (Whisper.net) + assemble walkthrough.html from a recording |
| `selftest` | Headless end-to-end self-test (12 checks, HTML report) |

Session hotkeys during audio/video: `S` screenshot, `P` pause/resume, `Q` stop.
Run `agenteyes` with no arguments for full usage.

## Output layout

```
%USERPROFILE%\Videos\AgentEyes\<timestamp>_<label>\
  audio.wav            (audio mode)
  recording.mp4        (video mode)
  shots\00m03s.png     screenshots named by offset
  manifest.json        monitor, region, mic, duration, file list
```

Config, presets, logs, and the Whisper model live in `%LOCALAPPDATA%\AgentEyes\`.
On first run, `StorageMigration` moves any qa-record-era folders to the new names.

## History

The engine began life as `qa-record` inside cc-qa-agent, was carved out via cc-ghost,
and renamed here. Old design docs from that era are under `../../docs/` as
`qa-record-*.md/html`. See `vendor/PROVENANCE.md` for code reused from cc-director.

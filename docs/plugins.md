# AgentEyes plugins (post-recording processors)

Issue #13, v1. A plugin takes a finished recording and produces something from it -
a QA report, documentation, meeting notes, anything that consumes the files of a
recording. Transcription is NOT a plugin: recording + transcription is always-on
core behavior. Plugins run after it.

## The contract

A plugin is a folder:

```
%LOCALAPPDATA%\AgentEyes\plugins\<id>\
    plugin.json        (required - the manifest)
    ...anything else the plugin needs (scripts, exe, venv)
```

`plugin.json`:

```json
{
  "id": "qa-walk-companion",
  "name": "QA Walk Companion",
  "description": "Turns a QA walkthrough recording into a QA report with the bugs found.",
  "version": "1.0.0",
  "command": ["python", "run.py", "{dir}"]
}
```

- `id` - folder-safe identifier; also names the log file.
- `command` - the process to start, as an argument array. `{dir}` in any argument
  is replaced with the absolute path of the recording directory. The working
  directory is the plugin's own folder.
- Gotcha: reference scripts in the plugin folder as `.\\script.cmd`, not bare
  `script.cmd` - modern Windows excludes the working directory from the command
  search path (NoDefaultCurrentDirectoryInExePath).

## Per-plugin settings (optional)

A plugin can declare configurable values; they render in the Plugin Manager's
"Configure" dialog and persist per machine:

```json
{
  "id": "qa-walk-companion",
  "command": ["python", "run.py", "{dir}"],
  "settings": [
    { "key": "reportTitle", "label": "Report title", "type": "text",
      "default": "QA walkthrough", "description": "Heading of the generated report" },
    { "key": "fileIssues", "label": "File GitHub issues for bugs found", "type": "bool",
      "default": "false" }
  ]
}
```

- `type`: `text` (default) or `bool`. All values are strings ("true"/"false"
  for bools).
- The plugin process receives each value as an environment variable:
  `MQS_SETTING_REPORTTITLE`, `MQS_SETTING_FILEISSUES` (key uppercased,
  non-alphanumerics become `_`).
- Storage: `plugins\<id>.settings.json` NEXT TO the plugin folder - a registry
  update replaces the folder and must not wipe the configuration.

## What a plugin receives

The recording directory is the API. Stable files it can read:

| File | What |
|---|---|
| `manifest.json` | mode (video/audio/shot), timestamps, duration, mic, AI title/description |
| `recording.mp4` / `audio.wav` | the captured media |
| `audio_16k.wav` | 16 kHz mono audio (what Whisper read) |
| `transcript.txt` / `transcript.json` | the transcription (plain / with offsets) |
| `shots/*.png` | extracted key frames (video) or the screenshot |
| `thumb.jpg` / `thumb.png` | library thumbnail |
| `walkthrough.html` | the built walkthrough, when present |

## What a plugin produces

Write artifacts INTO the recording directory (or subfolders). They live with the
recording, survive renames, and ship with it when the folder is copied.

stdout/stderr are captured to `plugin-<id>.log` in the recording directory.
Exit code 0 = success; anything else is logged as a failure.

## Execution model

- Plugins run AFTER transcription completes, sequentially, newest recording first.
- Each plugin is its own process - a crash or hang cannot take the app down.
  Hard timeout: 10 minutes, then the process tree is killed.
- Enabled per user in the Plugin Manager. Nothing runs unless opted in.
- ASCII-only output recommended (logs render in Windows consoles).

## Installing plugins (issue #61)

Plugins are managed in the **Plugin Manager** (Settings > Manage plugins...): a
list of installed plugins, each with enable/disable, Configure, Remove, and an
"Update to X" badge when the catalog has a newer version. Two ways to add one:

- **Install from file** - pick a plugin `.zip` (or folder), or drag-and-drop it
  onto the window. A local install has no registry hash, so the manager shows the
  command the plugin will run and asks before enabling it (nothing runs until you
  enable it). The same zip-slip / plugin.json-at-root rules apply; a zip OF the
  folder (manifest one level down) is accepted too.
- **Browse catalog** - install/update from a registry.

The shared install/validate/remove logic lives in Core (`PluginPackage`), used by
both local installs and registry installs.

### Registry (issue #32)

The registry is a JSON file; the default is `plugins/registry.json` on the main
branch of the one consolidated public repo (`thefrederiksen/agenteyes-app`,
issue #186), and `PluginRegistryUrl` in config.json overrides it.

```json
{
  "plugins": [
    {
      "id": "qa-walk-companion",
      "name": "QA Walk Companion",
      "description": "QA report with bugs found from a walkthrough recording.",
      "version": "1.0.1",
      "zipUrl": "https://github.com/thefrederiksen/agenteyes-app/releases/download/plugins/qa-walk-companion-1.0.1.zip",
      "sha256": "<hex digest of the zip>"
    }
  ]
}
```

Install rules (no-fallback): the zip's SHA-256 must match the registry entry or
the install refuses with the exact mismatch; the zip must contain plugin.json at
its root; entries that escape the plugin folder (zip-slip) are rejected.
Updates: the catalog shows "Update to X" when the registry version is newer
than the installed one.

## Building and packaging a plugin

Plugin sources live in `plugins/<id>/` in this repo. The real ones so far are
PowerShell (no runtime to install on the user's machine). AgentEyes injects the
signed-in DevThrottle account's `DEVTHROTTLE_API_KEY` and
`DEVTHROTTLE_BASE_URL` into each plugin process, so plugins need no key of their
own and there is no alternate provider path:

- `plugins/qa-walk-companion/` - reads `manifest.json` + `transcript.json` and
  writes `qa-report.html` + `qa-bugs.json` (the bugs the tester reported).
- `plugins/doc-companion/` - reads `manifest.json` + `transcript.json` + the
  captured `shots/`, and writes `docs.html` + `docs.md` (step-by-step
  documentation with the nearest screenshot under each step).

To cut a registry release of a plugin:

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-plugin.ps1 <id>
```

It zips `plugins\<id>\` (plugin.json at the zip root), prints the SHA-256 the
installer verifies, and emits the ready-to-paste `registry.json` entry. Upload
the zip from `dist\plugins\` to the public releases repo and paste the entry into
`plugins/registry.json` there - that is the only publish step.

## Status

- List-based Plugin Manager + per-plugin Configure + local (file / drag-drop)
  install: done in #61.
- Migrating the built-in walkthrough build out of core into the QA Walk
  Companion plugin (regression-safe migration plan needed first): still open.

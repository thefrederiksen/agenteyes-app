# Vendored source provenance

AgentEyes copies ("steals") proven C# source from cc-director and adapts it, so the tool is
self-contained with no runtime dependency on any `cc-*` binary. This file records exactly what was
copied, from where, and when, so it can be re-synced deliberately rather than drifting silently.

Source repo: cc-director (D:\ReposFred\cc-director)
Source commit at copy time: c2f0c90 (2026-06-02)

| AgentEyes file            | Copied from (cc-director path)                                          | Status        | Adaptations |
|---------------------------|-------------------------------------------------------------------------|---------------|-------------|
| Audio/AudioCapture.cs     | playground/voice-chat/src/VoiceChat.Core/Pipeline/AudioCapture.cs       | COPIED+ADAPTED| device selection, WAV file output, peak LevelChanged event, device enumeration/resolve |
| Manifest.cs               | phone/CcRecorder/Recording/LocalManifest.cs                             | PATTERN ONLY  | reimplemented for AgentEyes fields (monitor/region/shots); same on-disk-manifest idea |
| (Phase 4) HtmlRenderer    | src/CcDirector.Avalonia/Helpers/MarkdownHtmlRenderer.cs                 | TODO          | to be copied when walkthrough assembly (Package.cs) is built |

## Re-sync procedure

1. `cd D:\ReposFred\cc-director && git log -1 --oneline -- <source path>` to see if the upstream changed.
2. Diff the upstream file against the adapted copy here.
3. Re-apply the adaptations listed above, then update the commit hash at the top of this file.

## Not vendored (replaced by self-contained .NET libraries instead)

- Python `cc-whisper` / `cc-transcribe`  -> Whisper.net (NuGet), in-process.
- Python `cc-image`                       -> SixLabors.ImageSharp (NuGet).
- Python `cc-video` frame extraction      -> net-new C# on decoded frames.

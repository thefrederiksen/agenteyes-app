# Manifest round-trip fixtures (issue #155)

Each file here is a `manifest.json` SHAPE that exists on real machines. `ManifestRoundTripTests`
loads every one of them, writes it back through `ManifestStore`, and asserts that no property was
lost or changed - which is what stops a future field addition (or removal) from quietly deleting
data that is already on disk.

They are shapes, not private data: the titles, descriptions, file names and paths were replaced with
neutral text. The property NAMES, types and nesting are exactly what AgentEyes writes.

| Fixture | The shape it pins |
|---------|-------------------|
| `legacy-shot.json` | the oldest record still on disk - a screenshot session, before `Imported`, the per-language transcript map, and every attempt counter existed |
| `legacy-video-98.json` | a video recording from before issue #98: `Transcript` only, no `Transcripts` map, no `ThumbAttempts` |
| `current-video.json` | today's full shape: AI cost, per-language transcripts, all three attempt counters, extracted frames, and the issue #152 `PostProcessing` journal |
| `pending-mux.json` | a recording stopped with the audio mux deferred (issue #77) - the record without which raw capture files are unrecoverable |
| `future-unknown-fields.json` | a manifest written by a NEWER AgentEyes: known fields plus properties this version has never heard of, at the top level (scalar, object, array). Round-tripping it must keep them - that is the issue #155 extension-data guarantee |

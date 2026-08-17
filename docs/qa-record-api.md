# qa-record REST API

The qa-record app runs in the system tray and hosts a localhost-only control API so any local
agent can drive it. Same idea as cc-director's control API.

- Base URL: `http://127.0.0.1:7882` (configurable in `%LOCALAPPDATA%\qa-record\config.json`)
- Bind: `127.0.0.1` only (not reachable off-machine). No auth (localhost trust).
- One recording at a time. `POST /record/start` returns `409` if already recording.
- The app must be running (tray). Launch it, or enable Run at login from the tray menu.

## Endpoints

| Method | Path              | Body / notes |
|--------|-------------------|--------------|
| GET    | `/`               | discovery - lists endpoints |
| GET    | `/health`         | `{ ok: true }` |
| GET    | `/status`         | `{ state, mode, source, elapsedSeconds, level, dir }` (state = idle/recording/finalizing) |
| GET    | `/devices`        | `{ monitors[], mics[], dshow[] }` |
| GET    | `/recordings`     | recent recordings with manifest summary |
| POST   | `/screenshot`     | `{ screen, region?: [x,y,w,h] }` -> `{ file }` |
| POST   | `/record/start`   | see below -> `{ status }` |
| POST   | `/record/shot`    | marker screenshot during a session -> `{ file }` |
| POST   | `/record/stop`    | -> `{ dir, file, durationSeconds, shots }` |

### POST /record/start body

```json
{
  "mode":   "video",        // "audio" | "video"
  "screen": 2,              // monitor index from /devices
  "source": "mixed",        // "mic" | "system" | "mixed"  (mixed = mic + system)
  "mic":    "Yeti",         // substring of a device name; required for mic/mixed
  "region": [2560,0,1280,720], // optional [x,y,w,h]; omit for full monitor
  "gate":   true,           // noise gate on the mic
  "micVol": 100,            // percent
  "sysVol": 70,             // percent
  "fps":    30              // video only
}
```

## Examples

PowerShell:
```powershell
$b = "http://127.0.0.1:7882"
Invoke-RestMethod "$b/status"
Invoke-RestMethod "$b/record/start" -Method Post -ContentType application/json `
  -Body (@{ mode='video'; screen=2; source='mixed'; mic='Yeti' } | ConvertTo-Json)
Start-Sleep 10
Invoke-RestMethod "$b/record/stop" -Method Post        # -> { dir, file, durationSeconds }
```

curl:
```bash
curl http://127.0.0.1:7882/status
curl -X POST http://127.0.0.1:7882/record/start -H "content-type: application/json" \
  -d '{"mode":"audio","screen":1,"source":"system"}'
curl -X POST http://127.0.0.1:7882/record/stop
```

## Verification

`tools\qa-record\api-smoke.ps1` starts the app in tray mode and exercises the whole API headlessly
(status transitions, screenshot, audio+video start/stop, 409 conflict, produced files). It runs as
part of `tools\qa-record\run-all.ps1`.

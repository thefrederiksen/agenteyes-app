---
name: agenteyes-control
description: Drive the running AgentEyes recorder conversationally through its loopback Control API using the local Python client (agenteyes_client). Read live state (status, recordings, captures, presets, devices) and run actions (start/stop a recording, take a screenshot/capture). Loopback only. Triggers on "/agenteyes-control", "agenteyes control", "drive the recorder", "start recording via api", "stop the recording", "what is AgentEyes recording", "list my recordings", "take a screenshot with AgentEyes".
---

# agenteyes-control - drive AgentEyes through its Control API

This skill lets an agent operate the running AgentEyes app conversationally. It does NOT talk
raw HTTP - it uses the local Python client `agenteyes_client` (issue #75) which wraps every loopback
Control API route, returns the JSON verbatim, and raises a single typed `AgentEyesApiError` on any
non-2xx response. The API binds `http://127.0.0.1:7882` (loopback only).

## When to use

- The user wants to know what the recorder is doing (status, recording list, captures, presets).
- The user wants to start or stop a recording, or take a screenshot/capture.
- An agent workflow needs to drive the recorder as a step.

## Prerequisites

- The AgentEyes app must be running (tray is fine):
  `src\AgentEyes.App\bin\Release\net8.0-windows10.0.19041.0\AgentEyesApp.exe --tray`
- Python 3.10+ on PATH (the client is standard-library only - no pip install needed).
- The client lives at `clients/python/agenteyes_client/`. Make it importable by adding `clients/python`
  to `sys.path` (no install step - it is a local importable module).

## Usage

Run Python that imports the client. Always add `clients/python` to `sys.path` first:

```python
import os, sys
sys.path.insert(0, os.path.join("clients", "python"))   # from the repo root
from agenteyes_client import AgentEyesClient, AgentEyesApiError

agenteyes = AgentEyesClient()                       # defaults to http://127.0.0.1:7882

# --- read state ---
print(agenteyes.version())                    # e.g. "0.8.2"
print(agenteyes.status())                     # {"State": "idle"|"recording", ...}
print(agenteyes.recordings(limit=5))          # {"total": N, "items": [...]}
print(len(agenteyes.captures()), "captures")
print(len(agenteyes.presets()), "presets")

# --- run an action ---
agenteyes.record_start(mode="audio", screen=1, source="system")   # start a recording
assert agenteyes.is_recording()                                    # status State == "recording"
res = agenteyes.record_stop()                                      # stop; res["File"] is the output
print("saved:", res["File"])

# screenshot / capture without recording
shot = agenteyes.screenshot(screen=1)         # {"file": "...png"}
cap  = agenteyes.capture(mode="full", screen=1)
```

### Method surface (one method per route)

- System/discovery: `health()`, `version()`, `status()`, `is_recording()`, `devices()`, `discover()`
- Recordings (read): `recordings(limit=50, offset=0)`, `recording(id)`, `shots(id)`, `transcript(id)`
- Captures: `captures()`, `capture_info()`
- Presets: `presets()`
- Live actions: `screenshot(screen=1, region=None)`, `capture(mode, screen=None, region=None)`,
  `record_start(**opts)`, `record_shot()`, `record_stop()`

### Errors

Any non-2xx response raises `AgentEyesApiError` with `.status` (HTTP code), `.code` (short string, e.g.
`not_found`, `conflict`, `bad_request`, `unavailable`), and `.message`. Handle it, do not retry blindly:

```python
try:
    agenteyes.recording("does-not-exist")
except AgentEyesApiError as e:
    print(e.status, e.code, e.message)   # 404 not_found ...
```

Common codes: starting while already recording -> `409 conflict`; stopping when idle ->
`409 conflict`; unknown recording id / missing transcript -> `404 not_found`; bad capture mode ->
`400 bad_request`.

## Quick driver

`drive.py` in this folder is a runnable example the skill uses for a read + an action:

```bash
python .claude/skills/agenteyes-control/drive.py status        # read: print live status JSON
python .claude/skills/agenteyes-control/drive.py roundtrip     # action: audio start -> stop round-trip
python .claude/skills/agenteyes-control/drive.py screenshot    # action: take one screenshot
```

It prints plain ASCII and exits 0 on success, non-zero (with the `AgentEyesApiError` detail) on failure.

## Safety / posture

Loopback only (`127.0.0.1`). This skill only exercises the existing API; it does not weaken the
visible / controllable posture.

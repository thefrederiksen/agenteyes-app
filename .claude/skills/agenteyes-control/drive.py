"""agenteyes-control skill driver: read live state or run an action via agenteyes_client.

Drives the running AgentEyes app through the local Python client (issue #75).
Plain ASCII output; exits 0 on success, 1 on an AgentEyesApiError (with detail).

Usage (from the repo root):
    python .claude/skills/agenteyes-control/drive.py status        # read: print live status JSON
    python .claude/skills/agenteyes-control/drive.py roundtrip     # action: audio start -> stop
    python .claude/skills/agenteyes-control/drive.py screenshot    # action: take one screenshot

The first positional after the command may override the base URL
(default http://127.0.0.1:7882).
"""

import json
import os
import sys

# Locate the repo root from this file (.claude/skills/agenteyes-control/drive.py -> repo root) and add
# clients/python so agenteyes_client imports without an install step.
_REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
sys.path.insert(0, os.path.join(_REPO_ROOT, "clients", "python"))

from agenteyes_client import DEFAULT_BASE_URL, AgentEyesApiError, AgentEyesClient  # noqa: E402


def _read_status(agenteyes):
    """A READ: print the live status JSON and the recordings/captures counts."""
    status = agenteyes.status()
    print("version: {0}".format(agenteyes.version()))
    print("status: {0}".format(json.dumps(status)))
    print("recording: {0}".format(agenteyes.is_recording()))
    print("recordings: {0}".format(agenteyes.recordings(limit=1).get("total")))
    print("captures: {0}".format(len(agenteyes.captures())))
    return 0


def _roundtrip(agenteyes):
    """An ACTION: start an audio recording (system source, deterministic headless),
    confirm recording=true, stop it, confirm recording=false, print the output file."""
    print("before: recording={0}".format(agenteyes.is_recording()))
    agenteyes.record_start(mode="audio", screen=1, source="system")
    print("during: recording={0}".format(agenteyes.is_recording()))
    res = agenteyes.record_stop()
    print("after:  recording={0}".format(agenteyes.is_recording()))
    print("saved:  {0}".format(res.get("File")))
    return 0


def _screenshot(agenteyes):
    """An ACTION: take a single full-monitor screenshot and print the file path."""
    res = agenteyes.screenshot(screen=1)
    print("screenshot: {0}".format(res.get("file")))
    return 0


_COMMANDS = {
    "status": _read_status,
    "roundtrip": _roundtrip,
    "screenshot": _screenshot,
}


def main(argv):
    if len(argv) < 2 or argv[1] not in _COMMANDS:
        print("usage: drive.py {0} [base_url]".format("|".join(sorted(_COMMANDS))))
        return 2
    command = argv[1]
    base_url = argv[2] if len(argv) > 2 else DEFAULT_BASE_URL
    agenteyes = AgentEyesClient(base_url=base_url)
    try:
        return _COMMANDS[command](agenteyes)
    except AgentEyesApiError as err:
        print("API error: status={0} code={1} message={2}".format(err.status, err.code, err.message))
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))

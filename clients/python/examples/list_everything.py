"""Example: connect to a running AgentEyes app and print a quick summary.

Prints the app version, then the counts of recordings, captures, and presets,
then exits 0 (issue #75 AC4). Run with the app running:

    python clients/python/examples/list_everything.py

Optional first argument overrides the base URL (default http://127.0.0.1:7882).
"""

import os
import sys

# Make the sibling agenteyes_client package importable when run directly from the repo
# (no install step - the package is a local importable module, issue #75 A1).
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from agenteyes_client import DEFAULT_BASE_URL, AgentEyesApiError, AgentEyesClient  # noqa: E402


def main():
    base_url = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_BASE_URL
    client = AgentEyesClient(base_url=base_url)

    try:
        version = client.version()
        recordings = client.recordings(limit=1000)
        captures = client.captures()
        presets = client.presets()
    except AgentEyesApiError as err:
        print("API error: status={0} code={1} message={2}".format(err.status, err.code, err.message))
        return 1

    rec_total = recordings.get("total", len(recordings.get("items", [])))
    print("version: {0}".format(version))
    print("recordings: {0}".format(rec_total))
    print("captures: {0}".format(len(captures)))
    print("presets: {0}".format(len(presets)))
    return 0


if __name__ == "__main__":
    sys.exit(main())

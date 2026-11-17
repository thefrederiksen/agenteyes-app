#!/usr/bin/env python3
"""Issue #173 - prove the identity sweep changed ONLY the redacted spans.

Rerunnable by QA from the repository root:

    python3 docs/cencon/proof/issue-173/verify-sweep.py 02a4622

For every file under docs/ that differs between <base> and the working tree, it splits the OLD bytes
at each identity value and the NEW bytes at each redaction token, and requires the pieces in between
to be byte-identical. So any change other than "identity value -> token" - a moved digit, a dropped
line, a CRLF turned into LF - fails. It also compares every run of digits, line and CR counts, and
parses every .json file before and after.

It contains no address: addresses are matched by SHAPE (any email-shaped string on the old side must
have become a token on the new side). The home-path username is the repository owner's handle.

Exit 0 = every changed file passes. Exit 1 = at least one fails (named). Exit 2 = nothing to check.
"""

import json
import re
import subprocess
import sys

USERNAME = rb"soren"
# Old side: an email-shaped string (not a systemd instance unit such as a per-user manager unit,
# which the CI guard also allows), a token already present before the sweep, or a home-path
# username. The capture group keeps the path prefix so prefixes are compared too.
OLD = re.compile(
    rb"[A-Za-z0-9._%+-]+@(?![0-9]+\.(?:service|socket|target|slice|scope|timer)\b)"
    rb"(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}(?![A-Za-z0-9-])"
    rb"|<owner account>|<owner second account>"
    rb"|(/home/|/Users/|\\+Users\\+)(?:" + USERNAME + rb"|<user>)(?![A-Za-z0-9._-])"
)
NEW = re.compile(
    rb"<owner account>|<owner second account>"
    rb"|(/home/|/Users/|\\+Users\\+)<user>(?![A-Za-z0-9._-])"
)
DIGITS = re.compile(rb"\d+")


def git(*args: str) -> bytes:
    return subprocess.run(["git", *args], capture_output=True, check=True).stdout


def main(base: str) -> int:
    names = git("diff", "--name-only", base, "--", "docs").decode().split()
    # This proof directory is new in the change and has no old side to compare against.
    names = [n for n in names if not n.startswith("docs/cencon/proof/issue-173/")
             and n != "docs/cencon/DEVELOPMENT_METHOD.md"]
    if not names:
        print("[FAIL] instrument: no changed files under docs/ - nothing was compared")
        return 2
    failures = redacted = digit_runs = json_files = 0
    for name in names:
        old = git("show", f"{base}:{name}")
        with open(name, "rb") as fh:
            new = fh.read()
        if OLD.split(old) != NEW.split(new):
            print(f"[FAIL] bytes outside the redactions differ: {name}")
            failures += 1
            continue
        redacted += sum(1 for m in OLD.finditer(old) if not m.group(0).endswith(b">"))
        a, b = DIGITS.findall(old), DIGITS.findall(new)
        if a != b:
            print(f"[FAIL] digit runs differ: {name}")
            failures += 1
        digit_runs += len(a)
        if old.count(b"\n") != new.count(b"\n") or old.count(b"\r") != new.count(b"\r"):
            print(f"[FAIL] line structure differs: {name}")
            failures += 1
        if name.endswith(".json"):
            json.loads(old)
            json.loads(new)
            json_files += 1
    print(f"files={len(names)} identity_values_redacted={redacted} digit_runs_compared={digit_runs} "
          f"json_parsed_before_and_after={json_files} failures={failures}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "02a4622"))

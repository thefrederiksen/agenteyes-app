#!/usr/bin/env python3
"""Issue #173 - prove the identity sweep changed ONLY the redacted spans.

Rerunnable by QA from the repository root:

    python3 docs/cencon/proof/issue-173/verify-sweep.py 02a4622 [owner-file second-file]

For every file under docs/ that differs between <base> and the working tree, it splits the OLD bytes
at each identity value and the NEW bytes at each redaction token, and requires the pieces in between
to be byte-identical. So any change other than "identity value -> token" - a moved digit, a dropped
line, a CRLF turned into LF - fails. It also compares every run of digits, line and CR counts, and
parses every .json file before and after.

It also PAIRS each old value with the token that replaced it (QA round 1 found the split alone was
green when one valid token was swapped for the other, because a split forgets which token stood where):
  * a home path must become the same prefix + <user>, and an existing token must stay itself;
  * an address must become a token, and each distinct address must map to ONE token and each token
    from ONE address, across every file - so a single swapped occurrence is red;
  * given owner-file and second-file (each holding one address, kept OUTSIDE the repository and never
    printed), the mapping must be exactly owner -> <owner account> and second -> <owner second
    account>, which also catches a swap of every occurrence at once. Without them that wholesale
    swap is NOT caught, and the output says so.

Home-path prefixes are matched across a console line wrap (a CRLF or LF, plus any indentation, between
any two characters), because one sweep redaction (#173 QA round 1) was C:\\Us / ers\\<user> split by
a PowerShell wrap.

It contains no address: addresses are matched by SHAPE. The home-path username is the repository
owner's handle.

Exit 0 = every changed file passes. Exit 1 = at least one fails (named). Exit 2 = nothing to check.
"""

import json
import re
import subprocess
import sys

USERNAME = rb"soren"
OWNER_TOKEN = b"<owner account>"
SECOND_TOKEN = b"<owner second account>"
TOKENS = (OWNER_TOKEN, SECOND_TOKEN)
WRAP = rb"(?:\r?\n[ \t]*)?"


def wrappable(literal: str) -> bytes:
    """A regex for literal that also matches it broken by a console line wrap between any two chars."""
    return WRAP.join(re.escape(c.encode()) for c in literal)


# The three home-path prefixes, each tolerant of a wrap inside it.
HOME_PREFIX = (rb"(" + wrappable("/home/") + rb"|" + wrappable("/Users/") + rb"|(?:\\" + WRAP + rb")+"
               + wrappable("Users") + WRAP + rb"(?:\\" + WRAP + rb")+)")
# Old side: an email-shaped string (not a systemd instance unit such as a per-user manager unit,
# which the CI guard also allows), a token already present before the sweep, or a home-path
# username. The capture group keeps the path prefix so prefixes are compared too.
OLD = re.compile(
    rb"[A-Za-z0-9._%+-]+@(?![0-9]+\.(?:service|socket|target|slice|scope|timer)\b)"
    rb"(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}(?![A-Za-z0-9-])"
    rb"|<owner account>|<owner second account>"
    rb"|" + HOME_PREFIX + rb"(?:" + USERNAME + rb"|<user>)(?![A-Za-z0-9._-])"
)
NEW = re.compile(
    rb"<owner account>|<owner second account>"
    rb"|" + HOME_PREFIX + rb"<user>(?![A-Za-z0-9._-])"
)
DIGITS = re.compile(rb"\d+")


def git(*args: str) -> bytes:
    return subprocess.run(["git", *args], capture_output=True, check=True).stdout


def pair_failure(old: bytes, new: bytes, mapping: dict, values: dict):
    """Why the replacement old -> new is wrong, or None. Records address -> token in mapping."""
    if old in TOKENS:
        return None if new == old else "an existing token was changed"
    if old.endswith(b"<user>") or old.lower().endswith(USERNAME):
        return None if new.endswith(b"<user>") and new not in TOKENS else "a home path did not become <user>"
    if new not in TOKENS:
        return "an address did not become a token"
    key = old.lower()
    if values:
        want = values.get(key)
        if want is None:
            return "an address that is neither the owner's nor the second was redacted"
        if new != want:
            return "an address became the OTHER token"
    seen = mapping.setdefault(key, set())
    seen.add(new)
    return None


def main(base: str, owner_file: str = None, second_file: str = None) -> int:
    values = {}
    if owner_file and second_file:
        with open(owner_file, "rb") as fh:
            values[fh.read().strip().lower()] = OWNER_TOKEN
        with open(second_file, "rb") as fh:
            values[fh.read().strip().lower()] = SECOND_TOKEN
    mapping = {}
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
        for o, n in zip(OLD.finditer(old), NEW.finditer(new)):
            why = pair_failure(o.group(0), n.group(0), mapping, values)
            if why:
                line = old.count(b"\n", 0, o.start()) + 1
                print(f"[FAIL] {why}: {name}:{line}")
                failures += 1
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
    for key, tokens in mapping.items():
        if len(tokens) != 1:
            print("[FAIL] one address was replaced by BOTH tokens (a token was swapped somewhere)")
            failures += 1
    targets = [next(iter(t)) for t in mapping.values() if len(t) == 1]
    if len(targets) != len(set(targets)):
        print("[FAIL] two different addresses were replaced by the SAME token")
        failures += 1
    print(f"files={len(names)} identity_values_redacted={redacted} digit_runs_compared={digit_runs} "
          f"json_parsed_before_and_after={json_files} distinct_addresses={len(mapping)} failures={failures}")
    if values and failures:
        print(f"[FAIL] mapping checked against the given owner and second values: NOT exact - "
              f"{failures} failure(s), see the [FAIL] lines above")
    elif values:
        print("mapping checked against the given owner and second values: exact")
    else:
        print("WARNING no owner/second value files given: per-occurrence swaps are caught by the "
              "one-address-one-token rule, a swap of EVERY occurrence at once is not")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(*(sys.argv[1:] or ["02a4622"])))

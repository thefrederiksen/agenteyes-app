#!/usr/bin/env python3
"""Issue #173: fail when committed evidence carries an email address or a named home directory.

Scans every file (text AND binary, byte for byte) under the evidence directories and fails when
one holds an email-shaped string, or a home-directory path whose user segment is a real name rather
than a placeholder such as <user>. It matches SHAPES, never a particular value: this file must never
contain a real address or username, or the guard is the leak with a test around it.

Single-line detection only. Every file is scanned AS WRITTEN: a match must sit on one line. Text
stored as UTF-16 (any run of 4+ ASCII characters each followed by a NUL byte, which is how Windows
tools write text and how .NET binaries hold their strings; UTF-16BE matches the same run one byte
along) is decoded and scanned the same way, wherever it sits in a file.

What it MISSES, by design: an address or a home path that a console WRAPPED across two lines (QA
found C:\\Us / ers\\<name>\\... in a PowerShell error, issue #173 round 1; that occurrence is redacted).
Wrap-aware detection is issue #218. It was built and measured inside #173 (rounds 2 and 3) and
failed QA three times on the width model of every console this repository's tools use, so the owner
split it out; the detector, its corpus and its runner are parked on branch issue-218-wrap-detection
for #218 to start from. Also missed: UTF-32, compressed or encrypted data, base64.

What it does NOT do, stated so nobody reads more into a green run than it says:
  * It does not undo the past. Addresses already in git HISTORY stay there; the repository is
    public, so they are already out. Issue #173 deliberately did not rewrite history. This guard
    stops the NEXT leak, not the last one.
  * Home paths are checked by shape only: /home/<name>, /Users/<name> and <drive>:\\Users\\<name>
    (any number of backslashes, so the JSON-escaped form counts). A username anywhere ELSE - a pipe
    name, a host name, an ls owner column, a temp-directory segment - is not checked (issue #212
    tracks host names).
  * It cannot read text inside an image. A screenshot showing an address passes.

Allowances - each is a shape that can never be a person's address, with the reason beside it:
  * Reserved names (RFC 2606 / RFC 6761): example.com / .net / .org and anything under the
    .example, .invalid, .test and .localhost top-level domains. These are the stand-ins a seat
    should USE when an address has to appear (main 5e6e727 carries a synthetic git identity on
    .invalid in issue-196's proof script, and the AccountStateTests fixture uses example.com).
  * systemd unit names, which have the SHAPE of an address but whose last label is a unit type, not
    a top-level domain: a name, an "@", an instance, then .service / .socket / .device / .mount /
    .automount / .swap / .path / .timer / .slice / .scope. None of those is a delegated TLD (IANA
    root zone list, version 2026111700). .target IS a delegated TLD (a brand TLD), so a .target
    name is allowed only with a NUMERIC instance (the per-user manager form, uid then unit type),
    which no mailbox domain can be. The per-user manager unit appears in ordinary Linux
    service-status evidence (issue-102's systemctl status, on main).
  * Home-path user segments that name no person: a placeholder in angle brackets (<user>), an
    elision ("..."), the hosted CI accounts "runner" and "runneradmin" (GitHub's, in CI logs), and
    the throwaway accounts this repository's Linux container tests create: "tester" (made by
    scripts/linux/release-smoke.sh with useradd) and "qauser" (the #194 QA container account,
    docs/cencon/proof/issue-194/qa/qa-evidence.txt). A /home/ that is not at the start of a path
    ($SCRATCH/runs/x/home/.config, a sandbox HOME) is not a home directory and is not matched.
There is deliberately no per-file or per-line allowance: an address that is genuinely needed in
evidence is written as <owner account> (or a reserved-domain stand-in), not exempted.

Output names the file and line of each hit and prints the hit MASKED, so a CI log on a public
repository does not republish the address it caught.

Exit codes: 0 clean, 1 an address or a named home path was found, 2 the instrument is broken (a scanned
directory is missing or empty - an empty scan certifies nothing).

Usage: python3 scripts/check-proof-identity.py [repo-root]
"""

import os
import re
import sys

# Directories whose contents are evidence copied between seats and served publicly.
EVIDENCE_DIRS = ("docs/cencon/proof", "docs/cencon/review")

# Shape of an address: local part, "@" (or its URL-encoded form), a dotted domain, an alphabetic TLD.
ADDRESS = re.compile(
    rb"[A-Za-z0-9._%+-]+(?:@|%40)((?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,})(?![A-Za-z0-9-])"
)

# RFC 2606 / RFC 6761 reserved names: never a real mailbox.
RESERVED_DOMAIN = re.compile(
    rb"(?:^|\.)(?:example\.(?:com|net|org)|example|invalid|test|localhost)$",
    re.IGNORECASE,
)

# systemd unit types that are NOT delegated top-level domains: "name@instance.<type>" is a unit name.
SYSTEMD_UNIT_TLD = re.compile(
    rb"\.(?:service|socket|device|mount|automount|swap|path|timer|slice|scope)$", re.IGNORECASE
)
# Any unit type, .target included (a delegated brand TLD), with a numeric instance: "user@1000.service".
SYSTEMD_INSTANCE = re.compile(
    rb"^[0-9]+\.(?:service|socket|target|slice|scope|timer|mount|automount|path|swap|device)$"
)


# A home directory: the prefix, then the user segment (group 1). Forward-slash "/Users/" is matched
# case-sensitively so a URL such as /api/users/me is not a home path; the Windows forms are not.
HOME_PATH = re.compile(
    rb"(?:(?<![A-Za-z0-9._$-])/home/|/Users/|(?i:\\+users\\+)|(?i:[a-z]:/users/))"
    rb"(<[^<>\s]*>|[A-Za-z0-9._$%-]+)"
)
HOME_USER_ALLOWED = re.compile(rb"^(?:<[^<>\s]*>|\.+|runner|runneradmin|tester|qauser)$")

def mask_home(hit: bytes) -> str:
    """Show a home-path hit without republishing the name: /home/s***"""
    text = hit.decode("ascii", "replace")
    return f"{text[:1]}***"


def mask(hit: bytes) -> str:
    """Show the shape of a hit without republishing it: s***@***.com"""
    text = hit.decode("ascii", "replace")
    sep = "@" if "@" in text else "%40"
    local, _, domain = text.partition(sep)
    tld = domain.rsplit(".", 1)[-1]
    return f"{local[:1]}***{sep}***.{tld}"


def is_allowed(domain: bytes) -> bool:
    """True when the domain part of an address-shaped string cannot be a real mailbox domain."""
    return bool(RESERVED_DOMAIN.search(domain) or SYSTEMD_UNIT_TLD.search(domain)
                or SYSTEMD_INSTANCE.match(domain))


def address_hits(text: bytes):
    """(start, end, masked) for every disallowed address-shaped string in text."""
    for match in ADDRESS.finditer(text):
        if not is_allowed(match.group(1)):
            yield match.start(), match.end(), "email-shaped string " + mask(match.group(0))


def home_hits(text: bytes):
    """(start, end, masked) for every home path whose user segment names someone."""
    for match in HOME_PATH.finditer(text):
        user = match.group(1)
        if not HOME_USER_ALLOWED.match(user):
            yield match.start(), match.end(), "home path naming user " + mask_home(user)


def text_hits(data: bytes):
    """(offset, description) for every hit in data, as written (single line)."""
    return [(start, shown) for start, _end, shown in [*address_hits(data), *home_hits(data)]]


# Text stored as UTF-16LE: 4+ ASCII characters (printable, tab, CR, LF), each followed by a NUL.
# UTF-16BE text matches too, one byte along (its NUL comes first), so both byte orders are read.
UTF16LE_RUN = re.compile(rb"(?:[\t\n\r\x20-\x7e]\x00){4,}")


def utf16_runs(data: bytes):
    """(original offset of the run, decoded ASCII bytes) for every run of UTF-16 text in data."""
    for m in UTF16LE_RUN.finditer(data):
        yield m.start(), m.group(0)[0::2]


def file_hits(data: bytes):
    """(line, description) for every hit in one file, as written and in any UTF-16 text it holds. Line numbers count LF bytes, so they hold for UTF-16LE text too."""
    found = []
    for offset, shown in text_hits(data):
        found.append((data.count(b"\n", 0, offset) + 1, shown))
    for run_start, decoded in utf16_runs(data):
        for offset, shown in text_hits(decoded):
            original = run_start + 2 * offset
            hit = (data.count(b"\n", 0, original) + 1, "UTF-16 text: " + shown)
            if hit not in found:
                found.append(hit)
    return found


def scan(root: str) -> int:
    files = 0
    total_bytes = 0
    hits = []
    for rel_dir in EVIDENCE_DIRS:
        base = os.path.join(root, rel_dir)
        if not os.path.isdir(base):
            print(f"[FAIL] instrument: {rel_dir} does not exist under {root} - nothing was scanned")
            return 2
        dir_files = 0
        for dirpath, _dirnames, filenames in os.walk(base):
            for name in sorted(filenames):
                path = os.path.join(dirpath, name)
                with open(path, "rb") as fh:
                    data = fh.read()
                files += 1
                dir_files += 1
                total_bytes += len(data)
                rel = os.path.relpath(path, root).replace(os.sep, "/")
                for line, shown in file_hits(data):
                    hits.append((rel, line, shown))
        if dir_files == 0:
            print(f"[FAIL] instrument: {rel_dir} holds no files - an empty scan certifies nothing")
            return 2

    print(f"[check-proof-identity] scanned {files} files, {total_bytes} bytes under {', '.join(EVIDENCE_DIRS)}")
    if hits:
        for rel, line, shown in hits:
            print(f"[FAIL] {rel}:{line}: {shown}")
        print(f"[FAIL] {len(hits)} identity hit(s) in committed evidence. Replace an address with "
              "<owner account> (or a reserved-domain stand-in such as user@example.com) and a "
              "home-path user with <user> - see the evidence-identity rule in "
              "docs/cencon/DEVELOPMENT_METHOD.md. Issue #173.")
        return 1
    print("[OK] no email-shaped string and no named home path in committed evidence "
          "(single-line scan; wrapped shapes are issue #218)")
    return 0


if __name__ == "__main__":
    repo_root = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
    sys.exit(scan(repo_root))

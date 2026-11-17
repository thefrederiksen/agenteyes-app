#!/usr/bin/env python3
"""Issue #173: fail when committed evidence carries an email address or a named home directory.

Scans every file (text AND binary, byte for byte) under the evidence directories and fails when
one holds an email-shaped string, or a home-directory path whose user segment is a real name rather
than a placeholder such as <user>. It matches SHAPES, never a particular value: this file must never
contain a real address or username, or the guard is the leak with a test around it.

Line wraps. A console wraps long output at its width, so a path or an address can be split across
two lines (QA found C:\\Us / ers\\<name>\\... in a PowerShell error that every line-by-line search
missed). Every file is therefore scanned twice: as written, and as a JOINED view with each line break
(and the indentation after it) removed. A joined-view hit that spans a break is reported as WRAPPED,
unless the part before the break was already a complete match on its own (then the joined view is
only gluing the next line onto a finished string, and the as-written scan has already judged it).

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
  * Reserved example domains (RFC 2606 / RFC 6761): example.com / .net / .org and anything under
    the .example, .invalid, .test and .localhost top-level domains. These are the stand-ins a seat
    should USE when an address has to appear.
  * systemd template-instance units such as the per-user manager unit (a name, an "@", the numeric
    uid, then ".service"): the part after the "@" is a number plus a unit type, not a domain, and
    it appears in ordinary Linux service-status evidence.
  * Home-path user segments that name no person: a placeholder in angle brackets (<user>), an
    elision ("..."), and the hosted CI accounts "runner" and "runneradmin" (GitHub's, in CI logs).
    A /home/ that is not at the start of a path ($SCRATCH/runs/x/home/.config, a sandbox HOME) is
    not a home directory and is not matched.
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

RESERVED_DOMAIN = re.compile(
    rb"(?:^|\.)(?:example\.(?:com|net|org)|example|invalid|test|localhost)$",
    re.IGNORECASE,
)

SYSTEMD_INSTANCE = re.compile(
    rb"^[0-9]+\.(?:service|socket|target|slice|scope|timer|mount|automount|path|swap|device)$"
)


# A home directory: the prefix, then the user segment (group 1). Forward-slash "/Users/" is matched
# case-sensitively so a URL such as /api/users/me is not a home path; the Windows forms are not.
HOME_PATH = re.compile(
    rb"(?:(?<![A-Za-z0-9._$-])/home/|/Users/|(?i:\\+users\\+)|(?i:[a-z]:/users/))"
    rb"(<[^<>\s]*>|[A-Za-z0-9._$%-]+)"
)
HOME_USER_ALLOWED = re.compile(rb"^(?:<[^<>\s]*>|\.+|runner|runneradmin)$")

# A line break as a console wraps one: CRLF or LF, then any indentation the next line carries.
LINE_BREAK = re.compile(rb"\r?\n[ \t]*")


def joined_view(data: bytes):
    """Return (joined bytes, offsets, breaks): every line break removed; offsets[i] is the original
    offset of joined byte i; breaks holds the joined offsets where a break was removed."""
    parts = []
    offsets = []
    breaks = set()
    pos = 0
    length = 0
    for m in LINE_BREAK.finditer(data):
        chunk = data[pos:m.start()]
        parts.append(chunk)
        offsets.extend(range(pos, m.start()))
        length += len(chunk)
        breaks.add(length)
        pos = m.end()
    parts.append(data[pos:])
    offsets.extend(range(pos, len(data)))
    offsets.append(len(data))
    return b"".join(parts), offsets, breaks


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
    return bool(RESERVED_DOMAIN.search(domain) or SYSTEMD_INSTANCE.match(domain))


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


def complete_on_its_own(text: bytes) -> bool:
    """True when text, the part of a joined-view hit before its first line break, is already a
    whole address or home path - so the joined view merely glued the next line onto it."""
    if ADDRESS.fullmatch(text):
        return True
    return bool(HOME_PATH.fullmatch(text))


def file_hits(data: bytes):
    """(line, description) for every hit in one file, as written and across line wraps."""
    found = []
    for start, _end, shown in [*address_hits(data), *home_hits(data)]:
        found.append((data.count(b"\n", 0, start) + 1, shown))
    joined, offsets, breaks = joined_view(data)
    if not breaks:
        return found
    for start, end, shown in [*address_hits(joined), *home_hits(joined)]:
        inner = [b for b in breaks if start < b < end]
        if not inner:
            continue
        head = joined[start:min(inner)]
        if complete_on_its_own(head):
            continue
        original = offsets[start]
        found.append((data.count(b"\n", 0, original) + 1, "WRAPPED across a line break: " + shown))
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
    print("[OK] no email-shaped string and no named home path in committed evidence, "
          "as written or across line wraps")
    return 0


if __name__ == "__main__":
    repo_root = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
    sys.exit(scan(repo_root))

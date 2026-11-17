#!/usr/bin/env python3
"""Issue #173: fail when committed evidence carries an email address or a named home directory.

Scans every file (text AND binary, byte for byte) under the evidence directories and fails when
one holds an email-shaped string, or a home-directory path whose user segment is a real name rather
than a placeholder such as <user>. It matches SHAPES, never a particular value: this file must never
contain a real address or username, or the guard is the leak with a test around it.

Line wraps. A console wraps long output at its width, so a path or an address can be split across
lines (QA found C:\\Us / ers\\<name>\\... in a PowerShell error that every line-by-line search
missed). Every file is therefore scanned twice: as written, and as a JOINED view in which SOME line
breaks are removed. Removing every break (the round-2 guard) cannot tell a console wrap from two
unrelated lines: "import pytest" followed by a decorator line reads as an address. So a break is
joined only when it has the structure of a hard console wrap:
  * the next line starts at column 0 with a visible character (a console continues a wrapped line
    at column 0; an indented or blank next line is a new line, not a continuation);
  * whitespace before the break is ignored (a console capture pads a line to its width);
  * TIER 1, mid-word: the character before the break and the one after it are both word characters
    (letter, digit, "_" or "-") - the break falls INSIDE a word, which a person ending a line almost
    never does and a console does all the time. Joined at any line length.
  * TIER 2, at punctuation: any other visible pair (a line ending in "/" or "\\", a line starting with
    "@" or "/") is joined only when the line before the break is at least WRAP_WIDTH (80) columns
    long, padding included. Lines that start with "@" or "/" are ordinary in code and listings
    (decorators, mentions, scoped packages, ls output), so a short line before them is a new line.
The break may be CRLF, LF or a lone CR. A joined-view hit that spans a joined break is reported as
WRAPPED, unless the part before the break was already a complete match on its own (then the joined
view is only gluing the next line onto a finished string, which the as-written scan judged) - except
an ALLOWED home path (/home/runner) whose next line carries on with a path separator, which is
reported, since that is a name that merely begins with an allowed one.
Text stored as UTF-16 (any run of 4+ ASCII characters each followed by a NUL byte, which is how
Windows tools write text and how .NET binaries hold their strings; UTF-16BE matches the same run one
byte along) is decoded and scanned the same way, wherever it sits in a file.

What the wrap rule MISSES, by design:
  * a wrap that lands next to punctuation ("C:\\Users\\" | "name", "user" | "@host") on a console
    narrower than 80 columns;
  * a wrap whose continuation is indented (a formatter such as PowerShell Format-List indents its
    continuation lines). A home path split after its prefix is still caught as written, because the
    partial name is itself a name; a split INSIDE the prefix or inside an address is not;
  * an allowed home user (runner) followed by a wrapped name with no path separator after it on the
    next line; an address whose reserved domain is extended by the next line;
  * any other encoding: UTF-32, compressed or encrypted data, base64.
What it FLAGS that is not a leak, by design: a line of 80+ columns that ends at a home prefix or in
the middle of a word, followed by a line that completes a home path or an address. The committed
corpus under tests/evidence-identity-corpus/ pins every one of these, both ways.

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
  * systemd unit names (ported from #173 round 4): a last label of .service / .socket / .device /
    .mount / .automount / .swap / .path / .timer / .slice / .scope is a unit type, not a delegated
    TLD; .target IS a delegated (brand) TLD, so a .target name is allowed only with a NUMERIC
    instance, as in the per-user manager unit.
  * Home-path user segments that name no person: a placeholder in angle brackets (<user>), an
    elision ("..."), the hosted CI accounts "runner" and "runneradmin" (GitHub's, in CI logs), and
    the throwaway Linux container test accounts "tester" (scripts/linux/release-smoke.sh) and
    "qauser" (the #194 QA container) - ported from #173 round 4.
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

# systemd unit types that are NOT delegated top-level domains: "name@instance.<type>" is a unit name
# (ported from #173 round 4; IANA root zone list version 2026111700).
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

# A line break - CRLF, LF or a lone CR - with any whitespace before it (console padding) in group 1.
LINE_BREAK = re.compile(rb"([ \t]*)(\r\n|\r|\n)")
# A tier-1 break falls between two of these: inside a word.
WORD_BYTES = frozenset(b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-")
# A tier-2 break (beside punctuation) is joined only after a line at least this wide.
WRAP_WIDTH = 80
WHITESPACE = frozenset(b" \t\r\n")


def is_console_wrap(data: bytes, line_start: int, match) -> bool:
    """True when the break `match`, ending the line that began at line_start, has the structure of a
    hard console wrap (see the module docstring): column-0 continuation, then mid-word at any width
    or beside punctuation at WRAP_WIDTH or more."""
    content_end = match.start()
    nxt = match.end()
    if content_end <= line_start or nxt >= len(data):
        return False
    before = data[content_end - 1]
    after = data[nxt]
    if after in WHITESPACE:
        return False
    if before in WORD_BYTES and after in WORD_BYTES:
        return True
    return match.start(2) - line_start >= WRAP_WIDTH


def joined_view(data: bytes):
    """Return (joined bytes, offsets, breaks): every console-wrap break (and the padding before it)
    removed; offsets[i] is the original offset of joined byte i; breaks holds the joined offsets
    where a break was removed. Breaks that are not console wraps stay in place."""
    parts = []
    offsets = []
    breaks = set()
    pos = 0
    length = 0
    line_start = 0
    for m in LINE_BREAK.finditer(data):
        joined = is_console_wrap(data, line_start, m)
        line_start = m.end()
        if not joined:
            continue
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


def already_judged(head: bytes, tail: bytes) -> bool:
    """True when head, the part of a joined-view hit before its first line break, is already a whole
    address or home path on its own - so the joined view merely glued the next line onto it, and the
    as-written scan has judged it. The exception: an ALLOWED home user (runner) whose continuation
    goes on as a path ("bob/Videos") is a longer name that only begins with an allowed one."""
    if ADDRESS.fullmatch(head):
        return True
    home = HOME_PATH.fullmatch(head)
    if not home:
        return False
    if not HOME_USER_ALLOWED.match(home.group(1)):
        return True
    word = tail.split(None, 1)[0] if tail.split(None, 1) else b""
    return not (b"/" in word or b"\\" in word)


def text_hits(data: bytes):
    """(offset, description) for every hit in data, as written and across console wraps."""
    found = []
    for start, _end, shown in [*address_hits(data), *home_hits(data)]:
        found.append((start, shown))
    joined, offsets, breaks = joined_view(data)
    if not breaks:
        return found
    for start, end, shown in [*address_hits(joined), *home_hits(joined)]:
        inner = [b for b in breaks if start < b < end]
        if not inner:
            continue
        first = min(inner)
        if already_judged(joined[start:first], joined[first:]):
            continue
        found.append((offsets[start], "WRAPPED across a line break: " + shown))
    return found


# Text stored as UTF-16LE: 4+ ASCII characters (printable, tab, CR, LF), each followed by a NUL.
# UTF-16BE text matches too, one byte along (its NUL comes first), so both byte orders are read.
UTF16LE_RUN = re.compile(rb"(?:[\t\n\r\x20-\x7e]\x00){4,}")


def utf16_runs(data: bytes):
    """(original offset of the run, decoded ASCII bytes) for every run of UTF-16 text in data."""
    for m in UTF16LE_RUN.finditer(data):
        yield m.start(), m.group(0)[0::2]


def file_hits(data: bytes):
    """(line, description) for every hit in one file, as written, across console wraps, and in any
    UTF-16 text it holds. Line numbers count LF bytes, so they hold for UTF-16LE text too."""
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
    print("[OK] no email-shaped string and no named home path in committed evidence, "
          "as written or across line wraps")
    return 0


if __name__ == "__main__":
    repo_root = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
    sys.exit(scan(repo_root))

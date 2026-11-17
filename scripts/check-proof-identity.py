#!/usr/bin/env python3
"""Issue #173: fail when committed evidence carries an email address.

Scans every file (text AND binary, byte for byte) under the evidence directories and fails when
one holds an email-shaped string. It matches the SHAPE of an address, never a particular value:
this file must never contain a real address, or the guard is the leak with a test around it.

What it does NOT do, stated so nobody reads more into a green run than it says:
  * It does not undo the past. Addresses already in git HISTORY stay there; the repository is
    public, so they are already out. Issue #173 deliberately did not rewrite history. This guard
    stops the NEXT leak, not the last one.
  * It does not look for home-directory paths, usernames or host names. Those are a lower-severity
    class the written rule in docs/cencon/DEVELOPMENT_METHOD.md covers; this check is for addresses.
  * It cannot read text inside an image. A screenshot showing an address passes.

Allowances - each is a shape that can never be a person's address, with the reason beside it:
  * Reserved example domains (RFC 2606 / RFC 6761): example.com / .net / .org and anything under
    the .example, .invalid, .test and .localhost top-level domains. These are the stand-ins a seat
    should USE when an address has to appear.
  * systemd template-instance units such as the per-user manager unit (a name, an "@", the numeric
    uid, then ".service"): the part after the "@" is a number plus a unit type, not a domain, and
    it appears in ordinary Linux service-status evidence.
There is deliberately no per-file or per-line allowance: an address that is genuinely needed in
evidence is written as <owner account> (or a reserved-domain stand-in), not exempted.

Output names the file and line of each hit and prints the hit MASKED, so a CI log on a public
repository does not republish the address it caught.

Exit codes: 0 clean, 1 an address-shaped string was found, 2 the instrument is broken (a scanned
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


def mask(hit: bytes) -> str:
    """Show the shape of a hit without republishing it: s***@***.com"""
    text = hit.decode("ascii", "replace")
    sep = "@" if "@" in text else "%40"
    local, _, domain = text.partition(sep)
    tld = domain.rsplit(".", 1)[-1]
    return f"{local[:1]}***{sep}***.{tld}"


def is_allowed(domain: bytes) -> bool:
    return bool(RESERVED_DOMAIN.search(domain) or SYSTEMD_INSTANCE.match(domain))


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
                for match in ADDRESS.finditer(data):
                    if is_allowed(match.group(1)):
                        continue
                    line = data.count(b"\n", 0, match.start()) + 1
                    rel = os.path.relpath(path, root).replace(os.sep, "/")
                    hits.append((rel, line, mask(match.group(0))))
        if dir_files == 0:
            print(f"[FAIL] instrument: {rel_dir} holds no files - an empty scan certifies nothing")
            return 2

    print(f"[check-proof-identity] scanned {files} files, {total_bytes} bytes under {', '.join(EVIDENCE_DIRS)}")
    if hits:
        for rel, line, shown in hits:
            print(f"[FAIL] {rel}:{line}: email-shaped string {shown}")
        print(f"[FAIL] {len(hits)} email-shaped string(s) in committed evidence. Replace each with "
              "<owner account> (or a reserved-domain stand-in such as user@example.com) - see the "
              "evidence-identity rule in docs/cencon/DEVELOPMENT_METHOD.md. Issue #173.")
        return 1
    print("[OK] no email-shaped string in committed evidence")
    return 0


if __name__ == "__main__":
    repo_root = sys.argv[1] if len(sys.argv) > 1 else os.getcwd()
    sys.exit(scan(repo_root))

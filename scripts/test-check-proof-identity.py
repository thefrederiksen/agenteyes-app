#!/usr/bin/env python3
"""Issue #173: the committed corpus that keeps scripts/check-proof-identity.py honest in BOTH
directions. A guard that is only ever shown clean files has never been shown it can stay quiet on
an ordinary file, and one that is only shown leaks has never been shown it does not block the
repository. Every file under tests/evidence-identity-corpus/ is planted, one at a time, as the only
proof file in a throwaway repository root, and the real guard is run on it as CI runs it:

  green/          identity-free files. Each MUST exit 0. They include the shapes a guard that joins
                  line breaks flags (a pytest decorator, a property setter, ls output, a mention),
                  and the allowances: RFC 2606 / RFC 6761 reserved domains, systemd unit names
                  (user@1000.service, getty@tty1.service), and the container test accounts.
  red/            single-line leak shapes, including UTF-16LE/BE text and JSON-escaped and
                  URL-encoded forms. Each MUST exit 1 and name the file.
  declared-miss/  leaks the single-line guard does not catch, BY DESIGN (see the guard's docstring).
                  The wrapped-* files are every console-wrap shape built in #173 rounds 2 and 3;
                  catching them is issue #218. Each MUST exit 0 - if one turns red, the guard
                  changed: update the docstring and move it.
  declared-flag/  identity-free files the guard flags, BY DESIGN. Each MUST exit 1, same rule.

Every name and address in the corpus is a fake (planted-probe, acme-widgets.io); the corpus holds
shapes, never an identity. Exit 0 when every file behaves as its directory says, 1 when one does
not, 2 when the corpus is missing or a directory is empty (an empty corpus certifies nothing).

Usage: python3 scripts/test-check-proof-identity.py [repo-root]
"""

import os
import shutil
import subprocess
import sys
import tempfile

CORPUS = "tests/evidence-identity-corpus"
EXPECTED_EXIT = {"green": 0, "red": 1, "declared-miss": 0, "declared-flag": 1}


def run_guard(guard: str, corpus_file: str):
    """Plant corpus_file as the only proof file in a temporary root; return (exit code, output)."""
    with tempfile.TemporaryDirectory() as root:
        proof = os.path.join(root, "docs", "cencon", "proof", "corpus")
        review = os.path.join(root, "docs", "cencon", "review")
        os.makedirs(proof)
        os.makedirs(review)
        shutil.copyfile(corpus_file, os.path.join(proof, os.path.basename(corpus_file)))
        with open(os.path.join(review, "README.txt"), "w") as fh:
            fh.write("review directory placeholder\n")
        result = subprocess.run([sys.executable, guard, root], capture_output=True, text=True)
        return result.returncode, result.stdout + result.stderr


def main(repo_root: str) -> int:
    guard = os.path.join(repo_root, "scripts", "check-proof-identity.py")
    failures = 0
    counts = {}
    for cls, want in EXPECTED_EXIT.items():
        base = os.path.join(repo_root, CORPUS, cls)
        names = sorted(os.listdir(base)) if os.path.isdir(base) else []
        if not names:
            print(f"[FAIL] instrument: {CORPUS}/{cls} is missing or empty - nothing was tested")
            return 2
        counts[cls] = len(names)
        for name in names:
            code, out = run_guard(guard, os.path.join(base, name))
            ok = code == want and (want == 0 or f"corpus/{name}:" in out)
            label = "[OK]  " if ok else "[FAIL]"
            print(f"{label} {cls}/{name}: exit {code}, expected {want}")
            if not ok:
                failures += 1
                for line in out.splitlines():
                    print(f"         {line}")
    summary = ", ".join(f"{cls} {n}" for cls, n in counts.items())
    if failures:
        print(f"[FAIL] {failures} corpus file(s) did not behave as their directory says ({summary})")
        return 1
    print(f"[OK] every corpus file behaved as its directory says ({summary})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else os.getcwd()))

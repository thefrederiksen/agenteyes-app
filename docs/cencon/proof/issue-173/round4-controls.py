#!/usr/bin/env python3
"""Issue #173 round 4 (rescope) - the controls, rerunnable by QA from the repository root.

    python3 docs/cencon/proof/issue-173/round4-controls.py 5e6e727 [owner-file second-file [username-file]]

The three value files live OUTSIDE the repository and are never printed. Without them the controls
that need a value are skipped and the output says so.

Part 1 - verify-sweep.py (which now carries no identity) must be GREEN on the tree and RED on each
mutation. Each mutation is applied to the working tree, the verifier is run, and the file is restored
with `git checkout --` (so run it on a clean tree).
Part 2 - check-proof-identity.py (single-line) must be GREEN on the tree and RED on a planted probe,
and each allowance must be load-bearing: a copy of the guard with that allowance removed is run on
the same tree or on a planted identity-free file, and must go RED.
Part 3 - pair_failure() on synthetic home paths (no identity): over-redaction of a no-person segment,
under-redaction of a named one, and a changed prefix are each named.

Exit 0 when every control behaves as stated, 1 otherwise.
"""

import importlib.util
import os
import re
import shutil
import subprocess
import sys
import tempfile

# Importing verify-sweep.py must not leave a .pyc in the evidence tree: it embeds the absolute source
# path, which holds a home directory, and the guard scans the filesystem.
sys.dont_write_bytecode = True

HERE = "docs/cencon/proof/issue-173"
VERIFY = f"{HERE}/verify-sweep.py"
GUARD = "scripts/check-proof-identity.py"
results = []


def run(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True)
    return r.returncode, r.stdout + r.stderr


def expect(name, code, want, out, show=True):
    ok = code == want
    results.append(ok)
    print(f"{'[OK]  ' if ok else '[FAIL]'} {name}: exit {code}, expected {want}")
    if show:
        for line in out.strip().splitlines():
            if line.startswith(("files=", "[FAIL]", "mapping", "WARNING", "[OK]", "[check-")):
                print(f"         {line}")


def mutate(path, fn):
    with open(path, "rb") as fh:
        data = fh.read()
    new = fn(data)
    if new == data:
        raise SystemExit(f"[FAIL] instrument: mutation of {path} changed nothing")
    with open(path, "wb") as fh:
        fh.write(new)


def restore(path):
    subprocess.run(["git", "checkout", "--", path], check=True)


def verifier_control(name, base, values, path, fn):
    mutate(path, fn)
    try:
        code, out = run([sys.executable, VERIFY, base, *values])
    finally:
        restore(path)
    expect(name, code, 1, out)


def main(base, owner=None, second=None, username_file=None):
    values = [owner, second] if owner and second else []
    print(f"## Part 1 - verify-sweep.py against base {base}"
          f" ({'WITH' if values else 'WITHOUT'} the owner/second value files)")
    code, out = run([sys.executable, VERIFY, base, *values])
    expect("unmutated tree", code, 0, out)

    digit_file = "docs/cencon/proof/issue-113/qa-runs/round2/stop/after.json"
    verifier_control("one digit moved", base, values, digit_file,
                     lambda d: re.sub(rb"[0-9]", lambda m: b"9" if m.group(0) != b"9" else b"8", d, count=1))
    review = "docs/cencon/review/archived-pr-188-issue-187-review-round3.md"
    verifier_control("token swap: one <owner second account> -> <owner account>", base, values, review,
                     lambda d: d.replace(b"<owner second account>", b"<owner account>", 1))
    verifier_control("token replaced by a non-convention stand-in", base, values, digit_file,
                     lambda d: d.replace(b"<owner account>", b"<redacted>", 1))
    verifier_control("CRLF: the first LF turned into CRLF", base, values, digit_file,
                     lambda d: d.replace(b"\n", b"\r\n", 1))
    wrapped = "docs/cencon/proof/issue-156/qa-windows/run-rc-main.txt"
    verifier_control("over-redaction: a non-identity path segment (AppData) became <user>", base, values,
                     wrapped, lambda d: d.replace(b"\\AppData\\", b"\\<user>\\", 1))
    if username_file:
        with open(username_file, "rb") as fh:
            name = fh.read().strip()
        verifier_control("under-redaction: the WRAPPED redaction undone (ers\\<user> back to the name)",
                         base, values, wrapped,
                         lambda d: re.sub(rb"(\r?\n)ers\\<user>\\", lambda m: m.group(1) + b"ers\\" + name + b"\\", d,
                                          count=1))
    else:
        print("[SKIP] under-redaction of the wrapped occurrence: no username-file given")

    print("\n## Part 2 - check-proof-identity.py (single-line)")
    code, out = run([sys.executable, GUARD, "."])
    expect("guard on the tree", code, 0, out)
    probe = "docs/cencon/proof/round4-planted-probe.txt"
    with open(probe, "w") as fh:
        fh.write("signed in as planted.probe" + "@" + "acme-widgets.io\n")
    try:
        code, out = run([sys.executable, GUARD, "."])
    finally:
        os.remove(probe)
    expect("guard with a planted single-line address", code, 1, out)

    with open(GUARD) as fh:
        guard_src = fh.read()
    corpus = "tests/evidence-identity-corpus/green"
    mutations = [
        ("tester/qauser allowance removed -> the #194 evidence from main 5e6e727 is red", ".",
         "|runneradmin|tester|qauser)$", "|runneradmin)$"),
        ("unit-type TLD rule removed -> getty@tty1.service and the other named-instance units are red",
         f"{corpus}/fp23-systemd-unit-names-any-instance.txt",
         "return bool(RESERVED_DOMAIN.search(domain) or SYSTEMD_UNIT_TLD.search(domain)",
         "return bool(RESERVED_DOMAIN.search(domain)"),
        ("both unit rules removed -> user@1000.service in main's issue-102 systemctl status is red", ".",
         "or SYSTEMD_UNIT_TLD.search(domain)\n                or SYSTEMD_INSTANCE.match(domain))", ")"),
        ("reserved-domain rule removed -> scratch@example.invalid in main's issue-196 proof is red", ".",
         "return bool(RESERVED_DOMAIN.search(domain) or", "return bool("),
    ]
    with tempfile.TemporaryDirectory() as tmp:
        for name, target, old, new in mutations:
            if old not in guard_src:
                raise SystemExit(f"[FAIL] instrument: mutation text not found in the guard: {name}")
            mutated = os.path.join(tmp, "guard.py")
            with open(mutated, "w") as fh:
                fh.write(guard_src.replace(old, new, 1))
            root = "."
            if target != ".":
                root = os.path.join(tmp, "root")
                shutil.rmtree(root, ignore_errors=True)
                os.makedirs(os.path.join(root, "docs/cencon/proof"))
                os.makedirs(os.path.join(root, "docs/cencon/review"))
                shutil.copy(target, os.path.join(root, "docs/cencon/proof"))
                with open(os.path.join(root, "docs/cencon/review/README.txt"), "w") as fh:
                    fh.write("placeholder\n")
            code, out = run([sys.executable, mutated, root])
            hits = re.search(r"\[FAIL\] (\d+) identity hit", out)
            print(f"{'[OK]  ' if code == 1 else '[FAIL]'} {name}: exit {code}, expected 1"
                  f" ({hits.group(1) if hits else 0} hit(s))")
            results.append(code == 1)
            files = sorted({ln.split(":")[0].replace("[FAIL] ", "") for ln in out.splitlines()
                            if ln.startswith("[FAIL] docs/")})
            for f in files:
                print(f"         in {f}")

    print("\n## Part 3 - pair_failure() on synthetic home paths")
    spec = importlib.util.spec_from_file_location("verify_sweep", VERIFY)
    vs = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(vs)
    # The planted name is joined at run time so this file holds no named home path for the guard.
    probe = b"planted" + b"-probe"
    cases = [
        (b"/home/runner/x", b"/home/<user>/x", "over-redaction"),
        (b"C:\\Users\\<qa>\\x", b"C:\\Users\\<user>\\x", "over-redaction"),
        (b"/Users/" + probe + b"/x", b"/Users/" + probe + b"/x", "under-redaction"),
        (b"/home/" + probe + b"/x", b"/Users/<user>/x", "did not become the same prefix"),
        (b"/home/" + probe + b"/x", b"/home/<user>/x", None),
        (b"/home/runner/x", b"/home/runner/x", None),
    ]
    for old, new, want in cases:
        why = vs.pair_failure(vs.OLD.search(old), vs.NEW.search(new), {}, {})
        ok = (why is None) if want is None else (why is not None and want in why)
        results.append(ok)
        shown = [x.decode().replace(probe.decode(), "<planted name>") for x in (old, new)]
        print(f"{'[OK]  ' if ok else '[FAIL]'} {shown[0]} -> {shown[1]}: {why or 'accepted'}")

    failed = results.count(False)
    print(f"\n{'[OK]' if not failed else '[FAIL]'} {len(results) - failed}/{len(results)} controls as stated")
    return 1 if failed else 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit("usage: round4-controls.py <base> [owner-file second-file [username-file]]")
    sys.exit(main(*sys.argv[1:]))

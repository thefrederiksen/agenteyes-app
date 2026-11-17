#!/usr/bin/env python3
"""QA's independent check for #173 (token-level diff, NOT the developer's split-at-regex).

For every changed file under docs/ (except new #173 proof + the rule doc), tokenize old and new,
run difflib over the token lists, and require EVERY non-equal opcode to be one of:
  owner address -> <owner account>, second address -> <owner second account>,
  username -> <user> where the preceding old text ends with a home-path prefix.
Also: line count, CR count identical; for .json both parse and every non-string leaf identical,
structure identical. Values are read from files and never printed.
usage: qa_verify.py <base> <head> <owner-file> <second-file> <username>
"""
import difflib, json, re, subprocess, sys

base, head, of, sf, user = sys.argv[1:6]
owner = open(of).read().strip().encode()
second = open(sf).read().strip().encode()
user = user.encode()
TOK = re.compile(rb"<owner account>|<owner second account>|<user>|"
                 rb"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+[A-Za-z]|[A-Za-z0-9]+|\s+|.", re.S)
HOMEPFX = re.compile(rb"(/home/|/Users/|Users(\\)+|Users/)$")

def git(*a):
    return subprocess.run(["git", *a], capture_output=True, check=True).stdout

def leaves(o, path="$"):
    if isinstance(o, dict):
        yield (path, "dict", tuple(sorted(o)))
        for k in sorted(o): yield from leaves(o[k], path + "." + k)
    elif isinstance(o, list):
        yield (path, "list", len(o))
        for i, v in enumerate(o): yield from leaves(v, f"{path}[{i}]")
    elif isinstance(o, str):
        yield (path, "str", None)
    else:
        yield (path, type(o).__name__, o)

names = [n for n in git("diff", "--name-only", base, head, "--", "docs").decode().split()
         if not n.startswith("docs/cencon/proof/issue-173/") and n != "docs/cencon/DEVELOPMENT_METHOD.md"]
fails = 0; pairs = {"owner": 0, "second": 0, "user": 0}; jsonok = 0; newfiles = 0
for n in names:
    try:
        old = git("show", f"{base}:{n}")
    except subprocess.CalledProcessError:
        print("[FAIL] no old side (file added?):", n); fails += 1; continue
    new = git("show", f"{head}:{n}")
    if old.count(b"\n") != new.count(b"\n") or old.count(b"\r") != new.count(b"\r"):
        print("[FAIL] line/CR count:", n); fails += 1; continue
    ol, nl = old.split(b"\n"), new.split(b"\n")
    bad = False
    for op, a1, a2, b1, b2 in difflib.SequenceMatcher(None, ol, nl, autojunk=False).get_opcodes():
        if op == "equal": continue
        if op != "replace" or (a2 - a1) != (b2 - b1):
            bad = True; print(f"[FAIL] {n}: line-level {op} {a2-a1}->{b2-b1} lines"); break
        for lo, ln in zip(ol[a1:a2], nl[b1:b2]):
            ot, nt = TOK.findall(lo), TOK.findall(ln)
            if b"".join(ot) != lo or b"".join(nt) != ln:
                bad = True; print(f"[FAIL] {n}: tokenizer lost bytes"); break
            for op2, i1, i2, j1, j2 in difflib.SequenceMatcher(None, ot, nt, autojunk=False).get_opcodes():
                if op2 == "equal": continue
                o = b"".join(ot[i1:i2]); w = b"".join(nt[j1:j2])
                if op2 == "replace" and (i2 - i1) == 1 and (j2 - j1) == 1:
                    if o.lower() == owner.lower() and w == b"<owner account>": pairs["owner"] += 1; continue
                    if o.lower() == second.lower() and w == b"<owner second account>": pairs["second"] += 1; continue
                    if o == user and w == b"<user>" and HOMEPFX.search(b"".join(ot[max(0, i1-6):i1])):
                        pairs["user"] += 1; continue
                bad = True
                print(f"[FAIL] {n}: {op2} {len(o)}B -> {len(w)}B (not an allowed redaction)")
                break
            if bad: break
        if bad: break
    if bad: fails += 1; continue
    if n.endswith(".json"):
        a, b = json.loads(old), json.loads(new)
        if list(leaves(a)) != list(leaves(b)):
            print("[FAIL] json structure/non-string leaves differ:", n); fails += 1; continue
        jsonok += 1
print(f"files={len(names)} json_equal_nonstring={jsonok} redactions={pairs} failures={fails}")
sys.exit(1 if fails else (2 if not names else 0))

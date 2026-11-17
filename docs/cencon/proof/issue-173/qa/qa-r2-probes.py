#!/usr/bin/env python3
"""QA #173 round 2: plant one probe at a time under docs/cencon/review/qa-r2-probe/deep/er/, run the
guard, remove it. Run from the repo root: python3 docs/cencon/proof/issue-173/qa/qa-r2-probes.py
Every shape is assembled at run time (AT, US, HM) so this file is not itself a hit; every name is the
fake 'planted-probe' / acme-widgets.io."""
import os, shutil, subprocess
AT, US, HM, N = chr(64), "Us" + "ers", "/ho" + "me", "planted-probe"
D = "docs/cencon/review/qa-r2-probe/deep/er"
LEAK = [  # each SHOULD be red
 ("A2-name-split-mid.txt", f"p C:\\{US}\\planted-pr\r\nobe\\AppData\n"),
 ("B-trailing-space-crlf.txt", f"Cannot remove C:\\Us   \r\ners\\{N}\\AppData\\Local\n"),
 ("B2-trailing-tab-lf.txt", f"path /ho\t\n/me/{N}/x\n"),
 ("B3-trailing-space-address.txt", f"as planted.probe{AT}acme-wid   \ngets.io ok\n"),
 ("C-three-lines.txt", f"x C:\\U\r\nse\r\nrs\\{N}\\AppData\n"),
 ("C2-four-lines-address.txt", f"pla\nnted.pro\nbe{AT}acme\n-widgets.io\n"),
 ("D-lone-cr.txt", f"x C:\\Us\rers\\{N}\\AppData\n"),
 ("F-runner-prefix.txt", f"x {HM}/runner\nbob/Videos\n"),
 ("E-utf16le-not-wrapped.txt", f"x C:\\{US}\\{N}\\AppData\r\n".encode("utf-16-le")),
]
CLEAN = [  # each SHOULD be green: no identity in them
 ("A-split-inside-token.txt", f"out C:\\{US}\\<us\r\ner>\\AppData\n"),
 ("FP1-python-decorator.py", f"import pytest\n{AT}pytest.mark.parametrize(\"x\", [1])\ndef test_x(x):\n    pass\n"),
 ("FP2-property-setter.py", f"class A:\n    def x(self):\n        return self._x\n\n    {AT}x.setter\n    def x(self, v):\n        self._x = v\n"),
 ("FP3-ls-home-then-output.txt", f"$ ls {HM}\n/var/log/syslog\n"),
 ("FP4-csharp-verbatim.cs", f"var s = path\n{AT}\"C:\\Temp\";\n"),
 ("FP5-md-mention.md", f"Reviewed by the gate\n{AT}github.com/actions note\n"),
 ("FP6-users-heading.md", "## Admin /" + US + "\n/Settings page\n"),
]
def guard():
    r = subprocess.run(["python3", "scripts/check-proof-identity.py"], capture_output=True, text=True)
    return r.returncode, [l for l in r.stdout.splitlines() if l.startswith("[FAIL] docs")]
for title, cases, want in (("LEAK probes - each SHOULD be red (exit 1)", LEAK, 1), ("NO-LEAK probes - each SHOULD be green (exit 0)", CLEAN, 0)):
    print("####", title)
    for name, body in cases:
        os.makedirs(D, exist_ok=True)
        with open(os.path.join(D, name), "wb") as fh:
            fh.write(body if isinstance(body, bytes) else body.encode())
        code, lines = guard()
        shutil.rmtree("docs/cencon/review/qa-r2-probe")
        print(f"## {name} exit={code} -> {'as expected' if code == want else 'WRONG'}")
        for l in lines: print("   ", l)
code, lines = guard()
print(f"#### clean tree after every probe removed: exit={code}", *lines)

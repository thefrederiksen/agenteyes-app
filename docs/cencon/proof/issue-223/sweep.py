#!/usr/bin/env python3 -I
# #223 criterion 4: list every time figure in a file, in line form and in joined form
# (all whitespace, including line breaks, collapsed to one space). Usage: sweep.py FILE [list]
import re, sys
# every time figure: a number or range followed by a time unit
PAT = re.compile(r'(?<![\w.#])(\d+(?:\.\d+)?(?:\s*(?:-|to)\s*\d+(?:\.\d+)?)?)(?:\s|-)?(ms|s|seconds?|minutes?|hours?)(?![\w])')
txt = open(sys.argv[1], encoding='ascii').read()
joined = re.sub(r'\s+', ' ', txt)
hits = [(m.start(), m.group(0)) for m in PAT.finditer(joined)]
line_hits = sum(len(PAT.findall(l)) for l in txt.split('\n'))
print(f"line-form hits {line_hits}  joined-form hits {len(hits)}")
if len(sys.argv) > 2:
    for i, (pos, g) in enumerate(hits, 1):
        ctx = joined[max(0, pos-70):pos+len(g)+40]
        print(f"{i:3d} | {g} | ...{ctx}...")

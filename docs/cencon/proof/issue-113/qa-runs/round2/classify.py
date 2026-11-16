#!/usr/bin/env python3
# Classify every dense poll. MISLEADING = State is not 'recording' (the recording has ended, as far as a
# client can tell) AND (LastStopFailed is not true OR LastCaptureFault is empty) - i.e. a client reading
# State / LastStopFailed / LastCaptureFault could conclude the recording finished cleanly.
#   classify.py <densepoll.jsonl>
import json, sys, collections
rows = [json.loads(l) for l in open(sys.argv[1])]
err = [r for r in rows if "error" in r]
c = collections.Counter(); first = {}; mis = []
for r in rows:
    if "error" in r: continue
    k = (r["State"], r["CaptureLiveness"], r["LastStopFailed"], r["LastCaptureFault"] is not None)
    c[k] += 1; first.setdefault(k, r["t"])
started = False
for r in rows:
    if "error" in r: continue
    if r["State"] == "recording": started = True; continue
    if started and (r["LastStopFailed"] is not True or not r["LastCaptureFault"]): mis.append(r)
print(f"polls total={len(rows)} errors={len(err)} first={rows[0]['t']} last={rows[-1]['t']}")
print("state classes (State, CaptureLiveness, LastStopFailed, fault-named): count, first seen")
for k, n in sorted(c.items(), key=lambda kv: first[kv[0]]): print(f"  {k}: {n}  first {first[k]}")
post = [r for r in rows if "error" not in r and r["State"] != "recording"]
rec = [r for r in rows if "error" not in r and r["State"] == "recording"]
after = [r for r in post if r["t"] > (rec[-1]["t"] if rec else "")]
print(f"polls after the recording ended: {len(after)}; of them finalizing={sum(r['State']=='finalizing' for r in after)} idle={sum(r['State']=='idle' for r in after)}")
print(f"MISLEADING polls after the recording started: {len(mis)}")
for r in mis[:20]: print("  ", r)

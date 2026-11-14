#!/usr/bin/env python3
# QA round 2 for #113: poll /status as fast as the service answers, one JSON line per poll with UTC time.
#   densepoll.py <outfile> <seconds>
import json, sys, time, urllib.request, datetime
out, dur = sys.argv[1], float(sys.argv[2])
end = time.time() + dur
K = ["State","CaptureLiveness","CaptureSecondsSinceGrowth","LastStopFailed","LastStopError","LastCaptureFault","LastCaptureFaultUtc"]
with open(out, "w") as f:
    while time.time() < end:
        t = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
        try:
            d = json.load(urllib.request.urlopen("http://127.0.0.1:7882/status", timeout=5))
            f.write(json.dumps({"t": t, **{k: d.get(k) for k in K}}) + "\n")
        except Exception as e:
            f.write(json.dumps({"t": t, "error": str(e)}) + "\n")
        f.flush()
        time.sleep(0.02)

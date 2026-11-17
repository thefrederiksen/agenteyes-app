#!/usr/bin/env python3 -I
# QA #142 macOS: measure the EMPTY WINDOW - how long after a screen-only take starts before the capture
# file on disk holds a first COMPLETE fragment (moof + full mdat) that decodes to at least one frame.
# Before that moment an unclean end leaves nothing playable. Default settings (no fps given = 30).
# Polls every 50 ms. Time zero = the moment the start POST returned (also prints the POST-sent time).
import json, os, struct, subprocess, sys, time, urllib.request
API = "http://127.0.0.1:7882"; secs = float(sys.argv[1]) if len(sys.argv) > 1 else 15
def post(path, body):
    r = urllib.request.Request(API + path, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"}, method="POST")
    return json.load(urllib.request.urlopen(r, timeout=60))
def frags(f):
    try: size = os.path.getsize(f)
    except FileNotFoundError: return -1, 0, 0
    off = 0; complete = 0
    with open(f, "rb") as fh:
        while off + 8 <= size:
            fh.seek(off); n, t = struct.unpack(">I4s", fh.read(8))
            if n < 8 or off + n > size: break
            if t == b"mdat": complete += 1
            off += n
    return size, complete, off
print(f"load {os.getloadavg()} cores {os.cpu_count()}")
t_sent = time.time(); st = post("/record/start", {"mode": "video", "screen": 1, "source": "none"}); t0 = time.time()
d = st["Dir"]; f = os.path.join(d, "recording.mp4")
print(f"start POST sent -> returned {t0 - t_sent:.3f} s; dir {os.path.basename(d)}")
first_byte = first_frag = None; last = None; first_decode = None
while time.time() - t0 < secs:
    size, complete, _ = frags(f); el = time.time() - t0
    if (size, complete) != last:
        print(f"  t+{el:6.3f}s  size {size:>9}  complete mdat fragments {complete}"); last = (size, complete)
    if size > 0 and first_byte is None: first_byte = el
    if complete >= 1 and first_frag is None:
        first_frag = el
        # positive control in the same run: the first fragment really decodes
        cp = "<scratch>/emptywin-first-fragment.mp4"
        _, _, end = frags(f)
        with open(f, "rb") as src, open(cp, "wb") as dst: dst.write(src.read(end))
        r = subprocess.run(["ffprobe", "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", cp], capture_output=True, text=True)
        first_decode = r.stdout.strip()
        print(f"  first complete fragment copied ({end} bytes): decodes to {first_decode} frames")
    time.sleep(0.05)
sp = post("/record/stop", {}); print("stop:", json.dumps(sp))
print(f"RESULT first byte on disk at t+{first_byte if first_byte is None else round(first_byte, 3)} s; "
      f"first complete, decodable fragment at t+{first_frag if first_frag is None else round(first_frag, 3)} s "
      f"({first_decode} frames) - screen-only, 30 fps default, {os.cpu_count()} cores")

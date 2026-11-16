"""Decode proof for one media file (issue #113 pass 5, item 6's amended criterion).

Prints: container duration, per-stream start_time/duration/codec, a COUNTED frame total, a full
decode's exit code and error-line count, and a frame read from the last second (its pts and the png
size). 'moov atom present' is not a criterion here - a truncated fragmented file has one.
Usage: python verify_file.py <file> <outdir-for-png>"""
import json, os, subprocess, sys
f, outdir = sys.argv[1], sys.argv[2]
def run(args): return subprocess.run(args, capture_output=True, text=True)
print(f"FILE {f}  bytes={os.path.getsize(f)}")
p = run(["ffprobe", "-v", "error", "-show_entries",
         "format=duration,format_name:stream=index,codec_type,codec_name,start_time,duration,nb_frames,r_frame_rate,sample_rate",
         "-of", "json", f])
print(f"ffprobe exit={p.returncode}")
j = json.loads(p.stdout or "{}")
fmt = j.get("format", {})
print(f"format={fmt.get('format_name')} duration={fmt.get('duration')}")
for s in j.get("streams", []):
    print("stream " + " ".join(f"{k}={s.get(k)}" for k in
          ("index", "codec_type", "codec_name", "start_time", "duration", "nb_frames", "r_frame_rate", "sample_rate")))
c = run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-count_frames",
         "-show_entries", "stream=nb_read_frames", "-of", "default=nw=1:nk=1", f])
print(f"counted video frames={c.stdout.strip()} (exit {c.returncode})")
d = run(["ffmpeg", "-v", "error", "-nostdin", "-i", f, "-f", "null", "-"])
errs = [l for l in d.stderr.splitlines() if l.strip()]
print(f"full decode exit={d.returncode} error_lines={len(errs)}")
for l in errs[:5]: print("  decode: " + l)
png = os.path.join(outdir, os.path.basename(os.path.dirname(f)) + "_" + os.path.basename(f) + "_last.png")
e = run(["ffmpeg", "-v", "error", "-nostdin", "-y", "-sseof", "-1", "-i", f, "-frames:v", "1", "-update", "1", png])
ok = e.returncode == 0 and os.path.exists(png) and os.path.getsize(png) > 0
print(f"frame from the last second: exit={e.returncode} png_bytes={os.path.getsize(png) if os.path.exists(png) else 0} -> {'OK' if ok else 'FAIL'}")
t = run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time",
         "-of", "csv=p=0", f])
pts = [float(x) for x in t.stdout.split() if x.strip() and x.strip() != "N/A"]
if pts: print(f"video packet pts: first={min(pts):.3f} last={max(pts):.3f} count={len(pts)}")

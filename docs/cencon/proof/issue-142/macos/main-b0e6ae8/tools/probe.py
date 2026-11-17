#!/usr/bin/env python3 -I
# QA #142 macOS rows 4-6 probe. usage: probe.py <file.mp4> <expected_seconds> [tolerance_s=1.0] [min_tail_frames=54]
# Reports, from the file alone:
#   streams; container duration; per-stream duration; a FULL decode (exit code + stderr);
#   every decoded video frame's timestamp -> frame count, fps, last frame time;
#   top-level atoms (moov present / where);
#   THE ROW 6 CHECK (sharpened, lead's comment 6930028814):
#     (a) last decoded video frame time within +/- tolerance of the EXPECTED take length, AND
#     (b) >= min_tail_frames decoded frames with timestamps in [expected-2, expected] -
#         frames AT the expected end, not at the end of whatever bytes exist.
#   Verdict printed as ROW6-CHECK PASS / FAIL with each arm. Empty output from any instrument is
#   reported as BROKEN, never as a pass.
import json, subprocess, sys, struct, os

f = sys.argv[1]; exp = float(sys.argv[2])
tol = float(sys.argv[3]) if len(sys.argv) > 3 else 1.0
mintail = int(sys.argv[4]) if len(sys.argv) > 4 else 54

def run(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True)
    return r.returncode, r.stdout, r.stderr

print(f"file: {os.path.basename(f)}  bytes: {os.path.getsize(f)}  expected take: {exp:.3f} s  tolerance: +/-{tol} s  tail window: [{exp-2:.3f}, {exp:.3f}] needs >= {mintail} frames")
rc, out, err = run(["ffprobe", "-v", "error", "-show_entries",
                    "format=duration,format_name:stream=index,codec_type,codec_name,duration,avg_frame_rate,sample_rate,channels,nb_frames",
                    "-of", "json", f])
info = json.loads(out or "{}")
print("ffprobe exit:", rc, "stderr:", repr(err.strip()[:300]))
print("format:", json.dumps(info.get("format")))
for s in info.get("streams", []): print("stream:", json.dumps(s))

# top-level atoms
atoms = []
with open(f, "rb") as fh:
    off = 0; size = os.path.getsize(f)
    while off + 8 <= size and len(atoms) < 100000:
        fh.seek(off); h = fh.read(16)
        n, t = struct.unpack(">I4s", h[:8]); t = t.decode("latin1")
        if n == 1: n = struct.unpack(">Q", h[8:16])[0]
        if n == 0: n = size - off
        atoms.append((t, off, n)); off += n
kinds = {}
for t, o, n in atoms: kinds[t] = kinds.get(t, 0) + 1
moov = [(o, n) for t, o, n in atoms if t == "moov"]
print("top-level atoms:", json.dumps(kinds), " moov at:", moov, " last atom:", atoms[-1] if atoms else None,
      " atom walk ends at", off, "of", os.path.getsize(f), "(overrun = truncated last atom)" if off > os.path.getsize(f) else "")

# full decode of every stream
rc, out, err = run(["ffmpeg", "-v", "error", "-nostdin", "-i", f, "-map", "0", "-f", "null", "-"])
print(f"full decode: exit {rc}  stderr: {repr(err.strip()[:400])}")

# every decoded video frame's timestamp
rc, out, err = run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
                    "frame=best_effort_timestamp_time", "-of", "csv=p=0", f])
ts = [float(x.strip(",")) for x in out.split() if x.strip(",") and x.strip(",") != "N/A"]
if not ts:
    print("BROKEN INSTRUMENT: no video frame timestamps decoded"); sys.exit(2)
last = ts[-1]; n = len(ts)
print(f"decoded video frames: {n}  first ts {ts[0]:.3f}  last ts {last:.3f}  frames/(last-first+1/30): {n/(last-ts[0]+1/30):.4f} fps")
tail = [t for t in ts if exp - 2 <= t <= exp]
print(f"frames with ts in [{exp-2:.3f}, {exp:.3f}]: {len(tail)}" + (f"  (first {tail[0]:.3f}, last {tail[-1]:.3f})" if tail else ""))
# audio, if any
rc, out, err = run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-show_entries",
                    "packet=pts_time,duration_time", "-of", "csv=p=0", f])
pk = [l.strip(",").split(",") for l in out.split() if l.strip(",")]
if pk:
    a_end = float(pk[-1][0]) + float(pk[-1][1]); a_start = float(pk[0][0])
    v_end = last + 1/30
    print(f"audio packets: {len(pk)}  first pts {a_start:.3f}  audio end {a_end:.3f}  video end {v_end:.3f}  |video end - audio end| = {abs(v_end-a_end):.3f} s")
else:
    print("audio packets: 0 (no audio stream)")

arm_a = abs(last - exp) <= tol
arm_b = len(tail) >= mintail
print(f"ROW6-CHECK arm (a) last frame {last:.3f} vs expected {exp:.3f}: diff {last-exp:+.3f} s -> {'PASS' if arm_a else 'FAIL'}")
print(f"ROW6-CHECK arm (b) {len(tail)} frames in the last 2 s of the expected take (need >= {mintail}) -> {'PASS' if arm_b else 'FAIL'}")
print("ROW6-CHECK", "PASS" if arm_a and arm_b else "FAIL")

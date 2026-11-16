"""Verify a recording file: stream start_times, duration, COUNTED frames, full decode, a frame from the last second."""
import sys, subprocess, json, os
for f in sys.argv[1:]:
    print(f"=== {os.path.basename(os.path.dirname(f))}/{os.path.basename(f)}  size={os.path.getsize(f)}")
    if os.path.getsize(f) == 0: print("    EMPTY FILE (0 bytes)"); continue
    p = subprocess.run(["ffprobe","-v","error","-count_frames","-show_entries",
        "stream=index,codec_type,start_time,duration,nb_read_frames:format=duration,format_name","-of","json",f],capture_output=True,text=True)
    if p.returncode: print("    ffprobe FAILED:", p.stderr.strip()[:400]); continue
    j=json.loads(p.stdout)
    if not j.get("streams"): print("    NO STREAMS")
    for s in j.get("streams", []): print(f"    {s['codec_type']:5} start_time={s.get('start_time')} duration={s.get('duration')} counted_frames={s.get('nb_read_frames')}")
    print(f"    format={j['format'].get('format_name')} duration={j['format'].get('duration')}")
    d = subprocess.run(["ffmpeg","-v","error","-nostdin","-i",f,"-f","null","-"],capture_output=True,text=True)
    errs=[l for l in d.stderr.splitlines() if l.strip()]
    print(f"    full decode: exit={d.returncode} error_lines={len(errs)}" + (f" first={errs[0][:200]!r}" if errs else ""))
    png=f+".lastsec.png"
    l = subprocess.run(["ffmpeg","-v","error","-nostdin","-y","-sseof","-1","-i",f,"-frames:v","1","-update","1",png],capture_output=True,text=True)
    print(f"    last-second frame: exit={l.returncode} png_bytes={os.path.getsize(png) if os.path.exists(png) else 0}")
    t = subprocess.run(["ffprobe","-v","error","-select_streams","v:0","-show_entries","packet=pts_time","-of","csv=p=0",f],capture_output=True,text=True)
    pts=[float(x) for x in t.stdout.split() if x.strip() and x.strip()!="N/A"]
    if pts: print(f"    video packet pts: first={min(pts):.3f} last={max(pts):.3f} count={len(pts)}")

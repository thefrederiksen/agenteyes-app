"""Apply / restore the QA stall injection on PreviewTap.WriteFrameToDisk.

  python stall.py good     -> the round-5 design + an 8-second stall in every frame write
  python stall.py bad      -> the same stall PLUS the round-4 shape (the drain publishes inline)
  python stall.py restore  -> put the file back byte-exactly and verify the sha256
"""
import hashlib, sys

P = r"D:\ReposFred\agenteyes-qa33-r5\src\AgentEyes.Core\Preview\PreviewTap.cs"
PRISTINE = "ce50c018916e6cdf603ca2408a4d4c3a1166d68975f2abdfd55eadc631816954"

STALL_ANCHOR = """        private void WriteFrameToDisk(byte[] frame)
        {
            File.WriteAllBytes(_tempPath, frame);"""
STALL_NEW = """        private void WriteFrameToDisk(byte[] frame)
        {
            // QA ROUND 5 INJECTED STALL: a filesystem that takes 8 seconds to answer. 80x the
            // 100ms preview frame interval, and the thing no unit test can produce for real.
            System.Threading.Thread.Sleep(8000);
            File.WriteAllBytes(_tempPath, frame);"""

INLINE_ANCHOR = "                            if (_publishing) Offer(frame);"
INLINE_NEW = "                            if (_publishing) Publish(frame);"


def sha(p):
    with open(p, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def read():
    with open(P, "r", encoding="utf-8-sig", newline="") as f:
        return f.read()


def write(s):
    with open(P, "w", encoding="utf-8", newline="") as f:
        f.write(s)


mode = sys.argv[1]
if mode == "restore":
    import subprocess
    subprocess.run(["git", "-C", r"D:\ReposFred\agenteyes-qa33-r5", "checkout", "--",
                    r"src/AgentEyes.Core/Preview/PreviewTap.cs"], check=True)
    got = sha(P)
    print("restored sha256 =", got, "MATCHES PRISTINE" if got == PRISTINE else "*** MISMATCH ***")
    sys.exit(0 if got == PRISTINE else 1)

s = read()
if sha(P) != PRISTINE:
    print("*** the file is not pristine - restore first ***")
    sys.exit(1)
assert s.count(STALL_ANCHOR) == 1, "stall anchor not unique"
s = s.replace(STALL_ANCHOR, STALL_NEW)
if mode == "bad":
    assert s.count(INLINE_ANCHOR) == 1, "inline anchor not unique"
    s = s.replace(INLINE_ANCHOR, INLINE_NEW)
write(s)
print("applied:", mode, "-> sha256", sha(P))

"""QA round 5 - build variants of AgentEyes.App for the runtime checks on gate defect 3.

  python apppatch.py qm9      -> the CONSTRUCTOR remembers the preview choice again
  python apppatch.py stall    -> every config.json write takes 8 seconds (shipped code otherwise)
  python apppatch.py stallbad -> the same stall PLUS the round-4 shape (the HUD saves synchronously
                                 on the WPF dispatcher)
  python apppatch.py restore  -> git checkout the two files and verify their sha256
"""
import hashlib, subprocess, sys

ROOT = r"D:\ReposFred\agenteyes-qa33-r5"
HUD = ROOT + r"\src\AgentEyes.App\HudWindow.cs"
CFG = ROOT + r"\src\AgentEyes.App\Config.cs"
PRISTINE = {
    HUD: "82ae8d762d5e7715ff1c6bf67c9e8a9fd4b57ca7d5f8b8de5c1e4b4b9a0f5c3e",  # replaced at runtime
    CFG: "",
}

CTOR_OLD = "            ApplyPreviewState();\n\n            _timer.Tick += (_, _) => OnTick();"
CTOR_NEW = "            ApplyAndRememberPreviewChoice();\n\n            _timer.Tick += (_, _) => OnTick();"

SAVE_OLD = ("            _cfg.HudPreviewCorner = PreviewNames.Text(_preview.Corner);\n"
            "            _cfg.SaveWithoutBlockingTheUiThread();")
SAVE_NEW = ("            _cfg.HudPreviewCorner = PreviewNames.Text(_preview.Corner);\n"
            "            _cfg.Save();")

WRITE_OLD = """            lock (WriteGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);"""
WRITE_NEW = """            lock (WriteGate)
            {
                // QA ROUND 5 INJECTED STALL: a filesystem that takes 8 seconds to answer.
                System.Threading.Thread.Sleep(8000);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);"""


def sha(p):
    with open(p, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def read(p):
    with open(p, "r", encoding="utf-8-sig", newline="") as f:
        return f.read()


def write(p, s):
    with open(p, "w", encoding="utf-8", newline="") as f:
        f.write(s)


def patch(p, old, new):
    s = read(p)
    assert s.count(old) == 1, "anchor not unique in " + p
    write(p, s.replace(old, new))


mode = sys.argv[1]
if mode == "restore":
    subprocess.run(["git", "-C", ROOT, "checkout", "--",
                    "src/AgentEyes.App/HudWindow.cs", "src/AgentEyes.App/Config.cs"], check=True)
    print("restored HudWindow.cs sha256 =", sha(HUD))
    print("restored Config.cs    sha256 =", sha(CFG))
    sys.exit(0)

if mode == "qm9":
    patch(HUD, CTOR_OLD, CTOR_NEW)
elif mode == "stall":
    patch(CFG, WRITE_OLD, WRITE_NEW)
elif mode == "stallbad":
    patch(CFG, WRITE_OLD, WRITE_NEW)
    patch(HUD, SAVE_OLD, SAVE_NEW)
else:
    raise SystemExit("unknown mode " + mode)
print("applied", mode)
print("  HudWindow.cs sha256 =", sha(HUD))
print("  Config.cs    sha256 =", sha(CFG))

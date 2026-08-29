"""
QA round 4, issue #33 - QA-AUTHORED mutation sweep.

Written by QA, not reused from the developer. Each mutation breaks ONE decision the
round-4 allowlist design rests on, rebuilds, and runs the three HUD sizing test classes.

Three arms, stated up front (DEVELOPMENT_METHOD.md 6c):
  * mutation applied AND tests go RED  -> FIRED   (the check is real)
  * mutation applied AND tests stay GREEN -> SILENT (a check that cannot fail - a DEFECT)
  * mutation text not found in the source -> DID NOT APPLY (a broken instrument, NOT a pass)
"""
import subprocess, sys, os, re

ROOT = r"D:\ReposFred\agenteyes-qa33-r4"
APP  = os.path.join(ROOT, "src", "AgentEyes.App")
HUR  = os.path.join(APP, "HudUserResize.cs")
HSM  = os.path.join(APP, "HudSizeMemory.cs")
HPS  = os.path.join(APP, "HudPreviewSizing.cs")
HW   = os.path.join(APP, "HudWindow.cs")

FILTER = ("FullyQualifiedName~HudUserResizeTests|"
          "FullyQualifiedName~HudPreviewSizingOrderTests|"
          "FullyQualifiedName~HudSizeMemoryTests")

MUTATIONS = [
 ("QM1  a MOVE is treated as a resize (drop the WM_SIZING requirement)",
  HUR, "                    if (!_draggingASizingEdge) return;\n", ""),

 ("QM2  read the panel state at the END of the gesture, not the start",
  HUR, "Record(_thePanelWasUpWhenTheGestureBegan, \"the sizing border\");",
       "Record(ThePanelIsUp, \"the sizing border\");"),

 ("QM3  round 3's mechanism restored: observe SizeChanged and record it",
  HUR, "            Log.Info(\"hud: watching for user resizes (sizing border, grip, UI Automation)\");",
       "            Log.Info(\"hud: watching for user resizes (sizing border, grip, UI Automation)\");\n"
       "            _window.SizeChanged += (_, _) => Record(ThePanelIsUp, null);"),

 ("QM4  drop the automation peer override (UIA falls back to the HWND provider)",
  HW,  "        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>\n"
       "            _userResize.CreatePeer();",
       "        // peer override removed by mutation QM4"),

 ("QM5  a SECOND writer to the memory, from SavePosition",
  HW,  "            if (_size.HasSize)\n            {",
       "            _size.RecordUserResize(ActualWidth, ActualHeight);\n            if (_size.HasSize)\n            {"),

 ("QM6  the grip no longer records",
  HUR, "            _window.Height = Math.Max(_window.MinHeight, _window.ActualHeight + verticalChange);\n"
       "            Record(panelWasUp, null);",
       "            _window.Height = Math.Max(_window.MinHeight, _window.ActualHeight + verticalChange);\n"
       "            _ = panelWasUp;"),

 ("QM7  HudWindow constructs a SECOND HudSizeMemory",
  HW,  "            _userResize = new HudUserResize(this, _size);",
       "            _userResize = new HudUserResize(this, new HudSizeMemory(cfg.HudWidth, cfg.HudHeight));"),

 ("QM8  drop the panel-was-up narrowing in Record",
  HUR, "            if (!thePanelWasUpWhenTheGestureBegan) return;\n", ""),

 ("QM9  drop the non-positive-size refusal in RecordUserResize",
  HSM, "            if (!(width > 0) || !(height > 0)) return;\n", ""),

 ("QM10 opening the panel records the size it opened at (round 2's defect)",
  HPS, "            window.Height = height;",
       "            window.Height = height;\n            memory.RecordUserResize(window.Width, window.Height);"),

 ("QM11 ShowPanel sets the size BEFORE switching to manual sizing (order swap)",
  HPS, "            window.SizeToContent = SizeToContent.Manual;\n"
       "            window.Width = width;\n            window.Height = height;",
       "            window.Width = width;\n            window.Height = height;\n"
       "            window.SizeToContent = SizeToContent.Manual;"),

 ("QM12 PreferredSize ignores the remembered size",
  HSM, "            return (_width ?? defaultWidth, _height ?? defaultHeight);",
       "            return (defaultWidth, defaultHeight);"),

 ("QM13 the grip is no longer wired to the resize gesture",
  HW,  "            grip.DragDelta += (_, e) => _userResize.ByGrip(e.HorizontalChange, e.VerticalChange);",
       "            // QM13: the grip is no longer wired"),
]

def run(cmd):
    return subprocess.run(cmd, cwd=ROOT, shell=True, capture_output=True, text=True)

def test():
    r = run(f'dotnet build AgentEyes.sln -c Release -v q --nologo')
    if "Build succeeded" not in r.stdout:
        return "BUILD FAILED", r.stdout[-1500:]
    r = run(f'dotnet test AgentEyes.sln -c Release --no-build --filter "{FILTER}"')
    out = r.stdout + r.stderr
    m = re.search(r"Failed:\s+(\d+), Passed:\s+(\d+)", out)
    if not m:
        return "NO RESULT", out[-1500:]
    return ("RED" if int(m.group(1)) > 0 else "GREEN"), m.group(0)

results = []
for name, path, old, new in MUTATIONS:
    src = open(path, encoding="utf-8", newline="").read()
    if old not in src:
        results.append((name, "MUTATION DID NOT APPLY - INVESTIGATE", "target text not found"))
        print(f"[!!] {name}: DID NOT APPLY"); sys.stdout.flush()
        continue
    open(path, "w", encoding="utf-8", newline="").write(src.replace(old, new, 1))
    try:
        verdict, detail = test()
    finally:
        open(path, "w", encoding="utf-8", newline="").write(src)
    label = {"RED": "FIRED", "GREEN": "SILENT - THE CHECK CANNOT FAIL"}.get(verdict, verdict)
    results.append((name, label, detail))
    print(f"[{label}] {name}  ({detail.splitlines()[0] if detail else ''})"); sys.stdout.flush()

print("\n==== restoring and re-verifying the pristine tree ====")
v, d = test()
print(f"pristine tree: {v}  {d}")

print("\n==== SUMMARY ====")
fired = sum(1 for _, l, _ in results if l == "FIRED")
print(f"{fired} of {len(results)} mutations FIRED")
for n, l, d in results:
    print(f"  {l:38s} {n}")
    if l != "FIRED":
        print(f"      detail: {d}")

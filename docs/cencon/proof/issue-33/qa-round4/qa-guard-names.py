"""Which NAMED guard turns red for each IL-guard-targeting mutation (QA round 4, issue #33)."""
import subprocess, os, re
ROOT = r"D:\ReposFred\agenteyes-qa33-r4"
APP  = os.path.join(ROOT, "src", "AgentEyes.App")
HUR, HW = os.path.join(APP,"HudUserResize.cs"), os.path.join(APP,"HudWindow.cs")
FILTER = "FullyQualifiedName~HudUserResizeTests"
M = [
 ("QM3 SizeChanged subscription in the sizing code", HUR,
  '            Log.Info("hud: watching for user resizes (sizing border, grip, UI Automation)");',
  '            Log.Info("hud: watching for user resizes (sizing border, grip, UI Automation)");\n            _window.SizeChanged += (_, _) => Record(ThePanelIsUp, null);'),
 ("QM4 peer override removed", HW,
  "        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>\n            _userResize.CreatePeer();",
  "        // removed"),
 ("QM5 second writer from SavePosition", HW,
  "            if (_size.HasSize)\n            {",
  "            _size.RecordUserResize(ActualWidth, ActualHeight);\n            if (_size.HasSize)\n            {"),
 ("QM6 grip stops recording", HUR,
  "            _window.Height = Math.Max(_window.MinHeight, _window.ActualHeight + verticalChange);\n            Record(panelWasUp, null);",
  "            _window.Height = Math.Max(_window.MinHeight, _window.ActualHeight + verticalChange);\n            _ = panelWasUp;"),
 ("QM13 the grip.DragDelta wiring removed", HW,
  "            grip.DragDelta += (_, e) => _userResize.ByGrip(e.HorizontalChange, e.VerticalChange);",
  "            // removed"),
 ("QM7 second HudSizeMemory", HW,
  "            _userResize = new HudUserResize(this, _size);",
  "            _userResize = new HudUserResize(this, new HudSizeMemory(cfg.HudWidth, cfg.HudHeight));"),
]
def run(c): return subprocess.run(c, cwd=ROOT, shell=True, capture_output=True, text=True)
for name, path, old, new in M:
    src = open(path, encoding="utf-8", newline="").read()
    assert old in src, name
    open(path,"w",encoding="utf-8",newline="").write(src.replace(old,new,1))
    try:
        b = run("dotnet build AgentEyes.sln -c Release -v q --nologo")
        assert "Build succeeded" in b.stdout, b.stdout[-800:]
        r = run(f'dotnet test AgentEyes.sln -c Release --no-build --filter "{FILTER}"')
        out = r.stdout + r.stderr
        failed = sorted(set(re.findall(r"Failed\s+AgentEyes\.Tests\.\w+\.(\w+)", out)))
        print(f"{name}\n   red guards: {failed}\n   {re.search(r'Failed:.*', out).group(0)}\n", flush=True)
    finally:
        open(path,"w",encoding="utf-8",newline="").write(src)
b = run("dotnet build AgentEyes.sln -c Release --no-incremental -v q --nologo")
assert "Build succeeded" in b.stdout, "THE FINAL RESTORE BUILD FAILED - the binary on disk is STALE: " + b.stdout[-1500:]
print("tree restored and rebuilt --no-incremental; build succeeded")

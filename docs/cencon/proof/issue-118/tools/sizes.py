import json, os, sys, collections
app = sys.argv[1]; m = os.path.join(app, "Contents", "MacOS")
deps = json.load(open(os.path.join(m, "AgentEyesDesktop.deps.json")))
owner = {}
for tgt in deps["targets"].values():
    for lib, d in tgt.items():
        for kind in ("runtime", "native", "resources"):
            for p in (d.get(kind) or {}):
                owner[os.path.basename(p)] = lib.split("/")[0]
def group(rel, lib):
    if rel.startswith("libvlc/"): return "libvlc (in-app player: VideoLAN.LibVLC.Mac)"
    if rel.startswith("runtimes/"): return "Whisper natives for 8 platforms (runtimes/, copied by Whisper.net.Runtime; only macos-arm64 is usable here)"
    if lib is None:
        return "not listed in deps.json: " + rel
    l = lib.lower()
    if l.startswith("runtimepack.microsoft.netcore.app"): return ".NET 8 runtime (runtime pack 8.0.33)"
    if l.startswith("whisper.net"): return "Whisper natives (Whisper.net.Runtime, all platforms)"
    if l.startswith(("avalonia", "skiasharp", "harfbuzzsharp", "microcom", "tmds", "libvlcsharp")): return "UI toolkit: Avalonia, Skia, HarfBuzz, LibVLCSharp"
    if lib in ("AgentEyesDesktop", "agenteyes"): return "AgentEyes own assemblies (AgentEyesDesktop.dll, agenteyes.dll)"
    return "other NuGet dependencies"
tot = collections.Counter(); cnt = collections.Counter(); unk = []
for root, _, files in os.walk(m):
    for f in files:
        p = os.path.join(root, f); rel = os.path.relpath(p, m); sz = os.path.getsize(p)
        lib = owner.get(os.path.basename(rel)) if "/" not in rel else None
        if lib is None and rel in ("AgentEyesDesktop", "AgentEyesDesktop.deps.json", "AgentEyesDesktop.runtimeconfig.json", "AgentEyesDesktop.pdb", "agenteyes.pdb"):
            g = "AgentEyes own assemblies (AgentEyesDesktop.dll, agenteyes.dll)" if rel.endswith(".pdb") else "apphost + its config"
        else:
            g = group(rel, lib)
        if g.startswith("not listed"): unk.append((rel, sz)); g = "not listed in deps.json"
        tot[g] += sz; cnt[g] += 1
res = sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(os.path.join(app, "Contents")) for f in fs if "/MacOS" not in r)
grand = sum(tot.values()) + res
print(f"Bundle: {grand/1e6:.1f} MB in files ({sum(cnt.values())} under Contents/MacOS); du: see below")
for g, s in tot.most_common(): print(f"  {s/1e6:7.1f} MB  {100*s/grand:5.1f}%  {cnt[g]:4d} files  {g}")
print(f"  {res/1e6:7.1f} MB  {100*res/grand:5.1f}%        Info.plist, PkgInfo, icon, signature")
for rel, sz in sorted(unk, key=lambda x: -x[1])[:15]: print(f"    unlisted: {sz/1e6:6.2f} MB {rel}")

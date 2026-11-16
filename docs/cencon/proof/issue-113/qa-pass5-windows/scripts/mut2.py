import subprocess, pathlib, re, os
repo = pathlib.Path(r"C:\ReposFred\agenteyes-app"); f = repo/"src/AgentEyes.Core/Video/FfmpegArgs.cs"
def run(cmd, env=None):
    p = subprocess.run(cmd, cwd=repo, capture_output=True, text=True, shell=True, env=env); return p.returncode, p.stdout+p.stderr
env = dict(os.environ, AGENTEYES_FFMPEG_TESTS="1")
def t(tag):
    rc,o = run("dotnet build tests/AgentEyes.Tests/AgentEyes.Tests.csproj -c Release -nologo -v q"); assert rc==0, o[-1500:]
    rc,o = run('dotnet test tests/AgentEyes.Tests/AgentEyes.Tests.csproj -c Release --no-build -nologo --filter "FullyQualifiedName~FragmentedOutputTimeline"', env)
    print(f"[{tag}]", [l.strip() for l in o.splitlines() if re.search(r"(Passed|Failed)!", l)])
    for m in re.findall(r"(?:Assert\.\w+\(\) Failure.*|Expected:.*|Actual:.*|start_time.*)", o)[:6]: print("    ", m.strip()[:200])
raw = f.read_bytes()
old = b"+frag_keyframe+empty_moov+default_base_moof+delay_moov"; assert raw.count(old)==1
try:
    t("BASELINE with ffmpeg tests on")
    f.write_bytes(raw.replace(old, b"+frag_keyframe+empty_moov+default_base_moof")); t("QA-M4 delay_moov removed")
finally:
    f.write_bytes(raw); os.utime(f, None)
print("restored identical bytes:", f.read_bytes()==raw)
t("BASELINE after restore")

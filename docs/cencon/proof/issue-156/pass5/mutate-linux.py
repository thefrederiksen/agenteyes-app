import subprocess, sys, re
P='src/AgentEyes.Core/Video/FfmpegChildRegistry.cs'
orig=open(P).read()
M=[
 ("L1 other uid -> Unknown", 'return ProcessAnswer.NotMine(\n                        $"pid {pid} belongs to ANOTHER USER (/proc/{pid} owned', 'return ProcessAnswer.Unknown(\n                        $"pid {pid} belongs to ANOTHER USER (/proc/{pid} owned'),
 ("L2 our uid -> NotMine (the D-L1 defect back)", 'if (isOurUid(owner))\n                        return ProcessAnswer.Unknown(', 'if (isOurUid(owner))\n                        return ProcessAnswer.NotMine('),
 ("L3 /proc gone -> Unknown", 'return ProcessAnswer.NotMine($"pid {pid} exited while being read (/proc/{pid} does not exist)");', 'return ProcessAnswer.Unknown($"pid {pid} exited while being read (/proc/{pid} does not exist)");'),
 ("L4 owner unreadable -> NotMine", 'return ProcessAnswer.Unknown(\n                            $"pid {pid}: kill(pid, 0) was refused (EPERM) and whose process it is could not be read ({dir.Detail})");', 'return ProcessAnswer.NotMine(\n                            $"pid {pid}: kill(pid, 0) was refused (EPERM) and whose process it is could not be read ({dir.Detail})");'),
 ("L5 no probe / no own uids -> NotMine", 'return ProcessAnswer.Unknown(\n                            $"pid {pid}: kill(pid, 0) was refused (EPERM) and there is no read', 'return ProcessAnswer.NotMine(\n                            $"pid {pid}: kill(pid, 0) was refused (EPERM) and there is no read'),
 ("L6 the whole pass-4 row back (EPERM -> NotMine)", 'if (errno == Eperm && !isMac)\n                {', 'if (errno == Eperm && !isMac) return ProcessAnswer.NotMine($"pid {pid} belongs to ANOTHER USER (kill(pid, 0) errno {errno})");\n                if (false)\n                {'),
 ("L7 statx uid offset wrong (16, stx_nlink)", 'StxUidOffset = 20;', 'StxUidOffset = 16;'),
 ("L8 ENOENT read as Unreadable", 'return errno == Enoent', 'return errno == -12345'),
 ("L9 STATX_UID mask check inverted", 'if ((mask & StatxUid) == 0)', 'if ((mask & StatxUid) != 0)'),
 ("L10 Linux wrapper passes no owner probe", 'OperatingSystem.IsLinux() ? ReadLinuxProcOwner : null);', 'null);'),
]
for name,a,b in M:
    assert orig.count(a)==1, name
    open(P,'w').write(orig.replace(a,b))
    r=subprocess.run(['dotnet','test','tests/AgentEyes.Tests.Linux','-c','Release','--filter','FullyQualifiedName~ProcessIdentityTests'],capture_output=True,text=True)
    out=r.stdout
    fails=re.findall(r'^\s+Failed (\S+) ',out,re.M)
    summ=[l for l in out.splitlines() if 'Failed:' in l and 'Total' in l]
    msgs=re.findall(r'Assert\.\w+\(\) Failure[^\n]*\n[^\n]*\n?[^\n]*|error CS\d+[^\n]*',out)
    print(f"=== {name}: {'CAUGHT' if fails or r.returncode else 'SURVIVED'} rc={r.returncode}")
    for f in sorted(set(fails)): print("   failed:", f)
    for l in summ[:1]: print("  ", l.strip())
    # first assertion message
    m=re.search(r'Error Message:\n(.*?)\n(?:\s*Stack Trace)',out,re.S)
    if m: print("   message:", ' | '.join(x.strip() for x in m.group(1).strip().splitlines())[:400])
open(P,'w').write(orig)
print("restored:", open(P).read()==orig)

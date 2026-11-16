#!/usr/bin/env python3
"""Issue #156 pass 4 mutation runs: apply ONE mutation, build the macOS suite, run the identity/
ownership/reap/owned tests, record which fail, restore the file. Run from the repo root."""
import subprocess, sys, re, os
R = "src/AgentEyes.Core/Video/FfmpegChildRegistry.cs"
C = "src/AgentEyes.Core/CaptureLock.cs"
M = [
 ("M1 Alive -> NotMine (the pass 3 behaviour)", R, "case MacProcessStateKind.Alive:\n                    return ProcessAnswer.Unknown(", "case MacProcessStateKind.Alive:\n                    return ProcessAnswer.NotMine("),
 ("M2 Zombie -> Unknown", R, "case MacProcessStateKind.Zombie:\n                    return ProcessAnswer.NotMine(", "case MacProcessStateKind.Zombie:\n                    return ProcessAnswer.Unknown("),
 ("M3 Gone -> Unknown", R, "case MacProcessStateKind.Gone:\n                    return ProcessAnswer.NotMine(", "case MacProcessStateKind.Gone:\n                    return ProcessAnswer.Unknown("),
 ("M4 state unreadable -> NotMine", R, "                default:\n                    return ProcessAnswer.Unknown(", "                default:\n                    return ProcessAnswer.NotMine("),
 ("M5 EPERM -> Unknown", R, "if (errno == Eperm)\n                    return ProcessAnswer.NotMine(", "if (errno == Eperm)\n                    return ProcessAnswer.Unknown("),
 ("M6 ESRCH -> Unknown", R, "if (errno == Esrch) return ProcessAnswer.NotMine(", "if (errno == Esrch) return ProcessAnswer.Unknown("),
 ("M7 other errno -> NotMine", R, "                return ProcessAnswer.Unknown(\n                    $\"pid {pid}: its start time is refused", "                return ProcessAnswer.NotMine(\n                    $\"pid {pid}: its start time is refused"),
 ("M8 ours on Linux -> NotMine", R, "if (!isMac)\n                return ProcessAnswer.Unknown(", "if (!isMac)\n                return ProcessAnswer.NotMine("),
 ("M9 Windows refusal -> Unknown", R, "if (isWindows)\n                return ProcessAnswer.NotMine(", "if (isWindows)\n                return ProcessAnswer.Unknown("),
 ("M10 SZOMB constant wrong (4 = SSTOP)", R, "SZomb = 5;", "SZomb = 4;"),
 ("M11 equal start time -> Unknown", R, "? ProcessAnswer.Mine($\"pid {pid} is running", "? ProcessAnswer.Unknown($\"pid {pid} is running"),
 ("M12 FreeLockLiveness ignores Unknown", C, "if (answer.IsUnknown && unknown == null) unknown = (w, answer.Why);", "if (false && answer.IsUnknown && unknown == null) unknown = (w, answer.Why);"),
 ("M13 Release treats Unknown as exited", C, "if (answer.IsNotMine) continue;\n                running.Add(w);", "if (!answer.IsMine) continue;\n                running.Add(w);"),
 ("M14 reaper: Unknown owner treated as gone", R, "if (owner.IsUnknown)\n", "if (false && owner.IsUnknown)\n"),
 ("M15 reaper: Unknown child treated as exited", R, "if (child.IsNotMine)\n", "if (!child.IsMine)\n"),
 ("M16 reaper: Unknown at the deadline is killed", R, "if (last.IsUnknown)\n", "if (false && last.IsUnknown)\n"),
 ("M17 Windows: any error -> NotMine", R, "if (isWindows && nativeError != ErrorAccessDenied)", "if (false && isWindows && nativeError != ErrorAccessDenied)"),
 ("M18 MacProcessStateKind: Zombie is value 0", "src/AgentEyes.Core/Video/ProcessIdentity.cs", "Unreadable = 0,", "Unreadable = 9,"),
]
only = sys.argv[1:]
env = dict(os.environ)
for name, path, a, b in M:
    if only and name.split()[0] not in only: continue
    src = open(path).read()
    assert src.count(a) == 1, f"{name}: anchor not found exactly once"
    try:
        open(path, "w").write(src.replace(a, b))
        bld = subprocess.run(["dotnet", "build", "tests/AgentEyes.Tests.Mac", "-c", "Release"], capture_output=True, text=True, env=env)
        if bld.returncode != 0:
            print(f"{name}: BUILD FAILED"); continue
        t = subprocess.run(["dotnet", "test", "tests/AgentEyes.Tests.Mac", "-c", "Release", "--no-build",
                            "--filter", "FullyQualifiedName~ProcessIdentityTests|FullyQualifiedName~CaptureWriterReapTests|FullyQualifiedName~CaptureOwnershipTests|FullyQualifiedName~OwnedFfmpegTests"],
                           capture_output=True, text=True, env=env)
        failed = sorted(set(re.findall(r"^\s+Failed (\S+)", t.stdout, re.M)))
        summary = re.findall(r"(Failed!|Passed!)\s+-\s+Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+)", t.stdout)
        print(f"{name}: {summary[-1] if summary else 'NO SUMMARY'}")
        for f in failed: print(f"    FAILS {f.split('.')[-1]}")
    finally:
        open(path, "w").write(src)

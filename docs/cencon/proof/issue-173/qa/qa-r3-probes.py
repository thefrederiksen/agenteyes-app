#!/usr/bin/env python3
"""QA #173 round 3: NEW false-positive and miss probes aimed at the console-wrap discriminator.
Every probe is planted alone as the only proof file in a throwaway root and the real guard is run on
it. Shapes are assembled at run time (AT, US) so this file is not itself a hit; every name is the
fake 'planted-probe' / acme-widgets.io. Usage, from the repo root:
  python3 qa-r3-fp-probes.py [path/to/check-proof-identity.py]"""
import base64, os, random, shutil, subprocess, sys, tempfile

GUARD = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else "scripts/check-proof-identity.py")
AT, US, N = chr(64), "Us" + "ers", "planted-probe"


def fold(text, width):
    """fold(1) without -s: a hard cut every `width` columns, as a console does."""
    out = []
    for line in text.split("\n"):
        out.extend([line[i:i + width] for i in range(0, len(line), width)] or [""])
    return "\n".join(out)


rnd = random.Random(173)
prose = ("The reviewer pinged the release team in the channel and asked them to look at the scratch "
         f"directory under /{US}/<user>/scratch before the merge. Every account on the build host keeps "
         "its own directory. Thanks to everyone on the team, and especially the gate, for catching the "
         f"wrapped path. Contact the desk at desk{AT}example.com if anything under C:\\{US}\\<user>\\AppData "
         "looks off. ") * 6
minjson = ('{"version":"1.11.11","paths":{"videos":"C:\\\\' + US + '\\\\<user>\\\\Videos\\\\AgentEyes",'
           '"data":"\\/' + US + '\\/<user>\\/Library\\/AgentEyes","linux":"\\/home\\/runner\\/work"},'
           '"deps":["npm:' + AT + 'types/node","' + AT + 'scope/pkg"],"contact":"ops' + AT + 'example.com",'
           '"notes":"run via \\\\\\\\server\\\\share\\\\logs and \\/var\\/log\\/agenteyes for every seat"}') * 4

CLEAN = [  # identity-free: each SHOULD be green (exit 0)
 ("G1-long-line-ends-slash-then-slash.txt",
  "The CI job uploads its artifacts to the build server under https://ci.example.org/builds/\n"
  "/var/log/agenteyes/service.log is where the recorder service writes its own log\n"),
 ("G1b-long-line-ends-slash-then-home-runner.txt",
  "Each seat writes its scratch output under the per-seat directory on the shared host /\n"
  "/home/runner/work/agenteyes-app/agenteyes-app is the CI checkout path on Linux\n"),
 ("G1c-long-url-ends-slash-then-npm-scope.txt",
  "The npm registry page for the scoped type package lives at https://www.npmjs.com/package/\n"
  + AT + "types/node is the package we pin for the proof generator scripts in this repository\n"),
 ("G2-prose-hard-cut-at-80-midword.txt", fold(prose, 80) + "\n"),
 ("G2b-prose-hard-cut-at-120-midword.txt", fold(prose, 120) + "\n"),
 ("G3-minified-json-hard-cut-80.json", fold(minjson, 80) + "\n"),
 ("G3b-base64-random-wrapped-76.txt", base64.encodebytes(bytes(rnd.randrange(256) for _ in range(49152))).decode()),
 ("G3c-base64-slashy-wrapped-76.txt", base64.encodebytes(b"/var/log/agenteyes/" * 600 + bytes(range(256)) * 40).decode()),
 ("G4-long-url-hard-cut-80.txt", fold("https://github.com/thefrederiksen/agenteyes-app/actions/runs/44064860037/job/"
  "133975802520#step:7:120?check_suite_focus=true&ref=refs/heads/issue-173-scrub-identity&tab=users&x=" + "a" * 40, 80) + "\n"),
 ("G4b-long-url-split-before-placeholder.txt",
  f"https://example.org/api/v1.0/directory/organisation/departments/engineering/teams/{US}\n/<user>/profile?expand=manager\n"),
 ("G5-table-rows-end-in-backslash.txt",
  f"| C:\\{US}\\<user>\\AppData\\Local\\AgentEyes | the per-user data root on Windows seats      \\\n"
  "| C:\\ProgramData\\AgentEyes                 | the machine-wide data root, shared by all    \\\n"),
 ("G5b-ascii-art-backslash-then-prose.txt",
  "      /\\                                                                         /\\\n"
  "     /  \\        AgentEyes records what is on the screen, and nothing else       /  \\\n"
  "    /____\\_______________________________________________________________________/____\\\n"
  f"{US} see the indicator at all times. There is no stealth mode in the product at all.\n"),
 ("G6-shell-backslash-continuation.txt",
  "docker run --rm -v /var/lib/agenteyes:/data -e AGENTEYES_HOME=/srv/agenteyes ghcr.io/acme/agenteyes:1.11 \\\n"
  "--user agenteyes --entrypoint /usr/bin/agenteyes selftest\n"),
 ("G7-csv-long-header-then-at-row.csv",
  "timestamp,seat,machine,event,detail,duration_ms,exit_code,notes_from_the_reviewer_seat,owner\n"
  + AT + "ci.nightly,linux,runner,start,,0,0,scheduled,ci\n"),
]
LEAK = [  # each SHOULD be red (exit 1); the width is the line BEFORE the break, content only
 ("L-pwsh80-colzero-wrap-after-prefix-79.txt",
  "Remove-Item : Cannot remove item " + "x" * (79 - 33 - len(f"C:\\{US}\\")) + f"C:\\{US}\\\n{N}\\AppData\\Local\\Temp\n"),
 ("L-pwsh80-colzero-wrap-inside-prefix-79.txt",
  "Remove-Item : Cannot remove item " + "x" * (79 - 33 - 3) + f"C:\\\n{US}\\{N}\\AppData\\Local\\Temp\n"),
 ("L-pwsh80-colzero-wrap-before-at-79.txt",
  "Send-MailMessage : Mailbox unavailable for " + "x" * (79 - 43 - 13) + f"planted.probe\n{AT}acme-widgets.io\n"),
 ("L-pwsh120-colzero-wrap-after-prefix-119.txt",
  "Remove-Item : Cannot remove item " + "x" * (119 - 33 - len(f"C:\\{US}\\")) + f"C:\\{US}\\\n{N}\\AppData\\Local\\Temp\n"),
 ("L-formatlist-indented-split-at-separator.txt",
  f"Message : Cannot remove item because it is being used by another process at C:\\{US}\\\n"
  f"          {N}\\AppData\\Local\\AgentEyes\\recordings\\x.mp4\n"),
]


def run(name, body):
    with tempfile.TemporaryDirectory() as root:
        proof = os.path.join(root, "docs", "cencon", "proof", "p")
        os.makedirs(proof)
        os.makedirs(os.path.join(root, "docs", "cencon", "review"))
        with open(os.path.join(root, "docs", "cencon", "review", "README.txt"), "w") as fh:
            fh.write("placeholder\n")
        with open(os.path.join(proof, name), "wb") as fh:
            fh.write(body.encode())
        r = subprocess.run([sys.executable, GUARD, root], capture_output=True, text=True)
        return r.returncode, [l for l in r.stdout.splitlines() if l.startswith("[FAIL] docs")]


def lines_alone_clean(body):
    """Sanity check on the probe itself: no single line of a CLEAN probe may be a hit on its own,
    or the probe tests the as-written rule rather than the join."""
    for i, line in enumerate(body.split("\n")):
        code, _ = run("line.txt", line + "\n")
        if code:
            return f"line {i + 1} is a hit on its own"
    return "ok"


for title, cases, want in (("NEW identity-free probes - each SHOULD be green", CLEAN, 0),
                           ("Leak probes at real PowerShell widths - each SHOULD be red", LEAK, 1)):
    print("####", title)
    for name, body in cases:
        code, hits = run(name, body)
        extra = f" probe-self-check={lines_alone_clean(body)}" if want == 0 else ""
        print(f"## {name} exit={code} -> {'as expected' if code == want else 'WRONG'}{extra} {hits[:1]}")

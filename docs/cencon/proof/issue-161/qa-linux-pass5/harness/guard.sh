#!/bin/bash
# QA #161 pass 5, job 5: the whole guard, re-run at 22ec8cb on devlinux. Every run: strace -f + lldeny on BOTH real roots.
# Every REDIRECTED child gets AGENTEYES_SERVICE_PROBE_PORT=<closed port> so nothing of this harness asks the owner's 7882.
set -u
AE="dotnet $1"; DESK="dotnet $2"; LLD=$3; RS=$4; RV=$5; M=$6; EV=$7; CLOSED=$8
case "$M" in */qa5-guard*) ;; *) echo "refusing: M=$M is not a qa5-guard scratch dir"; exit 2;; esac
H=$(getent passwd "$(id -un)" | cut -d: -f6)
mkdir -p "$M" "$EV"
run() { local label=$1; shift; local envs=(); while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  ( cd "${CWD:-$M}" && env -u AGENTEYES_REDIRECT_DATA -u AGENTEYES_REDIRECT_VIDEOS -u AGENTEYES_SERVICE_PROBE_PORT "${envs[@]}" \
      strace -f -qq -s 200 -e trace=%file,%desc,%network -o "$EV/$label.strace" "$LLD" "$RS" "$RV" -- "$@" > "$EV/$label.out" 2> "$EV/$label.err" ); local rc=$?
  local T="$EV/$label.strace"; local hits=$(grep -vF execve "$T" | grep -E "$RS|$RV")
  echo; echo "[$label] exit=$rc real-root syscalls=$(printf '%s' "$hits" | grep -c .) opens/creates=$(printf '%s' "$hits" | grep -cE 'open|creat|mkdir|unlink|rename') EACCES=$(printf '%s' "$hits" | grep -c EACCES) probe-syscalls=$(grep -c 'redirect-probe' "$T") connects-7882=$(grep 'connect(' "$T" | grep -c 'htons(7882)') binds-7882=$(grep 'bind(' "$T" | grep -c 'htons(7882)') http=[$(grep -oE '"(GET|POST|PUT|DELETE) /[^ ]* HTTP' "$T" | sort | uniq -c | tr -s ' ' | tr '\n' ';')]"
  grep -v '^lldeny' "$EV/$label.err" | grep -v '^\s*$' | sed 's/^/  stderr: /' | cut -c1-600 | head -${LINES_ERR:-6}; }
probe_order() { grep -nE 'redirect-probe|scratch-library|getdents|openat\(.*O_DIRECTORY' "$EV/$1.strace" | grep -E 'redirect-probe|scratch-library' | sed -E 's/^([0-9]+):[0-9]+ +/  line \1: /; s/-[0-9]+-[0-9a-f]{32}/-<pid>-<guid>/' | cut -c1-200; }
R() { echo "AGENTEYES_SERVICE_PROBE_PORT=$CLOSED"; }

echo "#### 0. POSITIVE CONTROL for the real-root counter: an UNREDIRECTED --help, real HOME (must touch the real state root, refused by Landlock)"
run P0 -- $AE --help

echo; echo "#### 1. D1 attack (serve) and x1-x4"
mkdir -p $M/atk/home $M/atk/videos; LINES_ERR=4 run ATK $(R) HOME=$M/atk/home AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/atk/videos -- timeout -s INT 25 $AE serve --port 17163
mkdir -p $M/x1/videos;          run x1 $(R) AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/x1/videos -- $AE --help
mkdir -p $M/x2/home $M/x2/videos; run x2 $(R) HOME=$M/x2/home AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/x2/videos -- $AE --help
mkdir -p $M/x3/xdg $M/x3/videos;  run x3 $(R) XDG_DATA_HOME=$M/x3/xdg AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/x3/videos -- $AE --help
mkdir -p $M/x4/home $M/x4/data;   run x4 $(R) HOME=$M/x4/home AGENTEYES_REDIRECT_DATA=$M/x4/data AGENTEYES_REDIRECT_VIDEOS=$H/Videos -- $AE --help
mkdir -p $M/y1/home $M/y1/xdg $M/y1/cfg $M/y1/videos; run y1 $(R) HOME=$M/y1/home XDG_DATA_HOME=$M/y1/xdg XDG_CONFIG_HOME=$M/y1/cfg AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/y1/videos -- $AE --help
mkdir -p $M/y5/home $M/y5/videos; ln -sfn $H/.local/share $M/y5/dlink; run y5 $(R) HOME=$M/y5/home AGENTEYES_REDIRECT_DATA=$M/y5/dlink AGENTEYES_REDIRECT_VIDEOS=$M/y5/videos -- $AE --help
mkdir -p $M/y8/home $M/y8/videos; run y8 $(R) HOME=$M/y8/home AGENTEYES_REDIRECT_DATA=/proc/self/root$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/y8/videos -- $AE --help

echo; echo "#### 2. N1 refusals, and the write probe runs LAST (after overlap, link and marker rules)"
mkdir -p $M/n1/videos; run n1 $(R) AGENTEYES_REDIRECT_DATA=$M/n1/data AGENTEYES_REDIRECT_VIDEOS=$M/n1/videos -- $AE --help; echo "  tree after: $(cd $M/n1 && find . | sort | tr '\n' ' ')"
mkdir -p $M/n2/data; run n2 $(R) AGENTEYES_REDIRECT_DATA=$M/n2/data AGENTEYES_REDIRECT_VIDEOS=$M/n2/videos -- $AE --help; echo "  tree after: $(cd $M/n2 && find . | sort | tr '\n' ' ')"
mkdir -p $M/n3/data $M/n3/videos; chmod 555 $M/n3/videos; run n3 $(R) AGENTEYES_REDIRECT_DATA=$M/n3/data AGENTEYES_REDIRECT_VIDEOS=$M/n3/videos -- $AE --help; chmod 755 $M/n3/videos; probe_order n3; echo "  tree after: $(cd $M/n3 && find . | sort | tr '\n' ' ')"
mkdir -p $M/n6/videos; echo x > $M/n6/data; run n6 $(R) AGENTEYES_REDIRECT_DATA=$M/n6/data AGENTEYES_REDIRECT_VIDEOS=$M/n6/videos -- $AE --help
mkdir -p $M/n7/data; chmod 555 $M/n7/data; run n7-overlap-before-probe $(R) AGENTEYES_REDIRECT_DATA=$M/n7/data AGENTEYES_REDIRECT_VIDEOS=$H/Videos -- $AE --help; chmod 755 $M/n7/data
mkdir -p $M/n8/data/AgentEyes $M/n8/videos; ln -s $RS/logs $M/n8/data/AgentEyes/logs; chmod 555 $M/n8/data; run n8-link-before-probe $(R) AGENTEYES_REDIRECT_DATA=$M/n8/data AGENTEYES_REDIRECT_VIDEOS=$M/n8/videos -- $AE --help; chmod 755 $M/n8/data
mkdir -p $M/n9/data/AgentEyes $M/n9/videos; echo hand > $M/n9/data/AgentEyes/hand-made.txt; chmod 555 $M/n9/data; run n9-marker-before-probe $(R) AGENTEYES_REDIRECT_DATA=$M/n9/data AGENTEYES_REDIRECT_VIDEOS=$M/n9/videos -- $AE --help; chmod 755 $M/n9/data

echo; echo "#### 3. The scratch-marker rule (rule 2)"
mkdir -p $M/m1/data/AgentEyes $M/m1/videos; echo hand > $M/m1/data/AgentEyes/hand-made.txt; LINES_ERR=8 run m1-unmarked-nonempty $(R) AGENTEYES_REDIRECT_DATA=$M/m1/data AGENTEYES_REDIRECT_VIDEOS=$M/m1/videos -- $AE --help
mkdir -p $M/m2/data $M/m2/videos; LINES_ERR=30 run m2-fresh-empty $(R) AGENTEYES_REDIRECT_DATA=$M/m2/data AGENTEYES_REDIRECT_VIDEOS=$M/m2/videos -- $AE --help; echo "  markers after m2: $(cd $M/m2 && find . -name .agenteyes-scratch-library | sort | tr '\n' ' ')"; echo "  tree after m2: $(cd $M/m2 && find . | sort | tr '\n' ' ')"
run m3-same-roots-again-nonempty-marked $(R) AGENTEYES_REDIRECT_DATA=$M/m2/data AGENTEYES_REDIRECT_VIDEOS=$M/m2/videos -- $AE --help

echo; echo "#### 4. F2 - links inside a scratch root are JUDGED, not followed"
mkdir -p $M/L1/data/AgentEyes $M/L1/videos; ln -s $RS/logs $M/L1/data/AgentEyes/logs; run L1-abs-link-to-real-state $(R) AGENTEYES_REDIRECT_DATA=$M/L1/data AGENTEYES_REDIRECT_VIDEOS=$M/L1/videos -- $AE --help; echo "  syscalls THROUGH the link (path contains L1/data/AgentEyes/logs/): $(grep -c 'L1/data/AgentEyes/logs/' $EV/L1-abs-link-to-real-state.strace)"
mkdir -p $M/L2/data $M/L2/videos/AgentEyes; ln -s $RV $M/L2/videos/AgentEyes/linked-real; LINES_ERR=4 run L2-link-to-real-videos-serve $(R) AGENTEYES_REDIRECT_DATA=$M/L2/data AGENTEYES_REDIRECT_VIDEOS=$M/L2/videos -- timeout -s INT 25 $AE serve --port 17271; echo "  syscalls THROUGH the link: $(grep -c 'AgentEyes/linked-real/' $EV/L2-link-to-real-videos-serve.strace)"
mkdir -p $M/L3/data/AgentEyes $M/L3/videos; ln -s ../../../.. $M/L3/data/AgentEyes/up; run L3-relative-dotdot-out $(R) AGENTEYES_REDIRECT_DATA=$M/L3/data AGENTEYES_REDIRECT_VIDEOS=$M/L3/videos -- $AE --help
mkdir -p $M/L4/data/AgentEyes/inner $M/L4/videos; touch $M/L4/data/AgentEyes/.agenteyes-scratch-library; ln -s inner $M/L4/data/AgentEyes/inside; run L4-CONTROL-link-stays-inside $(R) AGENTEYES_REDIRECT_DATA=$M/L4/data AGENTEYES_REDIRECT_VIDEOS=$M/L4/videos -- $AE --help

echo; echo "#### 5. Rule 1 (the service half) on the host, via the injected port: a stand-in names a RELOCATED library"
mkdir -p $M/s1/reloc $M/s1/videos
python3 -I - "$M/s1/reloc" 17611 > $EV/s1-standin.out 2>&1 <<'PY' &
import http.server, json, sys
B = json.dumps({"DataRoot": sys.argv[1] + "/AgentEyes", "RecordingsRoot": sys.argv[1] + "-videos/AgentEyes", "LibraryRedirected": False}).encode()
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        print("REQ", self.command, self.path, flush=True); self.send_response(200); self.send_header("Content-Length", str(len(B))); self.end_headers(); self.wfile.write(B)
    def log_message(self, *a): pass
s = http.server.HTTPServer(("127.0.0.1", int(sys.argv[2])), H); s.timeout = 20; s.handle_request(); s.handle_request()
PY
SP=$!; sleep 1
LINES_ERR=4 run s1-service-names-relocated AGENTEYES_SERVICE_PROBE_PORT=17611 AGENTEYES_REDIRECT_DATA=$M/s1/reloc AGENTEYES_REDIRECT_VIDEOS=$M/s1/videos -- $AE --help
mkdir -p $M/s2/data $M/s2/videos; LINES_ERR=30 run s2-CONTROL-same-service-unrelated-redirect AGENTEYES_SERVICE_PROBE_PORT=17611 AGENTEYES_REDIRECT_DATA=$M/s2/data AGENTEYES_REDIRECT_VIDEOS=$M/s2/videos -- $AE --help
wait $SP; echo "  stand-in saw: $(grep REQ $EV/s1-standin.out | tr '\n' ';')"

echo; echo "#### 6. The port guards"
LINES_ERR=4 run P1-redirected-serve-default-port $(R) AGENTEYES_REDIRECT_DATA=$M/m2/data AGENTEYES_REDIRECT_VIDEOS=$M/m2/videos -- timeout -s INT 25 $AE serve
mkdir -p $M/svchome/Videos $M/svchome/.local/share/AgentEyes; echo '{"Port":17481}' > $M/svchome/.local/share/AgentEyes/config.json
( cd $M && env -u AGENTEYES_REDIRECT_DATA -u AGENTEYES_REDIRECT_VIDEOS -u AGENTEYES_SERVICE_PROBE_PORT HOME=$M/svchome "$LLD" "$RS" "$RV" -- timeout -s INT 60 $AE serve --port 17481 > $EV/svc.out 2>&1 ) &
for i in $(seq 1 60); do curl -s -m 1 http://127.0.0.1:17481/status > $EV/svc-status.json 2>/dev/null && [ -s $EV/svc-status.json ] && break; sleep 0.5; done
echo "  my scratch service (unredirected, HOME=<scratch>) on 17481 /status: $(python3 -I -c "import json; d=json.load(open('$EV/svc-status.json')); print({k:d.get(k) for k in ['DataRoot','RecordingsRoot','LibraryRedirected','State']})" 2>&1)"
mkdir -p $M/rc/data/AgentEyes $M/rc/videos; echo '{"Port":17481}' > $M/rc/data/AgentEyes/config.json; touch $M/rc/data/AgentEyes/.agenteyes-scratch-library
run P2-redirected-status-vs-unredirected-service $(R) AGENTEYES_REDIRECT_DATA=$M/rc/data AGENTEYES_REDIRECT_VIDEOS=$M/rc/videos -- $AE record status
run P3-redirected-START-vs-unredirected-service $(R) AGENTEYES_REDIRECT_DATA=$M/rc/data AGENTEYES_REDIRECT_VIDEOS=$M/rc/videos -- $AE record start
run P4-CONTROL-unredirected-cli-drives-it HOME=$M/svchome -- $AE record status
echo "  my scratch service still idle after P3: $(curl -s -m 2 http://127.0.0.1:17481/status | python3 -I -c 'import json,sys; d=json.load(sys.stdin); print(d.get("State"), d.get("IsRecording"))')"
wait
echo; echo "#### TOTAL connects to 127.0.0.1:7882 across every trace of this harness: $(cat $EV/*.strace | grep 'connect(' | grep -c 'htons(7882)') ; binds to 7882: $(cat $EV/*.strace | grep 'bind(' | grep -c 'htons(7882)')"
echo "#### positive control for that counter in the SAME traces: connects to the closed probe port $CLOSED: $(cat $EV/*.strace | grep 'connect(' | grep -c "htons($CLOSED)") ; to the injected stand-in 17611: $(cat $EV/*.strace | grep 'connect(' | grep -c 'htons(17611)')"

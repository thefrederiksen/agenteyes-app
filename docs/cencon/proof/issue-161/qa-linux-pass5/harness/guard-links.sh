#!/bin/bash
# QA #161 pass 5, job 5: the whole guard, re-run at 22ec8cb on devlinux. Every run: strace -f + lldeny on BOTH real roots.
# Every REDIRECTED child gets AGENTEYES_SERVICE_PROBE_PORT=<closed port> so nothing of this harness asks the owner's 7882.
set -u
AE="dotnet $1"; DESK="dotnet $2"; LLD=$3; RS=$4; RV=$5; M=$6; EV=$7; CLOSED=$8
case "$M" in */qa5-links*) ;; *) echo "refusing: M=$M is not a qa5-guard scratch dir"; exit 2;; esac
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
mark() { mkdir -p "$1/AgentEyes"; touch "$1/AgentEyes/.agenteyes-scratch-library"; }
echo "#### 4b. F2 on MARKED roots (so the marker rule passes and only the link rule can refuse)"
mkdir -p $M/K1/data $M/K1/videos; mark $M/K1/data; ln -s $RS/logs $M/K1/data/AgentEyes/logs; LINES_ERR=3 run K1-marked-abs-link-to-real-state-log $(R) AGENTEYES_REDIRECT_DATA=$M/K1/data AGENTEYES_REDIRECT_VIDEOS=$M/K1/videos -- $AE --help; echo "  syscalls THROUGH the link (K1/data/AgentEyes/logs/...): $(grep -c 'K1/data/AgentEyes/logs/' $EV/K1-marked-abs-link-to-real-state-log.strace) ; readlink/lstat of the link itself: $(grep -E 'K1/data/AgentEyes/logs"' $EV/K1-marked-abs-link-to-real-state-log.strace | grep -oE '^[0-9]+ +[a-z0-9_]+' | awk '{print $2}' | sort | uniq -c | tr -s ' ' | tr '\n' ';')"
mkdir -p $M/K2/data $M/K2/videos; mark $M/K2/videos; ln -s $RV $M/K2/videos/AgentEyes/linked-real; LINES_ERR=3 run K2-marked-link-to-real-videos-SERVE $(R) AGENTEYES_REDIRECT_DATA=$M/K2/data AGENTEYES_REDIRECT_VIDEOS=$M/K2/videos -- timeout -s INT 25 $AE serve --port 17272; echo "  syscalls THROUGH the link: $(grep -c 'AgentEyes/linked-real/' $EV/K2-marked-link-to-real-videos-SERVE.strace)"
mkdir -p $M/K3/data $M/K3/videos; mark $M/K3/data; ln -s ../../../.. $M/K3/data/AgentEyes/up; LINES_ERR=3 run K3-marked-relative-dotdot-out $(R) AGENTEYES_REDIRECT_DATA=$M/K3/data AGENTEYES_REDIRECT_VIDEOS=$M/K3/videos -- $AE --help
mkdir -p $M/K4/data $M/K4/videos; mark $M/K4/data; mkdir -p $M/K4/data/AgentEyes/deep/er; ln -s ../../../../../.. $M/K4/data/AgentEyes/deep/er/esc; LINES_ERR=3 run K4-marked-nested-link-out $(R) AGENTEYES_REDIRECT_DATA=$M/K4/data AGENTEYES_REDIRECT_VIDEOS=$M/K4/videos -- $AE --help
mkdir -p $M/K5/data $M/K5/videos; mark $M/K5/data; mkdir -p $M/K5/data/AgentEyes/inner; ln -s inner $M/K5/data/AgentEyes/inside; LINES_ERR=1 run K5-CONTROL-marked-link-stays-inside $(R) AGENTEYES_REDIRECT_DATA=$M/K5/data AGENTEYES_REDIRECT_VIDEOS=$M/K5/videos -- $AE --help
echo; echo "#### 2b. the write probe runs LAST: a MARKED root with an escaping link AND 0555 -> refused by the LINK rule, with NO probe syscall"
mkdir -p $M/N8/data $M/N8/videos; mark $M/N8/data; ln -s $RS/logs $M/N8/data/AgentEyes/logs; chmod 555 $M/N8/data; LINES_ERR=2 run N8-link-before-probe $(R) AGENTEYES_REDIRECT_DATA=$M/N8/data AGENTEYES_REDIRECT_VIDEOS=$M/N8/videos -- $AE --help; chmod 755 $M/N8/data
mkdir -p $M/N9/data $M/N9/videos; mark $M/N9/data; chmod 555 $M/N9/data; LINES_ERR=2 run N9-CONTROL-same-0555-no-link $(R) AGENTEYES_REDIRECT_DATA=$M/N9/data AGENTEYES_REDIRECT_VIDEOS=$M/N9/videos -- $AE --help; chmod 755 $M/N9/data
echo; echo "#### the banner's comparison list, in full (m2-style accepted start, K5):"; grep -v '^lldeny' $EV/K5-CONTROL-marked-link-stays-inside.err | tr ';' '\n' | sed 's/^/  | /'
echo; echo "#### connects to 7882 in these traces: $(cat $EV/*.strace | grep 'connect(' | grep -c 'htons(7882)') ; to the closed probe port $CLOSED: $(cat $EV/*.strace | grep 'connect(' | grep -c "htons($CLOSED)")"

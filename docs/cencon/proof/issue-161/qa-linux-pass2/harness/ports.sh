#!/bin/bash
# QA #161 pass 2 job 5: the port/library guards, re-run. Strace captures every HTTP request line sent.
AE=$1 LLD=$2 R1=$3 R2=$4 M=$5 EV=$6
rm -rf "$M" "$EV"; mkdir -p "$M" "$EV"
cli() { local label=$1; shift; local envs=(); while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  ( cd "$M" && env -u AGENTEYES_REDIRECT_DATA -u AGENTEYES_REDIRECT_VIDEOS "${envs[@]}" strace -f -qq -s 160 -e trace=%file,%desc,%network,sendmsg,sendto,write -o "$EV/$label.strace" "$LLD" "$R1" "$R2" -- "$AE" "$@" > "$EV/$label.out" 2> "$EV/$label.err" ); local rc=$?
  echo; echo "[$label] exit=$rc real-root opens=$(grep -vF execve "$EV/$label.strace" | grep -E "$R1|$R2" | grep -cE 'open|creat|mkdir') EACCES-on-real=$(grep -E "$R1|$R2" "$EV/$label.strace" | grep -c EACCES)"
  echo "  HTTP requests sent: $(grep -oE '"(GET|POST|PUT|DELETE) /[^ ]* HTTP' "$EV/$label.strace" | sort | uniq -c | tr -s ' ' | tr '\n' ';')"
  echo "  connects: $(grep -oE 'connect\([0-9]+, \{sa_family=AF_INET, sin_port=htons\([0-9]+\)' "$EV/$label.strace" | grep -oE 'htons\([0-9]+\)' | sort | uniq -c | tr -s ' ' | tr '\n' ';') binds: $(grep -oE 'bind\([0-9]+, \{sa_family=AF_INET6?, sin6?_port=htons\([0-9]+\)' "$EV/$label.strace" | grep -oE 'htons\([0-9]+\)' | sort -u | tr '\n' ' ')"
  cat "$EV/$label.out" "$EV/$label.err" | grep -v lldeny | sed 's/^/  | /' | cut -c1-330 | head -8; }
# Stand-in for the owner's service: UNREDIRECTED, post-#161 (reports roots), scratch roots via a spoofed HOME.
mkdir -p $M/svchome/Videos $M/svchome/.local/share/AgentEyes; echo '{"Port":17481}' > $M/svchome/.local/share/AgentEyes/config.json
( cd $M && env -u AGENTEYES_REDIRECT_DATA -u AGENTEYES_REDIRECT_VIDEOS HOME=$M/svchome strace -f -qq -o $EV/standin.strace -e trace=%file "$LLD" "$R1" "$R2" -- timeout -s INT 60 "$AE" serve --port 17481 > $EV/standin.out 2>&1 ) &
for i in $(seq 1 40); do curl -s -m 1 http://127.0.0.1:17481/status > $EV/standin-status.json 2>/dev/null && [ -s $EV/standin-status.json ] && break; sleep 0.5; done
echo "stand-in /status: $(python3 -c "import json,sys; d=json.load(open('$EV/standin-status.json')); print({k:d.get(k) for k in ['DataRoot','RecordingsRoot','LibraryRedirected','LibraryRedirect','State']})")"
mkdir -p $M/rc/data/AgentEyes $M/rc/videos; echo '{"Port":17481}' > $M/rc/data/AgentEyes/config.json
cli G1-redirected-status-vs-unredirected AGENTEYES_REDIRECT_DATA=$M/rc/data AGENTEYES_REDIRECT_VIDEOS=$M/rc/videos -- record status
cli G2-redirected-START-vs-unredirected AGENTEYES_REDIRECT_DATA=$M/rc/data AGENTEYES_REDIRECT_VIDEOS=$M/rc/videos -- record start
cli G3-unredirected-status-drives-it HOME=$M/svchome -- record status
echo "stand-in still idle after G2: $(curl -s -m 2 http://127.0.0.1:17481/status | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("State"), d.get("IsRecording", d.get("Recording")))')"
wait   # the stand-in ends by its own timeout -s INT 60. (A pkill -f pattern here also matched the INVOKING shell, whose command line held the same text, and killed the summary - do not use one.)
# The OWNER's real service on 7882 (pre-#161 binary). Read-only: status only.
mkdir -p $M/ro/data $M/ro/videos
cli O1-redirected-noport-vs-OWNER-7882 AGENTEYES_REDIRECT_DATA=$M/ro/data AGENTEYES_REDIRECT_VIDEOS=$M/ro/videos -- record status
cli O2-unredirected-vs-OWNER-7882 -- record status
cli O3-redirected-serve-on-7882 AGENTEYES_REDIRECT_DATA=$M/ro/data AGENTEYES_REDIRECT_VIDEOS=$M/ro/videos -- serve

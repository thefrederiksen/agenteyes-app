#!/bin/bash
# QA #161 Linux: the refusal matrix + guard-defeat probes, every run under strace -f and a Landlock
# deny on BOTH real roots (lldeny, control in 00-landlock-control.txt). Usage: matrix.sh <AE> <LLD> <R1> <R2> <M> <EV>
AE=$1 LLD=$2 R1=$3 R2=$4 M=$5 EV=$6; H=$(getent passwd "$(id -un)" | cut -d: -f6)
mkdir -p "$M" "$EV"
run() { local label=$1; shift; local envs=(); while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  env -u AGENTEYES_REDIRECT_DATA -u AGENTEYES_REDIRECT_VIDEOS "${envs[@]}" strace -f -qq -e trace=%file,%desc -o "$EV/$label.strace" "$LLD" "$R1" "$R2" -- "$AE" "$@" > "$EV/$label.out" 2> "$EV/$label.err"
  local rc=$?
  echo; echo "[$label] exit=$rc realroot=$(grep -vF execve "$EV/$label.strace" | grep -cE "$R1|$R2") eacces-on-real=$(grep -vF execve "$EV/$label.strace" | grep -E "$R1|$R2" | grep -c EACCES)"
  grep -v 'lldeny' "$EV/$label.err" | sed 's/^/  stderr: /' | cut -c1-600
  echo "  created under $M/$label: $(find "$M/$label" -mindepth 1 -newer "$EV/$label.strace" 2>/dev/null | wc -l) new, after-run listing: $(cd "$M/$label" 2>/dev/null && find . -mindepth 1 | sort | tr '\n' ' ')"; }
mk(){ mkdir -p "$M/$1/data" "$M/$1/videos"; }
mk a;  run a  AGENTEYES_REDIRECT_DATA=$M/a/data -- --help
mk b;  run b  AGENTEYES_REDIRECT_VIDEOS=$M/b/videos -- --help
mk n;  run n  AGENTEYES_REDIRECT_DATA= AGENTEYES_REDIRECT_VIDEOS=$M/n/videos -- --help
mk c;  run c  AGENTEYES_REDIRECT_DATA=c/data AGENTEYES_REDIRECT_VIDEOS=$M/c/videos -- --help
mk c2; run c2 AGENTEYES_REDIRECT_DATA=$M/c2/data AGENTEYES_REDIRECT_VIDEOS=./c2/videos -- --help
mk j;  run j  AGENTEYES_REDIRECT_DATA=$M/j/data AGENTEYES_REDIRECT_VIDOES=$M/j/videos -- --help
mk k;  run k  AGENTEYES_REDIRECT_DATA=$M/k/data AGENTEYES_REDIRECT_VIDEOS=$M/k/data -- --help
mk f;  run f  AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/f/videos -- --help
mk g;  run g  AGENTEYES_REDIRECT_DATA=$M/g/data AGENTEYES_REDIRECT_VIDEOS=$H/Videos -- --help
mk g2; run g2 AGENTEYES_REDIRECT_DATA=$M/g2/data AGENTEYES_REDIRECT_VIDEOS=$H/Videos/AgentEyes/x -- --help
mk h;  ln -s "$H/Videos" "$M/h/vlink"; run h AGENTEYES_REDIRECT_DATA=$M/h/data AGENTEYES_REDIRECT_VIDEOS=$M/h/vlink -- --help
mk i;  ln -s "$R1" "$M/i/videos/AgentEyes"; run i AGENTEYES_REDIRECT_DATA=$M/i/data AGENTEYES_REDIRECT_VIDEOS=$M/i/videos -- --help
mk i2; ln -s "$R2" "$M/i2/data/AgentEyes"; run i2 AGENTEYES_REDIRECT_DATA=$M/i2/data AGENTEYES_REDIRECT_VIDEOS=$M/i2/videos -- --help
mkdir -p "$M/d"; run d AGENTEYES_REDIRECT_DATA=$M/d/data AGENTEYES_REDIRECT_VIDEOS=$M/d/videos -- --help
mk e;  chmod 555 "$M/e/data" "$M/e/videos"; run e AGENTEYES_REDIRECT_DATA=$M/e/data AGENTEYES_REDIRECT_VIDEOS=$M/e/videos -- --help; chmod 755 "$M/e/data" "$M/e/videos"
mk p;  run p  AGENTEYES_REDIRECT_DATA=$M/p/data AGENTEYES_REDIRECT_VIDEOS=$M/p/videos -- --help
# Guard-defeat probes: the 'REAL root' the guard protects is computed from THIS process's HOME / XDG_DATA_HOME.
mkdir -p "$M/x1/videos";                       run x1 AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/x1/videos -- --help
mkdir -p "$M/x2/home" "$M/x2/videos";          run x2 HOME=$M/x2/home AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/x2/videos -- --help
mkdir -p "$M/x3/xdg" "$M/x3/videos";           run x3 XDG_DATA_HOME=$M/x3/xdg AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/x3/videos -- --help
mkdir -p "$M/x4/home" "$M/x4/data";            run x4 HOME=$M/x4/home AGENTEYES_REDIRECT_DATA=$M/x4/data AGENTEYES_REDIRECT_VIDEOS=$H/Videos -- --help

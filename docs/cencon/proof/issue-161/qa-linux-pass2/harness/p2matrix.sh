#!/bin/bash
# QA #161 pass 2 (Linux): attack cases beyond the predecessor's matrix. Every run: strace -f + Landlock
# deny on BOTH real roots. Usage: p2matrix.sh <AE> <LLD> <R1> <R2> <M> <EV>
AE=$1 LLD=$2 R1=$3 R2=$4 M=$5 EV=$6; H=$(getent passwd "$(id -un)" | cut -d: -f6)
mkdir -p "$M" "$EV"; rm -rf "$M"/* "$EV"/*
run() { local label=$1; shift; local cwd=$M; local envs=(); while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  ( cd "${CWD:-$M}" && env -u AGENTEYES_REDIRECT_DATA -u AGENTEYES_REDIRECT_VIDEOS "${envs[@]}" strace -f -qq -e trace=%file,%desc -o "$EV/$label.strace" "$LLD" "$R1" "$R2" -- "$@" > "$EV/$label.out" 2> "$EV/$label.err" ); local rc=$?
  local hits=$(grep -vF execve "$EV/$label.strace" | grep -E "$R1|$R2")
  echo; echo "[$label] exit=$rc real-root syscalls=$(printf '%s' "$hits" | grep -c .) opens/creates=$(printf '%s' "$hits" | grep -cE 'open|creat|mkdir|unlink|rename') EACCES=$(printf '%s' "$hits" | grep -c EACCES) probes=$(grep -c 'redirect-probe' "$EV/$label.strace")"
  grep -v 'lldeny' "$EV/$label.err" | sed 's/^/  stderr: /' | cut -c1-420
  printf '%s\n' "$hits" | grep -E 'open|creat|mkdir' | sed -E 's/^[0-9]+ +/  real-open: /' | sort | uniq -c | head -6; }
# P0 POSITIVE CONTROL, same instrument, same run: an unredirected CLI DOES open the real state root.
run P0 -- "$AE" --help
# ATK: the predecessor's exact attack, serve included.
mkdir -p $M/atk/home $M/atk/videos; run ATK HOME=$M/atk/home AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/atk/videos -- timeout -s INT 25 "$AE" serve --port 17163
# y: spoof EVERYTHING the environment half reads.
mkdir -p $M/y1/home $M/y1/xdg $M/y1/cfg $M/y1/videos; run y1 HOME=$M/y1/home XDG_DATA_HOME=$M/y1/xdg XDG_CONFIG_HOME=$M/y1/cfg AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/y1/videos -- "$AE" --help
mkdir -p $M/y2/home $M/y2/xdg $M/y2/cfg $M/y2/data; run y2 HOME=$M/y2/home XDG_DATA_HOME=$M/y2/xdg XDG_CONFIG_HOME=$M/y2/cfg AGENTEYES_REDIRECT_DATA=$M/y2/data AGENTEYES_REDIRECT_VIDEOS=$H/Videos -- "$AE" --help
mkdir -p $M/y3/videos; run y3 AGENTEYES_REDIRECT_DATA=$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/y3/videos -- env -u HOME "$AE" --help
mkdir -p $M/y4/home $M/y4/videos; run y4 HOME=$M/y4/home AGENTEYES_REDIRECT_DATA=$M/y4/../../../../../../../../../..$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/y4/videos -- "$AE" --help
mkdir -p $M/y5/home $M/y5/videos; ln -s $H/.local/share $M/y5/dlink; run y5 HOME=$M/y5/home AGENTEYES_REDIRECT_DATA=$M/y5/dlink AGENTEYES_REDIRECT_VIDEOS=$M/y5/videos -- "$AE" --help
mkdir -p $M/y6/home $M/y6/videos; run y6 HOME=$M/y6/home AGENTEYES_REDIRECT_DATA=$H/Videos AGENTEYES_REDIRECT_VIDEOS=$M/y6/videos -- "$AE" --help
mkdir -p $M/y7/home $M/y7/videos; CWD=$H/.local/share run y7 HOME=$M/y7/home AGENTEYES_REDIRECT_DATA=/proc/self/cwd AGENTEYES_REDIRECT_VIDEOS=$M/y7/videos -- "$AE" --help
mkdir -p $M/y8/home $M/y8/videos; run y8 HOME=$M/y8/home AGENTEYES_REDIRECT_DATA=/proc/self/root$H/.local/share AGENTEYES_REDIRECT_VIDEOS=$M/y8/videos -- "$AE" --help
# z: a person whose REAL library is relocated by XDG_DATA_HOME / XDG_CONFIG_HOME (simulated in scratch).
mkdir -p $M/reloc/xdg/AgentEyes $M/reloc/cfg $M/reloc/vids/AgentEyes; echo person-library > $M/reloc/xdg/AgentEyes/MARKER; echo person-recordings > $M/reloc/vids/AgentEyes/MARKER
printf 'XDG_VIDEOS_DIR="%s"\n' "$M/reloc/vids" > $M/reloc/cfg/user-dirs.dirs
mkdir -p $M/zc/videos; run zc XDG_DATA_HOME=$M/reloc/xdg AGENTEYES_REDIRECT_DATA=$M/reloc/xdg AGENTEYES_REDIRECT_VIDEOS=$M/zc/videos -- "$AE" --help
mkdir -p $M/za/spoof $M/za/videos; run za XDG_DATA_HOME=$M/za/spoof AGENTEYES_REDIRECT_DATA=$M/reloc/xdg AGENTEYES_REDIRECT_VIDEOS=$M/za/videos -- "$AE" --help
echo "  person's relocated state root after za: $(cd $M/reloc/xdg/AgentEyes && find . -mindepth 1 | sort | tr '\n' ' ')"
mkdir -p $M/zvc/data; run zvc XDG_CONFIG_HOME=$M/reloc/cfg AGENTEYES_REDIRECT_DATA=$M/zvc/data AGENTEYES_REDIRECT_VIDEOS=$M/reloc/vids -- "$AE" --help
mkdir -p $M/zva/data $M/zva/cfg; run zva XDG_CONFIG_HOME=$M/zva/cfg AGENTEYES_REDIRECT_DATA=$M/zva/data AGENTEYES_REDIRECT_VIDEOS=$M/reloc/vids -- "$AE" --help
# L: a link INSIDE a scratch root that leads into the real library (the guard resolves only the path TO each root).
mkdir -p $M/L1/data/AgentEyes $M/L1/videos; ln -s "$R2/logs" $M/L1/data/AgentEyes/logs; run L1 AGENTEYES_REDIRECT_DATA=$M/L1/data AGENTEYES_REDIRECT_VIDEOS=$M/L1/videos -- "$AE" --help
mkdir -p $M/L2/data $M/L2/videos/AgentEyes; ln -s $R1 $M/L2/videos/AgentEyes/linked-real; run L2 AGENTEYES_REDIRECT_DATA=$M/L2/data AGENTEYES_REDIRECT_VIDEOS=$M/L2/videos -- timeout -s INT 25 "$AE" serve --port 17271

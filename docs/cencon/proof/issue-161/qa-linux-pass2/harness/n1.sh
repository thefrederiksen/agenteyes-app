#!/bin/bash
HOME_OWNER=$(getent passwd "$(id -un)" | cut -d: -f6)   # the account home (passwd), never $HOME
# QA #161 pass 2 job 2 (N1): missing / 0555 / writable-in-unwritable-parent / file, each under strace + Landlock.
AE=$1 LLD=$2 R1=$3 R2=$4 M=$5 EV=$6
rm -rf "$M" "$EV"; mkdir -p "$M" "$EV"
run() { local label=$1; shift; local envs=(); while [ "$1" != "--" ]; do envs+=("$1"); shift; done; shift
  ( cd "$M" && env -u AGENTEYES_REDIRECT_DATA -u AGENTEYES_REDIRECT_VIDEOS "${envs[@]}" strace -f -qq -e trace=%file,%desc -o "$EV/$label.strace" "$LLD" "$R1" "$R2" -- "$@" > "$EV/$label.out" 2> "$EV/$label.err" ); local rc=$?
  echo; echo "[$label] exit=$rc real-root syscalls=$(grep -vF execve "$EV/$label.strace" | grep -cE "$R1|$R2")"
  grep -v lldeny "$EV/$label.err" | sed 's/^/  stderr: /' | cut -c1-400
  echo "  probe syscalls in order:"; grep -E 'redirect-probe' "$EV/$label.strace" | sed -E 's/^[0-9]+ +//; s/-[0-9]+-[0-9a-f]{32}/-<pid>-<guid>/' | sed 's/^/    /'
  echo "  tree after: $(cd "$M/$label" && find . | sort | tr '\n' ' ')"; }
# n1: data folder missing (typo) - must not be created
mkdir -p $M/n1/videos; run n1 AGENTEYES_REDIRECT_DATA=$M/n1/data AGENTEYES_REDIRECT_VIDEOS=$M/n1/videos -- "$AE" --help
# n2: videos folder missing
mkdir -p $M/n2/data; run n2 AGENTEYES_REDIRECT_DATA=$M/n2/data AGENTEYES_REDIRECT_VIDEOS=$M/n2/videos -- "$AE" --help
# n3: data writable, videos 0555 - data probe must succeed+unlink, videos probe EACCES, nothing left
mkdir -p $M/n3/data $M/n3/videos; chmod 555 $M/n3/videos; run n3 AGENTEYES_REDIRECT_DATA=$M/n3/data AGENTEYES_REDIRECT_VIDEOS=$M/n3/videos -- "$AE" --help; chmod 755 $M/n3/videos
# n4: both folders exist and are writable, but their PARENT is 0555 - must be ACCEPTED (the folder is what matters)
mkdir -p $M/n4/ro/data $M/n4/ro/videos; chmod 555 $M/n4/ro; run n4 AGENTEYES_REDIRECT_DATA=$M/n4/ro/data AGENTEYES_REDIRECT_VIDEOS=$M/n4/ro/videos -- "$AE" --help; chmod 755 $M/n4/ro
# n5: missing folder whose parent is 0555 - refused as missing, nothing created
mkdir -p $M/n5/ro $M/n5/videos; chmod 555 $M/n5/ro; run n5 AGENTEYES_REDIRECT_DATA=$M/n5/ro/data AGENTEYES_REDIRECT_VIDEOS=$M/n5/videos -- "$AE" --help; chmod 755 $M/n5/ro
# n6: the value is a FILE
mkdir -p $M/n6/videos; echo x > $M/n6/data; run n6 AGENTEYES_REDIRECT_DATA=$M/n6/data AGENTEYES_REDIRECT_VIDEOS=$M/n6/videos -- "$AE" --help
# n7: ORDER - videos on the REAL Videos folder (overlap) AND data 0555: overlap must win, and NO probe anywhere
mkdir -p $M/n7/data; chmod 555 $M/n7/data; run n7 AGENTEYES_REDIRECT_DATA=$M/n7/data AGENTEYES_REDIRECT_VIDEOS=$HOME_OWNER/Videos -- "$AE" --help; chmod 755 $M/n7/data

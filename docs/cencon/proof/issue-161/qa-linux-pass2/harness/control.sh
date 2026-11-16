#!/bin/bash
HOME_OWNER=$(getent passwd "$(id -un)" | cut -d: -f6)   # the account home (passwd), never $HOME
# Pass-2 QA control: the instrument must SEE a real-root access (positive) and allow scratch.
R1=$1 R2=$2 SCR=$3
for p in "$R1" "$R2" "$R2/logs"; do
  ls "$p" >/dev/null 2>$SCR/x.err; echo "ls    $p -> exit $? : $(head -1 $SCR/x.err)"
  touch "$p/.qa161p2-landlock-probe" 2>$SCR/x.err; echo "touch $p/.qa161p2-landlock-probe -> exit $? : $(head -1 $SCR/x.err)"
done
touch "$SCR/probe" ; echo "touch scratch -> exit $?"
ls $HOME_OWNER/Videos >/dev/null; echo "ls ~/Videos (parent) -> exit $?"

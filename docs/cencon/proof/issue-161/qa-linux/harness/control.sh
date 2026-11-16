#!/bin/bash
# Section 0: the denial instrument fires on both real roots, and does not block scratch.
for p in "$R1" "$R2" "$R2/logs"; do
  ls "$p" >/dev/null 2>$SCR/x.err; echo "ls    $p -> exit $? : $(cat $SCR/x.err | head -1)"
  touch "$p/.qa161-landlock-probe" 2>$SCR/x.err; echo "touch $p/.qa161-landlock-probe -> exit $? : $(head -1 $SCR/x.err)"
done
cat "$R2/linux-app.json" >/dev/null 2>$SCR/x.err; echo "cat   $R2/linux-app.json -> exit $? : $(head -1 $SCR/x.err)"
touch "$SCR/probe" ; echo "touch scratch -> exit $?"
ls <home>/Videos >/dev/null; echo "ls ~/Videos (parent, a sibling-only grant) -> exit $?"
ls <home>/ReposFred >/dev/null; echo "ls ~/ReposFred (sibling) -> exit $?"

#!/usr/bin/env bash
# Criterion 3: no fifth occurrence. Run from the repo root. Two forms of every search:
#   LINE   - git grep, one line at a time
#   JOINED - each file with line breaks collapsed: tr '\n' ' ' | tr -s ' '  (wrapped text, issue #218)
# Scope: every tracked file EXCEPT two, excluded by name because they quote the old strings on purpose:
# docs/cencon/proof/ (the historical record) and tests/Shared/SessionRemedyGuardTests.cs (the guard's
# own fixtures - its positive controls ARE the four strings). Pattern A is the remedy itself; pattern B is any mention of an Xorg session name.
set -uo pipefail
A="log in with ['\"]?Ubuntu on Xorg|choose ['\"]?Ubuntu on Xorg"
B="Ubuntu on Xorg|GNOME on Xorg|on Xorg['\"]"
scope=(-- . ':(exclude)docs/cencon/proof/**' ':(exclude)tests/Shared/SessionRemedyGuardTests.cs')
line_search() { git grep -n -I -i -E "$1" "${scope[@]}"; }
joined_search() {
    git ls-files -z "${scope[@]:1}" | while IFS= read -r -d '' f; do
        grep -Iq . "$f" 2>/dev/null || continue
        tr '\n' ' ' < "$f" | tr -s ' ' | grep -o -i -E "$1" | sed "s|^|$f: |"
    done
}
report() { local n; n=$(printf '%s' "$2" | grep -c . ); echo "  $1: $n hit(s)"; [[ -n "$2" ]] && printf '%s\n' "$2" | sed 's/^/    /'; }
echo "repo head: $(git rev-parse --short HEAD), working tree $(git status --porcelain | grep -c . ) changed path(s)"
echo "Pattern A (the remedy): $A"
echo "Pattern B (any Xorg session name): $B"
echo
echo "== 1. POSITIVE CONTROL: plant a hit WRAPPED across a line break, as README.md:75-76 wrapped before"
plant="docs/releases/zz-planted-control-221.md"
printf 'Wayland sessions are not supported - log in with\n"Ubuntu on Xorg".\n' > "$plant"
git add -N "$plant"   # intent-to-add so git grep / ls-files see it; removed below
report "LINE   A" "$(line_search "$A")"
report "JOINED A" "$(joined_search "$A")"
git rm -q --cached "$plant"; rm -f "$plant"
echo "  planted file removed: $([[ -e $plant ]] && echo NO || echo yes)"
echo
echo "== 2. POSITIVE CONTROL: plant a hit on ONE line"
printf 'log in with '"'"'Ubuntu on Xorg'"'"'\n' > "$plant"; git add -N "$plant"
report "LINE   A" "$(line_search "$A")"
report "JOINED A" "$(joined_search "$A")"
git rm -q --cached "$plant"; rm -f "$plant"
echo "  planted file removed: $([[ -e $plant ]] && echo NO || echo yes)"
echo
echo "== 3. THE REAL SEARCH, nothing planted"
report "LINE   A" "$(line_search "$A")"
report "JOINED A" "$(joined_search "$A")"
report "LINE   B" "$(line_search "$B")"
report "JOINED B" "$(joined_search "$B")"

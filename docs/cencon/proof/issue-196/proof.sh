#!/bin/bash
# Issue #196 proof. Every scenario runs on a throwaway clone; the real tree is fingerprinted before and after.
set -u
REPO=~/ReposFred/agenteyes-dev156
S=<scratch>/p
PWSH=~/.local/bin/pwsh
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet DOTNET_ROLL_FORWARD=LatestMajor
export GIT_AUTHOR_NAME=scratch GIT_AUTHOR_EMAIL=scratch@example.invalid GIT_COMMITTER_NAME=scratch GIT_COMMITTER_EMAIL=scratch@example.invalid
FIX=$(git -C "$REPO" rev-parse HEAD)
rm -rf "$S"; mkdir -p "$S"

versions() { (cd "$1" && grep -rn "<Version>" src tools --include='*.csproj' | sort); }
fingerprint() { (cd "$REPO" && git rev-parse HEAD && git status --porcelain --untracked-files=all && git tag -l 'v9*' && versions "$REPO" | shasum); }
vtest() { (cd "$1" && dotnet test "tests/AgentEyes.Tests.$2" -c Release --filter "FullyQualifiedName~ReleaseVersionTests" > "$S/vt.log" 2>&1; echo "dotnet test $2 exit=$?"; grep -A4 "Error Message" "$S/vt.log"; grep -E "Passed!|Failed!" "$S/vt.log"); }
clone() { git clone -q "$REPO" "$S/$1" && git -C "$S/$1" checkout -q -b scratch "$2"; }

echo "=== real tree fingerprint BEFORE"; fingerprint > "$S/fp-before"; cat "$S/fp-before"

echo; echo "=== C1 REPRODUCED: old script (main 02a4622) on a scratch clone, bump to 9.9.9"
clone before 02a4622
(cd "$S/before" && $PWSH -NoProfile -File scripts/new-release.ps1 9.9.9 2>&1; echo "script exit=$?"; echo "-- versions after:"; versions .; echo "-- git status:"; git status --short; echo "-- HEAD and tag:"; git log --oneline -1; git log --oneline -1 v9.9.9)

echo; echo "=== C2 FIXED: six projects (throwaway tools/AgentEyes.Throwaway added at 1.12.0), bump to 9.9.9"
clone six "$FIX"
(cd "$S/six" && mkdir -p tools/AgentEyes.Throwaway && printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n    <Version>1.12.0</Version>\n  </PropertyGroup>\n</Project>\n' > tools/AgentEyes.Throwaway/AgentEyes.Throwaway.csproj && git add -A && git commit -qm "scratch: throwaway sixth project" \
  && $PWSH -NoProfile -File scripts/new-release.ps1 9.9.9 2>&1; echo "script exit=$?"; echo "-- versions after:"; versions .; echo "-- git status:"; git status --short; echo "-- tag v9.9.9 points at:"; git log --oneline -1 v9.9.9; git show --stat --format= v9.9.9)
vtest "$S/six" Linux
echo "-- throwaway project removed (scratch clone deleted with it):"; rm -rf "$S/six"; ls "$S"

echo; echo "=== C3 RED: DesktopApp set out of step (1.11.0), the shared test on both suites"
clone red "$FIX"
sed -i '' 's|<Version>1.12.0</Version>|<Version>1.11.0</Version>|' "$S/red/src/AgentEyes.DesktopApp/AgentEyes.DesktopApp.csproj"
versions "$S/red"
vtest "$S/red" Mac
vtest "$S/red" Linux

echo; echo "=== C3b RED: a NEW sixth project out of step (1.0.0) is found and named"
clone red6 "$FIX"
mkdir -p "$S/red6/tools/AgentEyes.Throwaway" && printf '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <Version>1.0.0</Version>\n  </PropertyGroup>\n</Project>\n' > "$S/red6/tools/AgentEyes.Throwaway/AgentEyes.Throwaway.csproj"
vtest "$S/red6" Linux

echo; echo "=== REFUSAL leaves the tree untouched: a project declaring TWO <Version> elements"
clone two "$FIX"
(cd "$S/two" && sed -i '' 's|<Version>1.12.0</Version>|<Version>1.12.0</Version><Version>1.12.0</Version>|' tools/AgentEyes.Setup/AgentEyes.Setup.csproj && git commit -qam "scratch: two versions" \
  && $PWSH -NoProfile -File scripts/new-release.ps1 9.9.9 2>&1 | grep -E "declares|exit|bumped"; echo "script exit=${PIPESTATUS[0]}"; echo "-- git status (expect empty):"; git status --short; git tag -l 'v9*')

echo; echo "=== NO TAG ON A FAILED COMMIT: a pre-commit hook that refuses"
clone hook "$FIX"
(cd "$S/hook" && printf '#!/bin/sh\necho "scratch hook: refusing the commit"\nexit 1\n' > .git/hooks/pre-commit && chmod +x .git/hooks/pre-commit \
  && $PWSH -NoProfile -File scripts/new-release.ps1 9.9.9 2>&1 | grep -E "refusing|failed|Tagged"; echo "-- tags matching v9* (expect none):"; git tag -l 'v9*'; echo "-- HEAD:"; git log --oneline -1)

echo; echo "=== real tree fingerprint AFTER"; fingerprint > "$S/fp-after"; cat "$S/fp-after"
if cmp -s "$S/fp-before" "$S/fp-after"; then echo "[OK] real tree unchanged"; else echo "[FAIL] real tree changed"; fi

# usage: rungate.sh <label> <optin 0|1> [extra dotnet test args]
S=${QA_SCRATCH:?set QA_SCRATCH to a scratch folder}
label=$1; optin=$2; shift 2
export SHIM_REAL_DIR='C:\Users\<user>\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin'
export SHIM_LOG_DIR="$S/log-$label"; rm -rf "$SHIM_LOG_DIR"; mkdir -p "$SHIM_LOG_DIR"
export QA_RECORD_FFMPEG_DIR=$(cygpath -w "$S/shim")
export PATH="$S/shim:$PATH"
if [ "$optin" = 1 ]; then export AGENTEYES_FFMPEG_TESTS=1; else unset AGENTEYES_FFMPEG_TESTS; fi
cd /c/ReposFred/agenteyes-app
t0=$(date +%s)
dotnet test AgentEyes.sln -c Release --no-build "$@" > "$S/test-$label.txt" 2>&1; rc=$?
t1=$(date +%s)
grep -E "^(Passed|Failed)!|Total tests|error" "$S/test-$label.txt" | head -20
echo "RESULT $label: exit $rc, wall $((t1-t0)) s, ffmpeg starts $(ls $SHIM_LOG_DIR | grep -c '^ffmpeg-'), ffprobe starts $(ls $SHIM_LOG_DIR | grep -c '^ffprobe-')"

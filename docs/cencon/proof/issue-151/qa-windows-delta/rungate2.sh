# usage: rungate2.sh <label> <optin 0|1>   (QA_SCRATCH must be a POSIX path - a C: path breaks PATH in bash)
S=${QA_SCRATCH:?}; label=$1; optin=$2
export SHIM_REAL_DIR='C:\Users\<user>\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1.1-full_build\bin'
rm -rf "$S/log-$label"; mkdir -p "$S/log-$label"; export SHIM_LOG_DIR=$(cygpath -w "$S/log-$label")
export QA_RECORD_FFMPEG_DIR=$(cygpath -w "$S/shim"); export PATH="$S/shim:$PATH"
if [ "$optin" = 1 ]; then export AGENTEYES_FFMPEG_TESTS=1; else unset AGENTEYES_FFMPEG_TESTS; fi
echo "where ffmpeg -> $(where ffmpeg | head -1)"
cd /c/ReposFred/agenteyes-app; t0=$(date +%s)
dotnet test AgentEyes.sln -c Release --no-build > "$S/test-$label.txt" 2>&1; rc=$?; t1=$(date +%s)
grep -E "Test run for|^(Passed|Failed)!|^\s+Failed " "$S/test-$label.txt" | head -20
echo "RESULT $label: exit $rc, wall $((t1-t0)) s, ffmpeg starts $(ls $S/log-$label | grep -c '^ffmpeg-'), ffprobe starts $(ls $S/log-$label | grep -c '^ffprobe-')"

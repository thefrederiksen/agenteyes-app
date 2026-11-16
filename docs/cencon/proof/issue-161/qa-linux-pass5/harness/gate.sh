#!/bin/bash
# gate.sh <label> <project> <canary-port> [optin]: runs INSIDE lldeny+strace. First a canary redirected child whose probe
# is pointed at <canary-port> (closed) - the in-run positive control - then the suite, no build.
cd <home>/ReposFred/agenteyes-app
C=$(mktemp -d <tmp-scratch>); mkdir -p $C/d $C/v
AGENTEYES_SERVICE_PROBE_PORT=$3 AGENTEYES_REDIRECT_DATA=$C/d AGENTEYES_REDIRECT_VIDEOS=$C/v dotnet src/AgentEyes.Core/bin/Release/net8.0/agenteyes.dll --help >/dev/null 2>&1
echo "canary child exit $?"
[ "$4" = optin ] && export AGENTEYES_FFMPEG_TESTS=1
dotnet test tests/$2 -c Release --no-build -nodeReuse:false 2>&1 | grep -E "^\s+(Skipped|Failed) |Passed!|Failed!|error"

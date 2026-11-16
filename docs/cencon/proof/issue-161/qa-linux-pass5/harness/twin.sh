#!/bin/bash
# INSIDE --network none: 7882 free, as on a machine whose recorder is stopped or restarting.
cd /tmp
( for i in $(seq 1 400); do r=$(curl -s -m 0.5 -X POST -o /tmp/body -w '%{http_code}' http://127.0.0.1:7882/record/start 2>/dev/null); [ "$r" = 200 ] && { echo "THIRD-PARTY CLIENT: POST /record/start to 127.0.0.1:7882 -> HTTP $r, body: $(cut -c1-120 /tmp/body)"; break; }; sleep 0.02; done ) &
CL=$!
strace -f -qq -e trace=bind,listen -o /tmp/tw dotnet exec /usr/lib/dotnet/sdk/8.0.134/vstest.console.dll $1/AgentEyes.Tests.Linux.dll "--TestCaseFilter:FullyQualifiedName~RealProcess_ServiceOnTheDefaultPortNamesARelocatedLibrary_RedirectOntoItRefused" --ResultsDirectory:/tmp/tr 2>&1 | grep -E "Passed!|Failed!|Skipped|Total"
wait $CL
echo "binds to 7882 in the test run:"; grep 'htons(7882)' /tmp/tw | sed 's/^[0-9]* //' | cut -c1-140

#!/bin/sh
# The whole Linux suite (opt-in) in a --network none container limited to $1 CPU(s): 7882 is the container's own.
docker run --rm --cpus=$1 --network none --user 1000:1000 --tmpfs /tmp:exec,mode=1777 \
  -v /usr/lib/dotnet:/usr/lib/dotnet:ro -v /etc/passwd:/etc/passwd:ro -v /etc/group:/etc/group:ro -v <repo>:<repo>:ro \
  -e HOME=/tmp/h -e AGENTEYES_FFMPEG_TESTS=1 -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ubuntu:24.04 \
  sh -c "mkdir -p /tmp/h && /usr/lib/dotnet/dotnet exec /usr/lib/dotnet/sdk/8.0.134/vstest.console.dll <repo>/tests/AgentEyes.Tests.Linux/bin/Release/net8.0/AgentEyes.Tests.Linux.dll --ResultsDirectory:/tmp/r" 2>&1 | grep -E "^\s+Failed |Failed:|Passed:" | sed 's/ - AgentEyes.*//'

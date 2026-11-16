#!/bin/sh
# Runs the built Linux test assembly in a container: --network none (its own loopback, 7882 free), the host's
# .NET and passwd read-only, the repo read-only, NOTHING of the owner's library mounted.
docker run --rm --network none --user $(id -u):$(id -g) --tmpfs /tmp:exec,mode=1777 \
  -v /usr/lib/dotnet:/usr/lib/dotnet:ro -v /etc/passwd:/etc/passwd:ro -v /etc/group:/etc/group:ro -v <repo>:<repo>:ro \
  -e HOME=/tmp/h -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 -e F="$1" ubuntu:24.04 \
  sh -c 'mkdir -p /tmp/h && /usr/lib/dotnet/dotnet exec /usr/lib/dotnet/sdk/8.0.134/vstest.console.dll <repo>/tests/AgentEyes.Tests.Linux/bin/Release/net8.0/AgentEyes.Tests.Linux.dll --ResultsDirectory:/tmp/r "--TestCaseFilter:$F"'

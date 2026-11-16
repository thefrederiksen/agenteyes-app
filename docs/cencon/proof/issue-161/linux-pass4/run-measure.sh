#!/bin/sh
# Usage: run-measure.sh <runs>. Container: --network none (its own loopback; 7882 there is NOT the owner's), the
# host's .NET and python read-only, the repo read-only, nothing of the owner's library mounted.
docker run --rm --network none --user 1000:1000 --tmpfs /tmp:exec,mode=1777 \
  -v /usr/lib/dotnet:/usr/lib/dotnet:ro -v /usr/bin/python3.12:/usr/bin/python3.12:ro -v /usr/lib/python3.12:/usr/lib/python3.12:ro -v /usr/lib/x86_64-linux-gnu/libexpat.so.1.9.1:/lib/x86_64-linux-gnu/libexpat.so.1:ro \
  -v /etc/passwd:/etc/passwd:ro -v /etc/group:/etc/group:ro -v <repo>:<repo>:ro \
  -v <scratchpad>/measure.sh:/m.sh:ro -v <scratchpad>/standin.py:/standin.py:ro -v <scratchpad>/silent.py:/silent.py:ro \
  -e HOME=/tmp/h -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ubuntu:24.04 \
  bash -c "mkdir -p /tmp/h && /m.sh <repo>/tests/AgentEyes.Tests.Linux/bin/Release/net8.0/agenteyes.dll $1"

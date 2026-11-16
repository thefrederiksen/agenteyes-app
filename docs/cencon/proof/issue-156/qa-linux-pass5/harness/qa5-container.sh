#!/bin/bash
# QA #156 pass 5/5b (Linux seat): the owner read and the Linux EPERM row in containers WITHOUT statx in libc
# (ubuntu:18.04, glibc 2.27) and WITH it (ubuntu:24.04, glibc 2.39), builds pass 5 (3e818e6) and head (f6d16bf).
# Unlike the developer's harness, the EPERM is REAL here: the prober runs under llprobe (Landlock refusing
# other /proc/<pid> reads + seccomp kill->EPERM) inside the container, and the real public path
# (IdentifyProcess -> Inspect) and the real CLI `package` run. Prober uid 1000; "another user" = a root sleep.
set -u
S=${SCRATCH:?}; MSRT=$S/msrt
for img in ubuntu:18.04 ubuntu:24.04; do for n in b3e head; do
  case $n in b3e) AE=$S/wt/before-3e818e6/src/AgentEyes.Core/bin/Release/net8.0;; head) AE=<HOME>/ReposFred/agenteyes-116/src/AgentEyes.Core/bin/Release/net8.0;; esac
  W=$S/runs/container-$(echo $img | tr ':.' '--')-$n; rm -rf "$W"; mkdir -p "$W/home/.config" "$W/home/.local/share" "$W/home/Videos/AgentEyes"
  echo "===== container $img, build $n ($(strings $AE/agenteyes.dll | grep -m1 -oE '1\.[0-9]+\.[0-9]+\+[0-9a-f]{40}'))"
  docker run --rm --network none -v "$MSRT":/dn:ro -v "$S/hout/$n":/h:ro -v "$AE":/ae:ro -v "$S/harness/llprobe-static":/ll:ro -v "$W":/w \
    -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 $img sh -c '
    sleep 600 & R=$!
    chroot --userspec=1000:1000 / env HOME=/w/home XDG_CONFIG_HOME=/w/home/.config XDG_DATA_HOME=/w/home/.local/share R=$R sh -c "
      cd /w; H=\"/dn/dotnet /h/AgentEyes.Tests.Linux.dll\"
      echo \"isolation: \$(\$H paths | grep PATHS)\"
      sleep 600 & L=\$!
      echo \"live same-user stand-in writer L=\$L (uid \$(stat -c %u /proc/\$L)), root process R=\$R (uid \$(stat -c %u /proc/\$R))\"
      echo \"--- 1. totality probe (EPERM injected on the row, real owner read):\"; \$H total \$L \$R 2147483640 2>&1 | sed s/^/\ \ /
      D=/w/home/Videos/AgentEyes/2026-11-16_190000_video; mkdir -p \$D
      printf %s \"{ \\\"Tool\\\": \\\"AgentEyes\\\", \\\"Mode\\\": \\\"video\\\", \\\"Label\\\": \\\"video\\\", \\\"CreatedUtc\\\": \\\"2026-11-16T19:00:00.0000000Z\\\", \\\"MonitorIndex\\\": 1, \\\"MonitorName\\\": \\\"stand-in\\\", \\\"PendingMux\\\": { \\\"Mode\\\": \\\"video\\\", \\\"Source\\\": \\\"mic\\\", \\\"RawVideo\\\": \\\"raw.mp4\\\", \\\"FinalFile\\\": \\\"recording.mp4\\\", \\\"RawDurationSeconds\\\": 0 } }\" > \$D/manifest.json
      head -c 4096 /dev/urandom > \$D/raw.mp4
      \$H owner \$D \$L 2>&1 | grep OWNER | cut -c14-160
      echo \"--- 2. plain prober, real path (control):\"; \$H inspect \$D 2>&1 | grep -E \"recorded writer|Inspect|Exception\" | cut -c1-300 | sed s/^/\ \ /
      echo \"--- 3. Landlock + kill EPERM, real path:\"; /ll --kill-eperm -- /dn/dotnet /h/AgentEyes.Tests.Linux.dll inspect \$D 2>&1 | grep -E \"llprobe|recorded writer|Inspect|Exception\" | cut -c1-300 | sed s/^/\ \ /
      echo \"--- 4. the real CLI under Landlock + kill EPERM: agenteyes package --no-frames\"; /ll --kill-eperm -- /dn/dotnet /ae/agenteyes.dll package \$D --no-frames > /w/pkg-live.log 2>&1; echo \"  exit \$?: \$(grep -m1 -iE \"refusing|exception|error\" /w/pkg-live.log | cut -c1-260)\"
      echo \"  after: owner record \$(test -f \$D/capture.owner.json && echo PRESENT || echo ABSENT); manifest has a PostProcessing mux state: \$(grep -c State \$D/manifest.json); live writer L \$(kill -0 \$L 2>/dev/null && echo ALIVE || echo gone)\"
      sed -i \"s/\\\"Pid\\\": *\$L\\b/\\\"Pid\\\": \$R/\" \$D/capture.owner.json
      echo \"--- 5. writer pid now held by ANOTHER USER (root R=\$R), Landlock + kill EPERM, real path (positive control):\"; tr -d \"\\n \" < \$D/capture.owner.json | grep -o \"Writers.*\" | cut -c1-90 | sed s/^/\ \ /
      /ll --kill-eperm -- /dn/dotnet /h/AgentEyes.Tests.Linux.dll inspect \$D 2>&1 | grep -E \"recorded writer|Inspect|Exception\" | cut -c1-300 | sed s/^/\ \ /
      kill \$L
    "
    kill $R'
done; done

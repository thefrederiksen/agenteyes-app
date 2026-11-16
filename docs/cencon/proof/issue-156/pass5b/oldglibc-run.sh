#!/bin/bash
# #156 pass 5b: the real owner read and the real Linux EPERM row of the ownership predicate, in a container
# whose glibc has no statx (Ubuntu 18.04, glibc 2.27) and, as the control, one whose glibc has it (24.04,
# glibc 2.39). The prober runs as uid 1000; a root `sleep` in the same container is "another user".
# Inputs: MSRT = a Microsoft .NET 8 runtime (dotnet-install.sh --channel 8.0 --runtime dotnet --install-dir),
#         H = the built oldglibc-harness output dir, BEFORE / AFTER = agenteyes.dll output dirs of the two builds.
set -u
for img in ubuntu:18.04 ubuntu:24.04; do for b in BEFORE AFTER; do
  D=${!b}; echo "===== container $img, build $b"
  docker run --rm -v "$MSRT":/dn:ro -v "$D":/ae:ro -v "$H":/h:ro -e HOME=/tmp -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 $img \
    sh -c 'sleep 600 & R=$!; chroot --userspec=1000:1000 / /dn/dotnet /h/OldGlibc.dll /ae/agenteyes.dll $R; kill $R'
done; done

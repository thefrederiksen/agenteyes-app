#!/bin/bash
# count.sh <strace-file> <port>: connects to 127.0.0.1:<port>, in BOTH forms .NET uses: AF_INET sin_port=htons(P) and the
# dual-mode AF_INET6 sin6_port=htons(P) ... "::ffff:127.0.0.1". Any address: an address-blind count (htons(P) anywhere in a connect).
f=$1; p=$2
echo "connects with htons($p): $(grep -E 'connect\(' "$f" | grep -c "port=htons($p)")  [v4: $(grep -E 'connect\(' "$f" | grep "sin_port=htons($p)" | grep -c '127.0.0.1') ; v4-mapped: $(grep -E 'connect\(' "$f" | grep "sin6_port=htons($p)" | grep -c '::ffff:127.0.0.1')]"

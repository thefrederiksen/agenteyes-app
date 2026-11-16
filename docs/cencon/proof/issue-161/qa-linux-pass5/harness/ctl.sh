#!/bin/bash
# INSIDE --network none: the instrument's positive control for 7882 itself.
D=/usr/lib/dotnet/dotnet; mkdir -p /tmp/c1/d /tmp/c1/v /tmp/c2/d /tmp/c2/v
echo "## C1: redirected child, AGENTEYES_SERVICE_PROBE_PORT UNSET (production default) -> must show a connect to 7882"
env -u AGENTEYES_SERVICE_PROBE_PORT AGENTEYES_REDIRECT_DATA=/tmp/c1/d AGENTEYES_REDIRECT_VIDEOS=/tmp/c1/v strace -f -qq -e trace=connect,bind -o /tmp/t1 $D /app/agenteyes.dll --help >/dev/null 2>&1; echo "exit $?"
/count.sh /tmp/t1 7882; grep 'htons(7882)' /tmp/t1 | head -1 | sed 's/^[0-9]* //' | cut -c1-200
echo "## C2: same child, AGENTEYES_SERVICE_PROBE_PORT=41999 -> must show 0 to 7882 and 1 to 41999"
AGENTEYES_SERVICE_PROBE_PORT=41999 AGENTEYES_REDIRECT_DATA=/tmp/c2/d AGENTEYES_REDIRECT_VIDEOS=/tmp/c2/v strace -f -qq -e trace=connect,bind -o /tmp/t2 $D /app/agenteyes.dll --help 2>/tmp/e2 >/dev/null; echo "exit $?"
/count.sh /tmp/t2 7882; /count.sh /tmp/t2 41999; grep -o "the service probe was pointed[^:]*" /tmp/e2 | head -1

#!/bin/bash
# usage: mutate.sh NAME FILE PYTHON-OLD PYTHON-NEW
set -e
cd /home/soren/ReposFred/agenteyes-app
name=$1; file=$2; old=$3; new=$4
cp "$file" "$file.orig"
python3 -c "
import sys;p=sys.argv[1];s=open(p).read();o=sys.argv[2];n=sys.argv[3]
assert s.count(o)>=1, 'mutation anchor not found'
open(p,'w').write(s.replace(o,n))" "$file" "$old" "$new"
echo "=== MUTATION $name: in $file replaced [$old] with [$new]"
dotnet test tests/AgentEyes.Tests.Linux/AgentEyes.Tests.Linux.csproj -c Release 2>&1 | grep -E "^\s+Failed AgentEyes|Passed!|Failed!" | sed -E "s/ \[[^]]*\]$//" | sed -E 's/^\s+Failed /  FAILED /' | sort | uniq -c | sed -E 's/^ +1 //'
mv "$file.orig" "$file"
touch "$file"   # a restored file must be newer than the mutated build output

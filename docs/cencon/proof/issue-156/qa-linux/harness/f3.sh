. "$SP/lib.sh"; set +u
run_env $SP/runs/f3; rm -rf $RUNROOT/home/Videos/AgentEyes $RUNROOT/home/.local/share/AgentEyes $RUNROOT/transcript.txt; mkdir -p $LIB
SRC=$SP/runs/v2-merged/home/Videos/AgentEyes/2026-11-16_073950_video   # crashed B, repaired: mux done, owner.json left behind
cp -a $SRC $LIB/2026-11-16_073950_video-samehost; cp -a $SRC $LIB/2026-11-16_073950_video-otherhost
python3 - $LIB/2026-11-16_073950_video-otherhost/capture.owner.json <<'PY'
import json,sys; p=sys.argv[1]; o=json.load(open(p)); o["Host"]="otherhost"; o["HostId"]="00000000000000000000000000000001"; json.dump(o,open(p,"w"),indent=2)
PY
say "copies of repaired recording B (PendingMux cleared, mux done), capture.owner.json left behind by the crash:"
for d in samehost otherhost; do say "  $d: $(state $LIB/2026-11-16_073950_video-$d) Host=$(python3 -c "import json;print(json.load(open('$LIB/2026-11-16_073950_video-$d/capture.owner.json'))['Host'])")"; done
package fix $LIB/2026-11-16_073950_video-samehost same
package fix $LIB/2026-11-16_073950_video-otherhost other

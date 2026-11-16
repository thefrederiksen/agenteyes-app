. "$SP/lib.sh"; set +u
run_env $SP/runs/c6; rm -rf $RUNROOT/home/Videos/AgentEyes $RUNROOT/home/.local/share/AgentEyes $RUNROOT/transcript.txt; mkdir -p $LIB; trap cleanup EXIT
cp -a $SP/runs/v2-fix/home/Videos/AgentEyes/2026-11-16_073058_video $LIB/2026-11-16_073058_video   # finished A of the fix run (owner released cleanly)
F=$LIB/2026-11-16_073058_video
start_serve fix 17750 s; S=$SERVE_PID; MYPIDS+=($S)
api 17750 POST /record/start "$START_BODY" > $RUNROOT/start.json; sleep 10
D=$(python3 -c "import json;print(json.load(open('$RUNROOT/start.json'))['Dir'])"); say "live capture $(basename $D) by pid $S; finished F=$(basename $F) owner.json=$(sz $F/capture.owner.json)"
say "REST rename F while capturing: $(api 17750 POST /recordings/2026-11-16_073058_video/rename '{"name":"qa156 renamed while another records"}' | tr -d '\n' | cut -c1-200)"
say "  F manifest Label/Title now: $(python3 -c "import json;m=json.load(open('$F/manifest.json'));print(m.get('Label'),'|',m.get('Title'))")"
package fix $F Fpkg
say "live capture still: $(api 17750 GET /status | python3 -c 'import json,sys;s=json.load(sys.stdin);print(s.get("State"),s.get("ElapsedSeconds"))'); $(state $D)"
package fix $D Dlive
api 17750 POST /record/stop >/dev/null; wait_mux $D 120; say "$(state $D)"

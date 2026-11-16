. "$SP/lib.sh"; set +u
run_env $SP/runs/sigtest
for sig in INT TERM; do
  start_serve fix 17690 sig$sig; P=$SERVE_PID
  say "pid $P SigIgn=$(grep SigIgn /proc/$P/status | awk '{print $2}') (bit 2 = SIGINT ignored)"
  sleep 2; kill -$sig $P; for i in $(seq 1 20); do kill -0 $P 2>/dev/null || break; sleep 0.5; done
  say "after SIG$sig: alive=$(alive $P)"; kill -9 $P 2>/dev/null; wait $P 2>/dev/null
done

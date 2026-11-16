# QA #161 pass-5 stand-in for "whatever listens on 127.0.0.1:PORT". Written by the QA seat, independent of the
# developer's standin/silent/slow/fullq. Modes:
#   answer DELAY  - HTTP server; GET /status answers {DataRoot,RecordingsRoot} after DELAY seconds; logs each request line
#   silent        - accepts every connection, reads, never answers (a hung service)
#   fullq         - listen(0) and fill the accept queue, never accept: further connects never complete
import socket, sys, threading, time, json
port = int(sys.argv[1]); mode = sys.argv[2]; delay = float(sys.argv[3]) if len(sys.argv) > 3 else 0.0
log = open(sys.argv[4] if len(sys.argv) > 4 else "/dev/null", "a", buffering=1)
BODY = json.dumps({"DataRoot": "/qa-svc/state/AgentEyes", "RecordingsRoot": "/qa-svc/videos/AgentEyes"}).encode()
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(("127.0.0.1", port))
if mode == "fullq":
    s.listen(0); held = []
    for i in range(16):
        c = socket.socket(); c.settimeout(0.3)
        try: c.connect(("127.0.0.1", port)); held.append(c)
        except OSError: held.append(c); break
    print("READY fullq (queue filled after %d connects)" % len(held), flush=True)
    while True: time.sleep(60)
s.listen(64); print("READY " + mode, flush=True)
held = []
def serve(c):
    data = b""
    try:
        while b"\r\n\r\n" not in data:
            d = c.recv(4096)
            if not d: break
            data += d
        log.write("%.3f %s\n" % (time.time(), data.split(b"\r\n")[0].decode("ascii", "replace")))
        if mode == "silent": held.append(c); return
        time.sleep(delay)
        c.sendall(b"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: %d\r\nConnection: close\r\n\r\n" % len(BODY) + BODY)
        c.close()
    except OSError: pass
while True:
    c, _ = s.accept(); threading.Thread(target=serve, args=(c,), daemon=True).start()

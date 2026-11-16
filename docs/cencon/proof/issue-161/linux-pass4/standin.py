# Plays the person's service on the container's own 127.0.0.1:7882: answers GET /status the way a post-#161
# service does (DataRoot / RecordingsRoot), and nothing else.
import http.server, json
BODY = json.dumps({"DataRoot": "/tmp/svch/.local/share/AgentEyes", "RecordingsRoot": "/tmp/svch/Videos/AgentEyes"}).encode()
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != "/status":
            self.send_response(404); self.end_headers(); return
        self.send_response(200); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(BODY))); self.end_headers(); self.wfile.write(BODY)
    def log_message(self, *a): pass
http.server.ThreadingHTTPServer(("127.0.0.1", 7882), H).serve_forever()

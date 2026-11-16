# Listens on the container's own 127.0.0.1:7882 with an accept queue that is full and never drained: every further
# connect is dropped and never completes - the Windows closed-port retry, reproduced on Linux. Only the client's
# CONNECT bound ends it.
import socket, time
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(("127.0.0.1", 7882)); s.listen(0)
held = []
for i in range(8):
    c = socket.socket(); c.settimeout(0.3)
    try: c.connect(("127.0.0.1", 7882))
    except OSError: pass
    held.append(c)
print("queue filled", flush=True)
while True: time.sleep(60)

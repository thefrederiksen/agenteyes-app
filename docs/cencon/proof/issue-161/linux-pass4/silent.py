# Accepts on the container's own 127.0.0.1:7882 and never answers: the shape of a probe that cannot finish
# (Windows' closed-port retry, or a hung service). Only the client's own timeout ends it.
import socket, time
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(("127.0.0.1", 7882)); s.listen(64)
held = []
while True:
    c, _ = s.accept(); held.append(c)

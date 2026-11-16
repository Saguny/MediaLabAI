"""Debug viewer: see what Layable thinks your body is doing, without VR.

    python3 -m layable.viewer          then open http://localhost:8766

Sim mode drives the estimator from sliders. Live mode shows the body
Layable is sending to the MediaLabAI character (layable.puppet broadcasts to
127.0.0.1:39581 over UDP).
"""

import argparse
import json
import math
import random
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlparse

from .config import Config
from .estimator import Estimator
from .vmath import axis_angle, qnorm

LIVE_PORT = 39581  # the VRChat copy of Layable uses 39571
PAGE = Path(__file__).with_name("viewer.html")


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def head_rot(yaw, recline, turn, nod):
    """yaw: heading; recline: 0 upright .. 90 lying face-up;
    turn: head around the neck (+ = left); nod: chin down (+)."""
    r = lambda axis, deg: axis_angle(axis, math.radians(deg))
    q = qmul(r((0, 1, 0), yaw), r((1, 0, 0), recline))
    q = qmul(q, r((0, 1, 0), turn))
    q = qmul(q, r((1, 0, 0), -nod))
    return qnorm(q)


def sim_hands(q, head_pos):
    """Hand sliders are offsets from the head, turned with the heading. A few
    millimetres of wobble, like a held controller (a perfectly still one
    counts as put down)."""
    yaw = math.radians(q.get("yaw", 0.0))
    c, s = math.cos(yaw), math.sin(yaw)
    out = {}
    for side, k in (("left", "l"), ("right", "r")):
        x, y, z = q.get(k + "x", 0.0), q.get(k + "y", 0.0), q.get(k + "z", 0.0)
        wx, wz = c * x + s * z, -s * x + c * z
        j = lambda: random.uniform(-0.004, 0.004)
        out[side] = (head_pos[0] + wx + j(), head_pos[1] + y + j(), head_pos[2] + wz + j())
    return out


def snapshot(est, head_pos, head_q, body, cfg, source, hands=None):
    gates = est.hands if est else {}
    return {
        "hands": {s: {"pos": p, "weight": gates[s].weight if s in gates else 0.0}
                  for s, p in (hands or {}).items() if p is not None},
        "source": source,
        "status": est.status if est else {},
        "head": {"pos": head_pos, "rot": head_q},
        "joints": {k: {"pos": v.pos, "rot": v.rot} for k, v in body.items()},
        "cfg": {"bed_height": cfg["bed_height_cm"] / 100.0, "floor_offset": cfg["floor_offset"],
                "height": cfg["height_cm"] / 100.0, "trackers": cfg["trackers"]},
        "time": time.time(),
    }


class LiveListener(threading.Thread):
    def __init__(self):
        super().__init__(daemon=True)
        self.latest = None
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.bind(("127.0.0.1", LIVE_PORT))

    def run(self):
        while True:
            data, _ = self.sock.recvfrom(65536)
            try:
                self.latest = json.loads(data)
            except ValueError:
                pass


def make_handler(cfg, live):
    est_lock = threading.Lock()
    state = {"est": Estimator(cfg), "last": time.monotonic()}

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def _send(self, code, body, ctype):
            self.send_response(code)
            self.send_header("Content-Type", ctype)
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            url = urlparse(self.path)
            if url.path == "/":
                return self._send(200, PAGE.read_bytes(), "text/html; charset=utf-8")
            if url.path == "/live":
                data = live.latest if live else None
                if data is None or time.time() - data.get("time", 0) > 2:
                    return self._send(200, b'{"source": "none"}', "application/json")
                return self._send(200, json.dumps(data).encode(), "application/json")
            if url.path == "/sim":
                q = {k: float(v[0]) for k, v in parse_qs(url.query).items() if k != "mode"}
                mode = parse_qs(url.query).get("mode", ["auto"])[0]
                with est_lock:
                    cfg.reload()
                    for key in ("height_cm", "bed_height_cm"):
                        if key in q:
                            cfg[key] = q[key]
                    cfg["mode"] = mode
                    if q.get("reset"):
                        state["est"] = Estimator(cfg)
                    est = state["est"]
                    est.cfg = cfg
                    now = time.monotonic()
                    dt = min(max(now - state["last"], 1e-3), 0.1)
                    state["last"] = now
                    pos = (q.get("x", 0.0), q.get("y", 1.63), q.get("z", 0.0))
                    rot = head_rot(q.get("yaw", 0), q.get("recline", 0), q.get("turn", 0), q.get("nod", 0))
                    hands = sim_hands(q, pos) if q.get("hands") else None
                    body = est.update(pos, rot, dt, hands)
                    snap = snapshot(est, pos, rot, body, cfg, "sim", hands)
                return self._send(200, json.dumps(snap).encode(), "application/json")
            self._send(404, b"not found", "text/plain")

    return Handler


def main():
    ap = argparse.ArgumentParser(prog="layable.viewer", description="Layable debug viewer")
    ap.add_argument("--port", type=int, default=8766)
    ap.add_argument("--config", help="config file (default ~/.config/layable-medialab/config.toml)")
    args = ap.parse_args()
    cfg = Config(args.config) if args.config else Config()
    try:
        live = LiveListener()
        live.start()
    except OSError as e:
        print(f"live mode unavailable ({e}); is another viewer running?")
        live = None
    try:
        srv = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(cfg, live))
    except OSError as e:
        raise SystemExit(f"can't use port {args.port} ({e.strerror}); is the viewer already "
                         f"running? Try --port {args.port + 1}")
    print(f"Layable viewer: http://localhost:{args.port}")
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()

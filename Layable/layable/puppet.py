"""Layable as a body solver for the MediaLabAI character.

    python3 -m layable.puppet        (Unity's LayableBody starts it on its own)

The character has no headset. Unity sends her animated head and the posture
she chose (standing, leaning, crouching, sitting on the ground, lying down)
about once a frame; posture.py turns that into the head a real person would
have, and Layable's estimator builds the body for it: planted feet, weight
shifts, steps, sitting and lying. Unity puts that body on her skeleton.

This is a separate copy of Layable with its own settings
(~/.config/layable-medialab/config.toml) and its own ports, so it never gets
in the way of the VRChat one, which talks to WiVRn.

Request (JSON, one per datagram). Layable/OpenXR coordinates: right-handed,
+Y up, -Z forward, metres, in a fixed stage space with the floor at y = 0:

    {"seq": 12, "dt": 0.016,
     "head":  {"p": [x, y, z], "q": [x, y, z, w]},   her animated head (eyes)
     "frame": {"p": [x, 0, z], "q": [x, y, z, w]},   her root: where she stands, -Z = the way she faces
     "eye": 1.38,                                    her standing eye height
     "posture": "Stand",                             Stand Lean Crouch SitGround LieBack LieSide
     "drift": 1.0,                                   how much she sways standing (0..1)
     "cfg": {"height_cm": 150}}                      settings for this frame (over the file)

{"reset": true} starts over with a fresh body (a new session).

Reply: {"seq": 12, "trackers": {"hip": {"p": [...], "q": [...]}, ...},
        "posture": "Crouch", "goal": "SitGround", "busy": true,
        "head": {"p": [...], "q": [...]},            the virtual head Layable used
        "status": {...}}
"""

import argparse
import json
import socket
import time

from .config import DEFAULTS, Config, _check
from .estimator import Estimator, Pose
from .posture import BED, POSTURES, PostureDriver
from .vmath import lerp, nlerp

PORT = 39580
MAX_DT = 0.1
MODE_BLEND_SECONDS = 0.6   # standing body <-> lying body cross-fade


class Puppet:
    def __init__(self, cfg, seed=None):
        self.cfg = cfg
        self.seed = seed
        self.warned = set()
        self.reset()

    def reset(self):
        # One estimator per way of being: switching Layable's mode in place
        # would make the body jump, so both run all the time and the output
        # fades from one to the other.
        self.est = {"standing": Estimator(dict(self.cfg)), "bed": Estimator(dict(self.cfg))}
        self.bed_weight = 0.0
        self.driver = PostureDriver(self.seed)
        self.frames = 0
        self.last = None

    def merged(self, overrides, mode):
        cfg = dict(self.cfg)
        if mode == "bed":  # the ground is her bed, and lying down the body follows the head exactly
            cfg.update({k: v for k, v in BED.items() if k != "mode"})
        for k, v in (overrides or {}).items():
            if k not in DEFAULTS:
                self._warn(k, f"unknown setting '{k}' ignored")
                continue
            problem = _check(k, v)
            if problem:
                self._warn(k, f"{k} = {v!r}: {problem}")
                continue
            cfg[k] = float(v) if isinstance(DEFAULTS[k], float) else v
        cfg["mode"] = mode
        return cfg

    def _warn(self, key, text):
        if key not in self.warned:
            self.warned.add(key)
            print(f"[puppet] {text}")

    def handle(self, msg):
        """One request in, one reply out."""
        if msg.get("reset"):
            self.reset()
        head = msg.get("head")
        if not head:
            return {"seq": msg.get("seq"), "trackers": {}, "status": {"reset": True}}
        real = (tuple(float(c) for c in head["p"]), tuple(float(c) for c in head["q"]))
        fr = msg.get("frame") or {"p": [real[0][0], 0.0, real[0][2]], "q": [0.0, 0.0, 0.0, 1.0]}
        frame = (tuple(float(c) for c in fr["p"]), tuple(float(c) for c in fr["q"]))
        dt = min(max(float(msg.get("dt", 1.0 / 60.0)), 0.0), MAX_DT)
        overrides = msg.get("cfg") or {}
        height = float(overrides.get("height_cm", self.cfg["height_cm"])) / 100.0
        eye = float(msg.get("eye", 0.927 * height))
        posture = msg.get("posture", "Stand")
        if posture not in POSTURES:
            self._warn("posture:" + str(posture), f"unknown posture '{posture}', staying as she is")
        self.driver.set_goal(posture)

        pos, rot, settings = self.driver.update(real, frame, eye, height, dt, float(msg.get("drift", 1.0)))
        bodies = {}
        for mode, est in self.est.items():
            est.cfg = self.merged(overrides, mode)
            bodies[mode] = est.update(pos, rot, dt)

        want = 1.0 if settings.get("mode") == "bed" else 0.0
        step = dt / MODE_BLEND_SECONDS
        self.bed_weight = min(max(self.bed_weight + min(max(want - self.bed_weight, -step), step), 0.0), 1.0)
        w = self.bed_weight * self.bed_weight * (3.0 - 2.0 * self.bed_weight)
        up, bed = bodies["standing"], bodies["bed"]
        if w <= 0.0:
            body = up
        elif w >= 1.0:
            body = bed
        else:
            body = {k: Pose(lerp(up[k].pos, bed[k].pos, w), nlerp(up[k].rot, bed[k].rot, w))
                    for k in up if k in bed}
        status_est = self.est["bed" if w > 0.5 else "standing"]

        self.frames += 1
        self.last = (pos, rot, body, status_est)
        status = {k: v for k, v in status_est.status.items() if isinstance(v, (str, int, float, bool))}
        return {
            "seq": msg.get("seq"),
            "trackers": {k: {"p": list(p.pos), "q": list(p.rot)} for k, p in body.items()},
            "posture": self.driver.at,
            "goal": self.driver.goal,
            "busy": self.driver.busy or 0.0 < self.bed_weight < 1.0,
            "head": {"p": list(pos), "q": list(rot)},
            "status": status,
        }


def serve(port=PORT, config=None):
    cfg = Config(config) if config else Config()
    puppet = Puppet(cfg)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", port))
    sock.settimeout(1.0)
    # Windows reports a reply that reached no one (Unity left Play mode) as a reset on the
    # next recvfrom, which would end the puppet. Turn that off; the catch below is the backstop.
    if hasattr(socket, "SIO_UDP_CONNRESET"):
        sock.ioctl(socket.SIO_UDP_CONNRESET, False)
    viewer = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print(f"[puppet] listening on 127.0.0.1:{port}, settings {cfg.path}")
    checked = time.monotonic()
    last_status, last_posture = 0.0, None
    while True:
        try:
            data, addr = sock.recvfrom(65536)
        except (socket.timeout, ConnectionResetError):
            data = None
        now = time.monotonic()
        if now - checked > 1.0:
            checked = now
            if cfg.reload():
                print("[puppet] settings reloaded")
        if data is None:
            continue
        try:
            reply = puppet.handle(json.loads(data))
        except (ValueError, KeyError, TypeError, IndexError) as e:
            print(f"[puppet] bad request from {addr}: {e}")
            continue
        sock.sendto(json.dumps(reply).encode(), addr)
        if puppet.frames % 3 == 0 and puppet.last:
            send_to_viewer(viewer, puppet)
        if reply.get("goal") != last_posture:
            last_posture = reply.get("goal")
            print(f"[puppet] posture -> {last_posture}")
        if now - last_status > 10.0 and puppet.last:
            last_status = now
            st = puppet.last[3].status
            print(f"[puppet] {reply.get('posture')}: {st.get('lying', '')} (head {st.get('head_y', 0):.2f} m)")


def send_to_viewer(viewer, puppet):
    from .viewer import LIVE_PORT, snapshot
    pos, rot, body, est = puppet.last
    try:
        snap = snapshot(est, pos, rot, body, est.cfg, "live")
        viewer.sendto(json.dumps(snap).encode(), ("127.0.0.1", LIVE_PORT))
    except (OSError, TypeError, ValueError):
        pass


def main():
    ap = argparse.ArgumentParser(prog="layable.puppet", description=__doc__.splitlines()[0])
    ap.add_argument("--port", type=int, default=PORT)
    ap.add_argument("--config", help="config file (default ~/.config/layable-medialab/config.toml)")
    args = ap.parse_args()
    try:
        serve(args.port, args.config)
    except OSError as e:
        raise SystemExit(f"[puppet] can't use port {args.port} ({e.strerror}); is it already running?")
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()

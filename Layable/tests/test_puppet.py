"""Checks that the character's body behaves like a real person's.
Run: python3 tests/test_puppet.py   (add --model to also try the learned model)"""

import json
import math
import socket
import sys
import threading
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from layable.config import CONFIG_PATH, DEFAULTS
from layable import puppet as P
from layable import viewer
from layable.posture import POSTURES
from layable.vmath import axis_angle, rotate

USE_MODEL = "--model" in sys.argv
H = 1.50                       # her height
EYE = 0.927 * H                # her standing eye height
ANKLE = 0.04 * H               # Layable's ankle height above the sole
HZ = 60
CFG = dict(DEFAULTS, use_model=USE_MODEL, tpose_calibration=False, hip_push=False, use_hands=False,
           height_cm=H * 100)
failures = 0


def check(name, cond, detail=""):
    global failures
    print(("  ok   " if cond else "  FAIL ") + name + (f"  ({detail})" if detail else ""))
    if not cond:
        failures += 1


def yaw_q(deg):
    return axis_angle((0.0, 1.0, 0.0), math.radians(deg))


class Sim:
    """Her animated head stands still at the origin; Unity would send this."""

    def __init__(self, seed=1):
        self.p = P.Puppet(dict(CFG), seed=seed)
        self.yaw = 0.0
        self.posture = "Stand"
        self.frames = []
        self.seq = 0

    def step(self):
        q = yaw_q(self.yaw)
        msg = {"seq": self.seq, "dt": 1.0 / HZ, "eye": EYE, "posture": self.posture,
               "head": {"p": [0.0, EYE, 0.0], "q": list(q)},
               "frame": {"p": [0.0, 0.0, 0.0], "q": list(q)},
               "cfg": {"height_cm": H * 100}}
        self.seq += 1
        r = self.p.handle(msg)
        self.frames.append(r)
        return r

    def run(self, seconds):
        start = len(self.frames)
        for _ in range(int(seconds * HZ)):
            self.step()
        return self.frames[start:]

    def until_settled(self, limit=10.0):
        frames = []
        for _ in range(int(limit * HZ)):
            r = self.step()
            frames.append(r)
            if not r["busy"]:
                break
        return frames


def pos(r, k):
    return r["trackers"][k]["p"]


def facing(r, k="hip"):
    f = rotate(tuple(r["trackers"][k]["q"]), (0.0, 0.0, -1.0))
    return math.degrees(math.atan2(-f[0], -f[2]))


def lowest_foot(frames):
    return min(min(pos(r, "left_foot")[1], pos(r, "right_foot")[1]) for r in frames)


def max_speed(frames, k):
    return max(math.dist(pos(a, k), pos(b, k)) * HZ for a, b in zip(frames, frames[1:]))


print(f"standing still for 30 s (learned model {'on' if USE_MODEL else 'off'})")
s = Sim()
s.run(2.0)
frames = s.run(30.0)
feet_y = [pos(r, side)[1] for r in frames for side in ("left_foot", "right_foot")]
lifted = sum(y > ANKLE + 0.01 for y in feet_y) / len(feet_y)
check("feet are on the floor, never under it", min(feet_y) > ANKLE - 0.01, f"lowest ankle {min(feet_y):.3f} m")
check("...and only lift to take a step", lifted < 0.05 and max(feet_y) < ANKLE + 0.10,
      f"lifted {lifted:.1%} of the time, highest {max(feet_y):.3f} m")
for side in ("left_foot", "right_foot"):
    xs = [pos(r, side) for r in frames]
    slide = max(math.dist(xs[0], p) for p in xs)
    check(f"{side.replace('_', ' ')} stays planted", slide < 0.10, f"moved {slide:.3f} m")
hip_x = [pos(r, "hip")[0] for r in frames]
check("weight shifts from foot to foot (hip sways sideways)", max(hip_x) - min(hip_x) > 0.02,
      f"hip x {min(hip_x):+.3f}..{max(hip_x):+.3f} m")
check("...but not like a pendulum", max(hip_x) - min(hip_x) < 0.15)
check("never sits on air", all(r["status"].get("upright") == "standing" for r in frames))
check("hip moves at a human speed", max_speed(frames, "hip") < 0.5, f"{max_speed(frames, 'hip'):.2f} m/s")

print("turning round (the costume change: 180 degrees in 0.45 s, then back)")
s = Sim()
s.run(2.0)
start_feet = [pos(s.frames[-1], k) for k in ("left_foot", "right_foot")]
n = int(0.45 * HZ)
for i in range(n):
    s.yaw = 180.0 * (i + 1) / n
    s.step()
frames = s.run(2.5)
end = frames[-1]
check("body ends up facing the new way", abs(abs(facing(end)) - 180.0) < 25.0, f"hip faces {facing(end):.0f} deg")
moved = [math.dist(a, pos(end, k)) for a, k in zip(start_feet, ("left_foot", "right_foot"))]
check("she steps round (both feet move)", min(moved) > 0.05, f"feet moved {moved[0]:.2f}, {moved[1]:.2f} m")
check("feet on the floor while turning", lowest_foot(s.frames[-(n + len(frames)):]) > ANKLE - 0.01)

print("every posture, there and back")
expect = {
    "Lean": lambda r: pos(r, "hip")[1] > 0.6 * H * 0.53 and r["status"].get("upright") == "standing",
    "Crouch": lambda r: pos(r, "hip")[1] < 0.53 * H - 0.12 and r["status"].get("upright") == "standing",
    "SitGround": lambda r: pos(r, "hip")[1] < 0.2 and r["status"].get("mode") == "bed",
    "LieBack": lambda r: "on back" in r["status"].get("lying", ""),
    "LieSide": lambda r: "side" in r["status"].get("lying", ""),
}
for name in POSTURES[1:]:
    s = Sim()
    s.run(1.0)
    s.posture = name
    there = s.until_settled()
    hold = s.run(3.0)
    r = hold[-1]
    check(f"{name}: gets there", r["posture"] == name and not r["busy"], f"at {r['posture']}")
    check(f"{name}: looks right", expect[name](r),
          f"hip y {pos(r, 'hip')[1]:.2f} m, {r['status'].get('mode')}, {r['status'].get('lying')}")
    check(f"{name}: stays put", max_speed(hold, "hip") < 0.3, f"{max_speed(hold, 'hip'):.2f} m/s")
    s.posture = "Stand"
    back = s.until_settled()
    back += s.run(2.0)
    r = back[-1]
    check(f"{name}: stands up again", r["posture"] == "Stand" and r["status"].get("mode") == "standing"
          and pos(r, "hip")[1] > 0.45 * H, f"hip y {pos(r, 'hip')[1]:.2f} m")
    trip = there + hold + back
    check(f"{name}: no jumps on the way", max_speed(trip, "hip") < 2.0 and max_speed(trip, "left_foot") < 3.0,
          f"hip {max_speed(trip, 'hip'):.2f}, foot {max_speed(trip, 'left_foot'):.2f} m/s")
    check(f"{name}: nothing goes through the floor", lowest_foot(trip) > 0.0 and min(pos(f, 'hip')[1] for f in trip) > 0.05)

print("kept apart from the VRChat copy")
check("own settings file", CONFIG_PATH.parent.name == "layable-medialab", str(CONFIG_PATH))
check("own ports", P.PORT not in (39571, 39572) and viewer.LIVE_PORT != 39571, f"{P.PORT}, {viewer.LIVE_PORT}")

print("over UDP")
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind(("127.0.0.1", 0))
port = sock.getsockname()[1]
sock.close()
cfg_file = Path(__file__).resolve().parent / "_test_config.toml"
cfg_file.write_text("")
threading.Thread(target=P.serve, args=(port, str(cfg_file)), daemon=True).start()
client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
client.settimeout(2.0)
reply = None
for _ in range(20):
    client.sendto(json.dumps({"seq": 7, "dt": 1 / 60, "eye": EYE, "posture": "Stand",
                              "head": {"p": [0, EYE, 0], "q": [0, 0, 0, 1]}}).encode(), ("127.0.0.1", port))
    try:
        reply = json.loads(client.recvfrom(65536)[0])
        break
    except (socket.timeout, ConnectionRefusedError):
        continue
cfg_file.unlink(missing_ok=True)
check("answers with her body", reply is not None and reply["seq"] == 7 and "hip" in reply["trackers"])

print("all ok" if not failures else f"{failures} FAILED")
sys.exit(1 if failures else 0)

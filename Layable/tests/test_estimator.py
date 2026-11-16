"""Sanity checks for the pose guesses. Run: python3 tests/test_estimator.py"""

import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from layable.config import DEFAULTS
# these checks are about the rules (the model has its own), with the VRChat copy's defaults
DEFAULTS = dict(DEFAULTS, use_model=False, use_hands=True, hip_push=True, tpose_calibration=True, recenter_seconds=0.0)
from layable.estimator import Estimator, mix
from layable.vmath import basis_quat, rotate, rotate_about, norm

BED = 0.55
failures = 0


def check(name, cond, detail=""):
    global failures
    print(("  ok   " if cond else "  FAIL ") + name + (f"  ({detail})" if detail else ""))
    if not cond:
        failures += 1


def run(est, pos, fwd, up, seconds=1.0, hz=90, hands=None, jitter=0.0):
    rot = basis_quat(norm(fwd), norm(up))
    out = None
    for i in range(int(seconds * hz)):
        h = hands
        if hands and jitter:  # held controllers are never perfectly still
            wob = jitter * math.sin(i * 0.7)
            h = {s: (p[0] + wob, p[1], p[2] - wob) if p else None for s, p in hands.items()}
        out = est.update(pos, rot, 1.0 / hz, h)
    return out


def yaw_deg(q):
    f = rotate(q, (0, 0, -1))
    return math.degrees(math.atan2(-f[0], -f[2]))  # + = turned left (towards -X)


def fmt(p):
    return "(" + " ".join(f"{c:+.2f}" for c in p) + ")"


def show(out, est):
    print("   ", est.status["mode"], "|", est.status["lying"])
    for k in ("hip", "left_knee", "right_knee", "left_foot", "right_foot"):
        print(f"      {k:11s} {fmt(out[k].pos)}")


def fresh():
    cfg = dict(DEFAULTS)
    cfg["bed_height_cm"] = BED * 100
    cfg["height_cm"] = 175
    return Estimator(cfg)


print("standing, looking ahead")
e = fresh()
o = run(e, (0, 1.63, 0), (0, 0, -1), (0, 1, 0))
show(o, e)
check("mode standing", e.status["mode"] == "standing")
check("feet near floor", all(o[f].pos[1] < 0.12 for f in ("left_foot", "right_foot")))
check("hip about 0.9-1.05 m", 0.9 < o["hip"].pos[1] < 1.05, f"{o['hip'].pos[1]:.2f}")
check("hip tracker upright", rotate(o["hip"].rot, (0, 1, 0))[1] > 0.95)

print("lying on back, looking at the ceiling (head towards +Z)")
e = fresh()
o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1))
show(o, e)
check("mode bed", e.status["mode"] == "bed")
check("says on back", "on back" in e.status["lying"], e.status["lying"])
check("hip on the mattress", abs(o["hip"].pos[1] - (BED + 0.10)) < 0.03, f"{o['hip'].pos[1]:.2f}")
check("feet towards -Z", o["left_foot"].pos[2] < -1.2 and o["right_foot"].pos[2] < -1.2)
check("feet on the bed", all(BED < o[f].pos[1] < BED + 0.2 for f in ("left_foot", "right_foot")))
check("legs side by side", abs(o["left_foot"].pos[0] - o["right_foot"].pos[0]) > 0.12)
check("hip tracker front faces up", rotate(o["hip"].rot, (0, 0, -1))[1] > 0.9)

print("  ...turning head 40 deg to the right (should NOT roll the body)")
fwd = rotate_about((0, 1, 0), (0, 0, 1), math.radians(-40))
o = run(e, (0, BED + 0.2, 0), fwd, (0, 0, 1))
check("still on back", "on back" in e.status["lying"], e.status["lying"])
check("hips nearly level", abs(o["left_knee"].pos[1] - o["right_knee"].pos[1]) < 0.06, f"{abs(o['left_knee'].pos[1] - o['right_knee'].pos[1]):.3f}")

print("  ...turning the face fully right = rolling onto the right side")
for i in range(1, 136):
    a = math.radians(-40 - 50 * i / 135)  # head keeps turning to 90 deg
    fwd = rotate_about((0, 1, 0), (0, 0, 1), a)
    o = run(e, (0, BED + 0.2 + 0.05 * i / 135, 0), fwd, (0, 0, 1), seconds=1 / 90)
o = run(e, (0, BED + 0.25, 0), fwd, (0, 0, 1))
show(o, e)
check("now on right side", "on right side" in e.status["lying"], e.status["lying"])
check("legs stacked (one knee above the other)", abs(o["left_knee"].pos[1] - o["right_knee"].pos[1]) > 0.1)
check("hip higher on side", o["hip"].pos[1] > BED + 0.13, f"{o['hip'].pos[1]:.2f}")

print("lying on the left side, face towards -X")
e = fresh()
o = run(e, (0, BED + 0.25, 0), (-1, 0, 0), (0, 0, 1))
show(o, e)
check("on left side", "on left side" in e.status["lying"], e.status["lying"])
check("left leg underneath", o["left_knee"].pos[1] < o["right_knee"].pos[1])
check("knees drawn forward (-X)", o["left_knee"].pos[0] < -0.1, f"{o['left_knee'].pos[0]:.2f}")
check("nothing below the mattress", all(o[k].pos[1] >= BED for k in o))

print("sitting up in bed against the headboard, looking ahead (-Z)")
e = fresh()
o = run(e, (0, BED + 0.72, 0), (0, 0, -1), (0, 1, 0))
show(o, e)
check("mode bed", e.status["mode"] == "bed")
check("says sitting up", "sitting up" in e.status["lying"], e.status["lying"])
check("legs stretched forward", o["left_foot"].pos[2] < -0.7, f"{o['left_foot'].pos[2]:.2f}")
check("hip on the mattress", abs(o["hip"].pos[1] - (BED + 0.10)) < 0.08, f"{o['hip'].pos[1]:.2f}")

print("getting up: bed -> standing")
o = run(e, (0, 1.63, 0), (0, 0, -1), (0, 1, 0))
check("switches to standing", e.status["mode"] == "standing")

print("bed_height_cm setting moves everything")
e = fresh()
e.cfg["bed_height_cm"] = 40
o = run(e, (0, 0.40 + 0.2, 0), (0, 1, 0), (0, 0, 1))
check("hip follows lower bed", abs(o["hip"].pos[1] - 0.50) < 0.03, f"{o['hip'].pos[1]:.2f}")

print("tossing and turning: back -> right -> stomach -> left -> back")
e = fresh()
seen = []
for deg in list(range(0, 181, 2)) + list(range(180, 360, 2)) + [360]:
    fwd = rotate_about((0, 1, 0), (0, 0, 1), math.radians(-deg))
    run(e, (0, BED + 0.22, 0), fwd, (0, 0, 1), seconds=0.05)
    st = e.status["lying"].split(",")[0]
    if not seen or seen[-1] != st:
        seen.append(st)
o = run(e, (0, BED + 0.22, 0), (0, 1, 0), (0, 0, 1))
print("    states:", " -> ".join(seen))
check("went through every roll state in order",
      seen == ["on back", "on right side", "on stomach", "on left side", "on back"], str(seen))

print("nodding / tilting the head while lying doesn't swing the legs")
from layable.viewer import head_rot
for preset, (y, turn) in {"back": (0.20, 0), "left side": (0.25, 90), "right side": (0.25, -90)}.items():
    e = fresh()
    o = None
    for _ in range(90):
        o = e.update((0, BED + y, 0), head_rot(0, 90, turn, 0), 1 / 90)
    start = o["left_foot"].pos
    worst = 0.0
    for nod in list(range(0, 31)) + list(range(30, -31, -1)) + list(range(-30, 1)):
        for _ in range(3):
            o = e.update((0, BED + y, 0), head_rot(0, 90, turn, nod), 1 / 90)
        worst = max(worst, math.dist(o["left_foot"].pos, start))
    check(f"{preset}: feet move < 0.35 m while nodding +-30 deg", worst < 0.35, f"{worst:.2f} m")

print("bad config values are rejected, not crashed on")
import tempfile, os
from layable.config import Config
with tempfile.TemporaryDirectory() as td:
    path = os.path.join(td, "c.toml")
    open(path, "w").write('bed_height_cm = 25\nheight_cm = 196\n')
    c = Config(path)
    check("good values load", c["bed_height_cm"] == 25 and c["height_cm"] == 196)
    open(path, "w").write('bed_height_cm = 0.5\nmode = "sleepy"\nheight_cm = 1.96\n')
    os.utime(path, (1, 1))
    c.reload()
    check("bad values keep the previous ones",
          c["bed_height_cm"] == 25 and c["mode"] == "auto" and c["height_cm"] == 196,
          f"{c['bed_height_cm']!r} {c['mode']!r} {c['height_cm']!r}")
    e = Estimator(c)
    e.update((0, 1.0, 0), (0, 0, 0, 1), 1 / 90)
    check("estimator runs with the reloaded config", True)

print("196 cm tall: standing and lying on a 25 cm bed")
cfg = dict(DEFAULTS)
cfg.update(height_cm=196, bed_height_cm=25)
e = Estimator(cfg)
o = run(e, (0, 0.93 * 1.96, 0), (0, 0, -1), (0, 1, 0))
check("standing: feet on the floor", all(o[f].pos[1] < 0.13 for f in ("left_foot", "right_foot")),
      f"{o['left_foot'].pos[1]:.2f}")
check("standing: hip around 1.0-1.15 m", 1.0 < o["hip"].pos[1] < 1.15, f"{o['hip'].pos[1]:.2f}")
o = run(e, (0, 0.25 + 0.21, 0), (0, 1, 0), (0, 0, 1))
check("lying: in bed mode, on back", e.status["mode"] == "bed" and "on back" in e.status["lying"], e.status["lying"])
check("lying: hip on the 25 cm mattress", abs(o["hip"].pos[1] - 0.35) < 0.03, f"{o['hip'].pos[1]:.2f}")
reach = -o["left_foot"].pos[2]
check("lying: feet ~1.6-1.8 m from the head", 1.55 < reach < 1.85, f"{reach:.2f} m")

# ------------------------------------------------------------------ hands
print("HANDS: on back, face turned 70 deg right, hands resting at your sides")
at_sides = {"left": (-0.32, BED + 0.08, -0.45), "right": (0.32, BED + 0.08, -0.45)}
for label, hands in (("no hands", None), ("hands at sides", at_sides)):
    e = fresh()
    run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), hands=hands, jitter=0.004)
    for i in range(1, 91):
        fwd = rotate_about((0, 1, 0), (0, 0, 1), math.radians(-70 * i / 90))
        run(e, (0, BED + 0.2, 0), fwd, (0, 0, 1), seconds=1 / 90, hands=hands)
    run(e, (0, BED + 0.2, 0), fwd, (0, 0, 1), hands=hands, jitter=0.004)
    print(f"    {label}: {e.status['lying']}")
    if hands:
        check("hands at your sides keep you on your back", "on back" in e.status["lying"], e.status["lying"])
    else:
        check("(head only: 70 deg rolls you, as before)", "on right side" in e.status["lying"], e.status["lying"])

print("HANDS: lying still for 40 s with hands resting at your sides, face turned 70 deg")
e = fresh()
fwd70 = rotate_about((0, 1, 0), (0, 0, 1), math.radians(-70))
run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), hands=at_sides, jitter=0.004)
run(e, (0, BED + 0.2, 0), fwd70, (0, 0, 1), seconds=2, hands=at_sides, jitter=0.004)
run(e, (0, BED + 0.2, 0), fwd70, (0, 0, 1), seconds=40, hands=at_sides, jitter=0.003)
check("still on back after 40 s (still hands keep counting)", "on back" in e.status["lying"], e.status["lying"])

print("HANDS: really on the right side, face only 45 deg off the ceiling, hands in front")
in_front = {"left": (0.30, BED + 0.28, -0.30), "right": (0.36, BED + 0.10, -0.22)}
fwd = rotate_about((0, 1, 0), (0, 0, 1), math.radians(-45))
for label, hands in (("no hands", None), ("hands in front", in_front)):
    e = fresh()
    o = run(e, (0, BED + 0.25, 0), fwd, (0, 0, 1), seconds=1.5, hands=hands, jitter=0.004)
    print(f"    {label}: {e.status['lying']}")
    if hands:
        check("hands in front roll you onto the right side", "on right side" in e.status["lying"], e.status["lying"])
        check("nothing below the mattress", all(o[k].pos[1] >= BED - 0.01 for k in o))
    else:
        check("(head only: 45 deg stays on back, as before)", "on back" in e.status["lying"], e.status["lying"])

print("HANDS: standing, head straight, hands held out 40 deg to the left")
def hands_towards(deg, neck=(0, 1.43, 0), dist=0.42, drop=0.30, apart=0.32):
    f = rotate_about((0, 0, -1), (0, 1, 0), math.radians(deg))
    r = (-f[2], 0, f[0])
    c = (neck[0] + f[0] * dist, neck[1] - drop, neck[2] + f[2] * dist)
    return {"left": (c[0] - r[0] * apart / 2, c[1], c[2] - r[2] * apart / 2),
            "right": (c[0] + r[0] * apart / 2, c[1], c[2] + r[2] * apart / 2)}
e = fresh()
o = run(e, (0, 1.63, 0), (0, 0, -1), (0, 1, 0), hands=hands_towards(40), jitter=0.004)
hip_yaw, chest_yaw = yaw_deg(o["hip"].rot), yaw_deg(o["chest"].rot)
check("hips turn towards the hands (> 20 deg)", hip_yaw > 20, f"{hip_yaw:.0f} deg")
check("chest turns at least as far as the hips", chest_yaw >= hip_yaw - 2, f"chest {chest_yaw:.0f} deg")
e2 = fresh()
o2 = run(e2, (0, 1.63, 0), (0, 0, -1), (0, 1, 0))
check("(head only: hips stay straight)", abs(yaw_deg(o2["hip"].rot)) < 3, f"{yaw_deg(o2['hip'].rot):.0f} deg")

print("HANDS: standing, looking 60 deg left at something, hands still in front")
e = fresh()
run(e, (0, 1.63, 0), (0, 0, -1), (0, 1, 0), hands=hands_towards(0), jitter=0.004)
look = rotate_about((0, 0, -1), (0, 1, 0), math.radians(60))
o = run(e, (0, 1.63, 0), look, (0, 1, 0), hands=hands_towards(0), jitter=0.004)
check("hips stay with the hands, not the head", abs(yaw_deg(o["hip"].rot)) < 8, f"{yaw_deg(o['hip'].rot):.0f} deg")

print("HANDS: controllers out of reach (put on a shelf) are ignored")
far = {"left": (1.5, 1.0, -1.5), "right": (1.7, 1.0, -1.5)}
e, e2 = fresh(), fresh()
o = run(e, (0, 1.63, 0), (0, 0, -1), (0, 1, 0), hands=far)
o2 = run(e2, (0, 1.63, 0), (0, 0, -1), (0, 1, 0))
check("same as no hands", all(math.dist(o[k].pos, o2[k].pos) < 1e-6 for k in o2), e.status["hands"])

print("HANDS: a controller dropped on the bed stops counting once you move")
e = fresh()
dropped = {"left": (0.30, BED + 0.28, -0.30), "right": None}
run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), hands=dropped, jitter=0.004)
check("held: counts", e.hands["left"].weight > 0.9, e.status["hands"])
for i in range(90):  # head moves 40 cm while the controller lies still
    run(e, (0, BED + 0.2 + 0.4 * i / 90, 0), (0, 1, 0), (0, 0, 1), seconds=1 / 90, hands=dropped)
run(e, (0, BED + 0.6, 0), (0, 1, 0), (0, 0, 1), seconds=0.5, hands=dropped)
check("lying still: ignored", e.hands["left"].weight == 0.0, e.status["hands"])

print("SITTING on a chair (seat 45 cm), head upright")
cfg = dict(DEFAULTS)
cfg.update(height_cm=175, bed_height_cm=25)
e = Estimator(cfg)
T = 0.29 * 1.75
seat_neck = 0.45 + 0.05 + T
o = run(e, (0, seat_neck + 0.11 * 1.75, 0.05), (0, 0, -1), (0, 1, 0), seconds=1.5)
show(o, e)
check("mode sitting", e.status["mode"] == "sitting", e.status["mode"] + " / " + e.status["lying"])
check("hip at seat height", abs(o["hip"].pos[1] - 0.50) < 0.06, f"{o['hip'].pos[1]:.2f}")
check("knees forward of the hips", all(o[k].pos[2] < o["hip"].pos[2] - 0.3 for k in ("left_knee", "right_knee")),
      f"{o['left_knee'].pos[2] - o['hip'].pos[2]:.2f}")
check("thighs roughly level", abs(o["left_knee"].pos[1] - o["hip"].pos[1]) < 0.15,
      f"{o['left_knee'].pos[1] - o['hip'].pos[1]:+.2f}")
check("feet on the floor", all(o[f].pos[1] < 0.12 for f in ("left_foot", "right_foot")),
      f"{o['left_foot'].pos[1]:.2f}")

print("SITTING DOWN from standing, with a high (55 cm) bed: a chair, not the bed")
cfg = dict(DEFAULTS)
cfg.update(height_cm=196, bed_height_cm=55)
e = Estimator(cfg)
run(e, (0, 0.93 * 1.96, 0), (0, 0, -1), (0, 1, 0))
for i in range(90):  # sit down over a second
    y = 0.93 * 1.96 - (0.93 * 1.96 - (0.47 + 0.40 * 1.96)) * i / 89
    run(e, (0, y, 0.1 * i / 89), (0, 0, -1), (0, 1, 0), seconds=1 / 90)
o = run(e, (0, 0.47 + 0.40 * 1.96, 0.1), (0, 0, -1), (0, 1, 0))
check("sat down on a chair: sitting, feet on the floor",
      e.status["mode"] == "sitting" and o["left_foot"].pos[1] < 0.15,
      f"{e.status['mode']} / {e.status['lying']}, foot y {o['left_foot'].pos[1]:.2f}")
for i in range(90):  # ... then lie back on the bed
    k = i / 89
    up = norm((0, 1 - k, k))
    fwd = norm((0, k, -(1 - k)))
    run(e, (0, mix(0.47 + 0.40 * 1.96, 0.55 + 0.21, k), 0.1 + 0.6 * k), fwd, up, seconds=1 / 90)
run(e, (0, 0.55 + 0.21, 0.7), (0, 1, 0), (0, 0, 1))
check("then lying back: in bed, on back", e.status["mode"] == "bed" and "on back" in e.status["lying"],
      e.status["lying"])

print("BENDING OVER to look at the floor: hips go back, feet stay put")
e = fresh()
o = run(e, (0, 1.63, 0), (0, 0, -1), (0, 1, 0))
feet0 = [o[f].pos for f in ("left_foot", "right_foot")]
down = norm((0, -1, -0.35))
o = run(e, (0, 1.30, -0.30), down, norm((0, 0.35, -1)), seconds=1.0)
show(o, e)
check("says bending over", "bending" in e.status["lying"], e.status["lying"])
check("hips behind the head", o["hip"].pos[2] > -0.15, f"{o['hip'].pos[2]:.2f}")
check("hips stay high (not a squat)", o["hip"].pos[1] > 0.8, f"{o['hip'].pos[1]:.2f}")
moved = max(math.dist(a, o[f].pos) for a, f in zip(feet0, ("left_foot", "right_foot")))
check("feet stayed planted", moved < 0.05, f"{moved:.2f} m")

print("STANDING: swaying 10 cm doesn't move the feet, walking 1 m does")
e = fresh()
o = run(e, (0, 1.63, 0), (0, 0, -1), (0, 1, 0))
feet0 = [o[f].pos for f in ("left_foot", "right_foot")]
worst = 0.0
for i in range(180):
    x = 0.10 * math.sin(i / 180 * 2 * math.pi)
    o = run(e, (x, 1.63, 0), (0, 0, -1), (0, 1, 0), seconds=1 / 90)
    worst = max(worst, max(math.dist(a, o[f].pos) for a, f in zip(feet0, ("left_foot", "right_foot"))))
check("feet planted while swaying", worst < 0.03, f"{worst:.3f} m")
for i in range(90):
    o = run(e, (0, 1.63, -1.0 * i / 90), (0, 0, -1), (0, 1, 0), seconds=1 / 90)
o = run(e, (0, 1.63, -1.0), (0, 0, -1), (0, 1, 0), seconds=1.5)
check("feet followed after walking", all(o[f].pos[2] < -0.85 for f in ("left_foot", "right_foot")),
      f"{o['left_foot'].pos[2]:.2f} {o['right_foot'].pos[2]:.2f}")

print("CALIBRATION: lying in bed, T-pose (arms spread on the mattress) -> straight standing body")
e = fresh()
tpose_bed = {"left": (-0.72, BED + 0.12, -0.15), "right": (0.72, BED + 0.12, -0.15)}
run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), hands=at_sides, jitter=0.004)
check("before: on back", "on back" in e.status["lying"], e.status["lying"])
o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), seconds=1.0, hands=tpose_bed, jitter=0.004)
check("T-pose in bed: calibration pose", e.status["source"] == "calibration", e.status["lying"])
up_legs = all(abs(o[k].pos[0] - o[k.replace("knee", "foot")].pos[0]) < 0.02 and
              abs(o[k].pos[2] - o[k.replace("knee", "foot")].pos[2]) < 0.02 for k in ("left_knee", "right_knee"))
check("legs hang straight down", up_legs)
check("hip straight below the head", abs(o["hip"].pos[0]) < 0.05 and o["hip"].pos[1] < BED + 0.2 - 0.5,
      f"{o['hip'].pos}")
check("hip tracker upright", rotate(o["hip"].rot, (0, 1, 0))[1] > 0.95)
o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), seconds=2.0, hands=at_sides, jitter=0.004)
check("arms down again: back on your back", e.status["source"] != "calibration" and "on back" in e.status["lying"],
      e.status["lying"])

print("CALIBRATION: standing T-pose, and the manual command")
e = fresh()
H = 1.75
o = run(e, (0, 1.63, 0), (0, 0, -1), (0, 1, 0), seconds=1.0, jitter=0.004,
        hands={"left": (-0.80, 1.40, -0.05), "right": (0.80, 1.40, -0.05)})
check("standing T-pose: calibration pose", e.status["source"] == "calibration", e.status["lying"])
check("feet on the floor", all(o[f].pos[1] < 0.15 for f in ("left_foot", "right_foot")), f"{o['left_foot'].pos[1]:.2f}")
e = fresh()
run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1))
e.calibrate_for(2.0)
run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), seconds=1.0)
check("calibrate_for: pose without hands", e.status["source"] == "calibration")
run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), seconds=1.5)
check("... and it ends by itself", e.status["source"] != "calibration", e.status["lying"])

print("CALIBRATION: normal arm positions never trigger it")
e = fresh()
triggered = False
for hands in (at_sides, in_front, {"left": (-0.45, BED + 0.1, -0.5), "right": (0.45, BED + 0.1, -0.5)},
              {"left": (-0.3, BED + 0.5, 0.2), "right": (0.3, BED + 0.5, 0.2)}):
    run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), seconds=1.0, hands=hands, jitter=0.004)
    triggered |= e.status["source"] == "calibration"
e2 = fresh()
for hands in (hands_towards(0), hands_towards(0, apart=0.9), {"left": (-0.3, 0.85, 0), "right": (0.3, 0.85, 0)}):
    run(e2, (0, 1.63, 0), (0, 0, -1), (0, 1, 0), seconds=1.0, hands=hands, jitter=0.004)
    triggered |= e2.status["source"] == "calibration"
check("no false T-pose (sides, in front, wide on the bed, behind the head, held apart)", not triggered)

print("HIP PUSH: a hand coming in from the side shoves the hip, instantly")
e = fresh()
o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), hands=at_sides, jitter=0.003)
hip0, foot0 = o["hip"].pos, o["left_foot"].pos
start_h = (hip0[0] + 0.28, hip0[1] + 0.02, hip0[2])   # beside the hip, on your right (lying on back: +X)
for i in range(30):  # slide in to 5 cm from the centre over a third of a second
    x = 0.28 - 0.23 * (i + 1) / 30
    o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), seconds=1 / 90,
            hands={"left": at_sides["left"], "right": (hip0[0] + x, start_h[1], start_h[2])})
check("pushing while the hand is in", e.status.get("hip") == "pushing", e.status.get("hip"))
check("hip already moving left with no hold", e.hip_offset < -0.08, f"{e.hip_offset * 100:+.1f} cm")
for i in range(20):  # pull the hand back out
    o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), seconds=1 / 90,
            hands={"left": at_sides["left"], "right": (hip0[0] + 0.05 + 0.3 * (i + 1) / 20, start_h[1], start_h[2])})
o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), seconds=1.0, hands=at_sides, jitter=0.003)
check("hip stays ~10 cm left after the hand leaves", abs((o["hip"].pos[0] - hip0[0]) + 0.10) < 0.025,
      f"{(o['hip'].pos[0] - hip0[0]) * 100:+.1f} cm")
def bend(o):  # angle between neck->hip and hip->between the feet, seen from above
    n, h = o["neck"].pos, o["hip"].pos
    f = [(a + b) / 2 for a, b in zip(o["left_foot"].pos, o["right_foot"].pos)]
    u, v = (h[0] - n[0], h[2] - n[2]), (f[0] - h[0], f[2] - h[2])
    return abs(math.degrees(math.atan2(u[0] * v[1] - u[1] * v[0], u[0] * v[0] + u[1] * v[1])))
check("body still straight (neck, hip, feet in a line)", bend(o) < 3, f"{bend(o):.1f} deg bend")
check("feet swung the same way", o["left_foot"].pos[0] - foot0[0] < -0.10,
      f"{(o['left_foot'].pos[0] - foot0[0]) * 100:+.1f} cm")
check("log line: shifted", e.status.get("hip", "").startswith("shifted"), e.status.get("hip"))
e.reset_hip()
o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), seconds=1.0, hands=at_sides, jitter=0.003)
check("reset puts it back", abs(o["hip"].pos[0] - hip0[0]) < 0.01, f"{(o['hip'].pos[0] - hip0[0]) * 100:+.1f} cm")

print("HIP PUSH: hands coming down onto your belly, resting at your sides, or breathing never push")
e = fresh()
o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), hands=at_sides, jitter=0.003)
hip0 = o["hip"].pos
for i in range(45):  # both hands come down from above onto the belly
    k = (i + 1) / 45
    o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1), seconds=1 / 90,
            hands={"left": (hip0[0] - 0.04, hip0[1] + 0.45 - 0.33 * k, hip0[2] + 0.03),
                   "right": (hip0[0] + 0.04, hip0[1] + 0.45 - 0.33 * k, hip0[2])})
belly = {"left": (hip0[0] - 0.04, hip0[1] + 0.12, hip0[2] + 0.03), "right": (hip0[0] + 0.04, hip0[1] + 0.12, hip0[2])}
for hands in (belly, at_sides):
    for i in range(int(3 * 90)):
        wob = 0.01 * math.sin(i * 0.05)  # breathing
        o = e.update((0, BED + 0.2, 0), basis_quat((0, 1, 0), (0, 0, 1)), 1 / 90,
                     {s: (p[0] + wob, p[1] + wob, p[2]) for s, p in hands.items()})
check("no push, no shift", abs(e.hip_offset) < 1e-6, f"{e.status.get('hip')} {e.hip_offset:+.3f}")

def ramp(a, b, n):
    return [tuple(a[i] + (b[i] - a[i]) * k / (n - 1) for i in range(3)) for k in range(n)]


def walk(est, path, hz=90):
    o = None
    for p in path:
        o = est.update(p, basis_quat((0, 0, -1), (0, 1, 0)), 1.0 / hz)
    return o


def upright(**kw):
    cfg = dict(DEFAULTS, height_cm=175, bed_height_cm=55)
    cfg.update(kw)
    return Estimator(cfg)


STAND = (0, 1.63, 0)
print("BALANCE: leaning the head 12 cm forward over planted feet pushes the hips back")
res = {}
for cb in (0.0, 1.3):
    e = upright(hip_counterbalance=cb, hip_sway=0.0, experimental_lean=False)
    walk(e, [STAND] * 180)
    hip0, foot0 = e.smoothed["hip"].pos, e.smoothed["left_foot"].pos
    o = walk(e, ramp(STAND, (0, 1.63, -0.12), 27) + [(0, 1.63, -0.12)] * 27)
    res[cb] = (o["hip"].pos[2] - hip0[2], math.dist(o["left_foot"].pos, foot0), o)
check("counterbalance 0: hips follow the head", abs(res[0.0][0] + 0.12) < 0.01, f"{res[0.0][0]:+.3f} m")
check("counterbalance 1.3: hips move clearly less", -0.10 < res[1.3][0] < -0.05, f"{res[1.3][0]:+.3f} m")
check("feet stay planted", res[1.3][1] < 0.01, f"{res[1.3][1]:.3f} m")
o = res[1.3][2]
check("torso keeps its length", abs(math.dist(o["neck"].pos, o["hip"].pos) - 0.29 * 1.75) < 0.01,
      f"{math.dist(o['neck'].pos, o['hip'].pos):.3f} m")
legs_ok = all(abs(math.dist(o[s + "_hipjoint"].pos, o[s + "_knee"].pos) - 0.245 * 1.75) < 0.01 for s in ("left", "right"))
check("knees re-solved (thigh length kept)", legs_ok)
check("knee tracker follows the thigh", all(
    rotate(o[s + "_knee"].rot, (0, 1, 0))[1] > 0.95 and
    abs(math.degrees(math.acos(max(-1, min(1, sum(a * b for a, b in zip(rotate(o[s + "_knee"].rot, (0, 1, 0)),
        norm(tuple(h - k for h, k in zip(o[s + "_hipjoint"].pos, o[s + "_knee"].pos)))))))))) < 2
    for s in ("left", "right")))

e = upright(hip_sway=0.0, experimental_lean=False)
walk(e, [STAND] * 180)
hip0 = e.smoothed["hip"].pos
o = walk(e, ramp(STAND, (0, 1.63, 0.08), 27) + [(0, 1.63, 0.08)] * 27)
check("leaning back: hips move back less than the head", 0.02 < o["hip"].pos[2] - hip0[2] < 0.07,
      f"{o['hip'].pos[2] - hip0[2]:+.3f} m")


def sweep_jump(**kw):
    """Largest frame-to-frame jump of the raw hip while the head sways +-3 cm fore/aft over planted feet."""
    e = upright(**kw)
    walk(e, [STAND] * 180)
    worst, prev = 0.0, None
    for i in range(360):
        p = (0, 1.63, 0.03 * math.sin(i / 90 * 2 * math.pi))
        o = e.update(p, basis_quat((0, 0, -1), (0, 1, 0)), 1 / 90)
        z = e.smoothed["hip"].pos[2]
        if prev is not None:
            worst = max(worst, abs(z - prev))
        prev = z
    return worst
jump = sweep_jump()
check("swaying across the feet line: no hip jumps", jump < 0.004, f"worst {jump * 1000:.1f} mm/frame")

e, e2 = upright(hip_counterbalance=0.0, hip_sway=0.0, experimental_lean=False), upright()
e2._balance = lambda out, d: None
path = [STAND] * 90 + ramp(STAND, (0.06, 1.63, 0.05), 27) + [(0.06, 1.63, 0.05)] * 27
o, o2 = walk(e, path), walk(e2, path)
check("counterbalance, sway and lean all off: output untouched", all(math.dist(o[k].pos, o2[k].pos) < 1e-9 for k in o2))

print("BALANCE: weight on the right foot pops the hip out over it (sway)")
res = {}
for sway in (0.0, 0.5):
    e = upright(hip_sway=sway)
    walk(e, [STAND] * 180)
    hip0 = e.smoothed["hip"].pos
    o = walk(e, ramp(STAND, (0.08, 1.63, 0), 27) + [(0.08, 1.63, 0)] * 27)
    res[sway] = (o["hip"].pos[0] - hip0[0], o["right_hipjoint"].pos[1] - o["left_hipjoint"].pos[1])
check("sway: the hip moves further than the head", res[0.5][0] > 0.09, f"{res[0.5][0]:+.3f} m")
check("sway adds to the plain shift", res[0.5][0] - res[0.0][0] > 0.025, f"{res[0.5][0] - res[0.0][0]:+.3f} m")
check("pelvis tilts up on the loaded (right) side", res[0.5][1] > 0.005, f"{res[0.5][1]:+.3f} m")
check("(no sway: pelvis level)", abs(res[0.0][1]) < 1e-3, f"{res[0.0][1]:+.4f} m")

print("BALANCE: a wide stance lets the torso lean (experimental_lean)")
res = {}
for lean in (False, True):
    e = upright(hip_sway=0.0, experimental_lean=lean)
    walk(e, [STAND] * 90)
    for s, x in (("left", -0.20), ("right", 0.20)):  # feet planted wide apart
        f = e.feet[s]
        f["pos"] = f["from"] = (x, f["pos"][1], f["pos"][2])
    o = walk(e, [(0.06, 1.63, 0)] * 30)
    check(f"(lean {lean}: wide feet stayed planted)", abs(o["left_foot"].pos[0] + 0.20) < 0.01, f"{o['left_foot'].pos[0]:+.2f}")
    res[lean] = o["hip"].pos[0] - o["neck"].pos[0]
check("wide stance: hip stays nearer the middle than the head", res[True] < res[False] - 0.01,
      f"hip-neck {res[True]:+.3f} vs {res[False]:+.3f} m")

print("BALANCE: never in bed, faded out seated")
e = fresh()
o = run(e, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1))
e2 = fresh()
e2.cfg.update(hip_counterbalance=0.0, hip_sway=0.0, experimental_lean=False)
o2 = run(e2, (0, BED + 0.2, 0), (0, 1, 0), (0, 0, 1))
check("bed output unchanged by balance settings", all(math.dist(o[k].pos, o2[k].pos) < 1e-9 for k in o2))

print("CROUCH: crouching pitches the torso forward, hips go back")
res = {}
for deg in (0.0, 25.0):
    e = upright(crouch_lean_deg=deg)
    walk(e, [STAND] * 90)
    o = walk(e, [(0, 1.30 + 0.03 * math.sin(i * 0.3), 0) for i in range(90)])  # wobbly: not a seat
    res[deg] = (o["hip"].pos[2] - o["neck"].pos[2], e.status["lying"], e.seated)
check("crouch_lean_deg 0: hips under the neck", res[0.0][0] < 0.05, f"{res[0.0][0]:+.3f} m")
check("crouch_lean_deg 25: hips well behind the neck", res[25.0][0] > 0.15, f"{res[25.0][0]:+.3f} m")
check("a wobbly crouch is not a seat", not res[25.0][2] and "crouching" in res[25.0][1], res[25.0][1])
from layable.estimator import crouch_curve
check("crouch curve: 0 standing, 1 in a half squat, 0 low down",
      crouch_curve(0.999) < 0.02 and abs(crouch_curve(0.75) - 1) < 1e-6 and crouch_curve(0.5) == 0.0)

print("SEAT: bobbing down and up isn't sitting; holding still low down is; leaning keeps the hips on the seat")
e = upright()
walk(e, [STAND] * 90)
walk(e, ramp(STAND, (0, 1.25, 0), 30) + ramp((0, 1.25, 0), STAND, 30))
check("quick dip: not seated", not e.seated and e.seat_counter < 0.5, f"counter {e.seat_counter:.2f}")
seat_neck = (0, 0.50 + 0.29 * 1.75 + 0.11 * 1.75, 0.05)
o = walk(e, ramp(STAND, seat_neck, 60) + [seat_neck] * 90)
check("held still: seated", e.seated and "sitting on a seat" in e.status["lying"], e.status["lying"])
seat_y = o["hip"].pos[1]
check("hips at the seat", abs(seat_y - 0.50) < 0.04, f"{seat_y:.2f}")
lean_fwd = (0, seat_neck[1] - 0.12, -0.25)
o = walk(e, ramp(seat_neck, lean_fwd, 45) + [lean_fwd] * 45)
check("leaning forward: still seated", e.seated, e.status["lying"])
check("... hips stay on the seat", abs(o["hip"].pos[1] - seat_y) < 0.02 and abs(o["hip"].pos[2] - 0.17) < 0.05,
      f"y {o['hip'].pos[1]:.2f} z {o['hip'].pos[2]:.2f}")
o = walk(e, ramp(lean_fwd, (0, 1.63, -0.2), 60) + [(0, 1.63, -0.2)] * 90)
check("standing up leaves the seat", not e.seated and e.status["upright"] == "standing", e.status["lying"])
check("... hips back at standing height", o["hip"].pos[1] > 0.88, f"{o['hip'].pos[1]:.2f}")
e = upright(seat_estimation=False, bed_height_cm=25)
o = walk(e, [seat_neck] * 135)
check("seat_estimation off: still sits (old behaviour)", e.status["mode"] == "sitting", e.status["lying"])
e = upright(seat_sensitivity=0.0)
walk(e, [STAND] * 90)
walk(e, ramp(STAND, seat_neck, 60) + [seat_neck] * 45)
check("low sensitivity: half a second still isn't enough", not e.seated, f"counter {e.seat_counter:.2f}")

print("STATE BLENDS: exp_smooth is frame-rate independent and snaps")
from layable.estimator import exp_smooth
check("exp_smooth guards", exp_smooth(1.0, 2.0, 0.1, 0.0) == 2.0 and exp_smooth(1.0, 2.0, 0.0, 5.0) == 1.0)
check("exp_smooth snaps within 0.001", exp_smooth(1.0, 1.0009, 0.001, 1.0) == 1.0009)
v45, v180 = 0.0, 0.0
for _ in range(45):
    v45 = exp_smooth(v45, 1.0, 1 / 45, 5.0)
for _ in range(180):
    v180 = exp_smooth(v180, 1.0, 1 / 180, 5.0)
check("same result at 45 Hz and 180 Hz", abs(v45 - v180) < 1e-9 and abs(v45 - (1 - math.exp(-5))) < 1e-9, f"{v45:.4f} {v180:.4f}")
print("SMOOTHING: one-euro filter on the trackers")
from layable.estimator import Pose
Q = (0, 0, 0, 1)


def feed(path, hz=90, **kw):
    e = upright(**kw)
    return e, [e._smooth("x", Pose(p, Q), 1 / hz).pos for p in path]


tau = DEFAULTS["smoothing_seconds"]
_, got = feed([(0, 0, 0)] + [(0.1, 0, 0)] * 30, smoothing_beta=0.0, reduce_vertical_smoothing=False)
x, ok = 0.0, True
for p in got[1:]:  # beta 0: a plain first-order low-pass with time constant smoothing_seconds
    a = (1 / 90) / (1 / 90 + tau)
    x += (0.1 - x) * a
    ok &= abs(p[0] - x) < 1e-9
check("beta 0 = plain low-pass", ok, f"{got[-1][0]:.5f} vs {x:.5f}")
move = [(1.0 * i / 90, 0, 0) for i in range(90)]  # walking pace, 1 m/s
lag0 = move[-1][0] - feed(move, smoothing_beta=0.0)[1][-1][0]
lag = move[-1][0] - feed(move)[1][-1][0]
check("moving at 1 m/s: much less lag than fixed smoothing", lag < 0.6 * lag0, f"{lag * 100:.1f} vs {lag0 * 100:.1f} cm")
import random
random.seed(3)
noise = [(random.gauss(0, 0.002), 0, random.gauss(0, 0.002)) for _ in range(900)]
spread = lambda ps: math.sqrt(sum(p[0] ** 2 for p in ps[90:]) / len(ps[90:]))
s0, s1 = spread(feed(noise, smoothing_beta=0.0)[1]), spread(feed(noise)[1])
check("holding still: 2 mm jitter damped about as much as before", s1 < 1.3 * s0 and s1 < 0.5 * 0.002,
      f"{s1 * 1000:.2f} vs {s0 * 1000:.2f} mm")
_, got = feed([(0, 0, 0)] + [(0.1, 0.1, 0.1)] * 2)
check("reduce_vertical_smoothing: y follows faster", got[-1][1] > 2 * got[-1][0], f"x {got[-1][0]:.4f} y {got[-1][1]:.4f}")
_, got = feed([(0, 0, 0)] + [(0.1, 0.1, 0.1)] * 180)
check("settles on the target", math.dist(got[-1], (0.1, 0.1, 0.1)) < 1e-4, str(got[-1]))
_, got = feed([(0, 0, 0), (0.1, 0.1, 0.1)], smoothing_seconds=0.0)
check("smoothing_seconds 0: off", got[-1] == (0.1, 0.1, 0.1))
e = upright()
e._smooth("r", Pose((0, 0, 0), Q), 1 / 90)
turned = basis_quat(norm((-1, 0, -1)), (0, 1, 0))
r0 = upright(smoothing_beta=0.0)
r0._smooth("r", Pose((0, 0, 0), Q), 1 / 90)
for i in range(1, 31):  # turn 45 deg in a third of a second
    q = basis_quat(norm((-math.sin(math.radians(45 * i / 30)), 0, -math.cos(math.radians(45 * i / 30)))), (0, 1, 0))
    a = e._smooth("r", Pose((0, 0, 0), q), 1 / 90).rot
    b = r0._smooth("r", Pose((0, 0, 0), q), 1 / 90).rot
check("turning fast: rotation lags less too", yaw_deg(a) > yaw_deg(b) + 3, f"{yaw_deg(a):.0f} vs {yaw_deg(b):.0f} deg of 45")

try:
    from layable.neural import NeuralBody
    NeuralBody()
    have_model = True
except Exception as ex:
    have_model = False
    print(f"  (learned model not available: {ex}; skipping model balance checks)")
if have_model:
    print("BALANCE on the learned model's body (what's sent when upright)")
    def model_hip(to, **kw):
        e = upright(use_model=True, experimental_lean=False, **kw)
        o = walk(e, [STAND] * 180 + ramp(STAND, to, 27) + [to] * 27)
        model_hip.feet_z = (o["left_foot"].pos[2] + o["right_foot"].pos[2]) / 2
        return o["hip"].pos, e.status["source"]
    fwd = (0, 1.63, -0.12)
    a, src = model_hip(fwd, hip_counterbalance=0.0, hip_sway=0.0)
    b, _ = model_hip(fwd, hip_counterbalance=1.3, hip_sway=0.0)
    check("model is the source", src == "model", src)
    check("counterbalance never pushes the model's hip forward", b[2] >= a[2] - 1e-6, f"z {b[2]:+.3f} vs {a[2]:+.3f}")
    bk = (0, 1.63, 0.06)
    a, _ = model_hip(bk, hip_counterbalance=0.0, hip_sway=0.0)
    b, _ = model_hip(bk, hip_counterbalance=1.3, hip_sway=0.0)
    fz = model_hip.feet_z
    check("leaning back: the model's hip only moves towards the feet", abs(b[2] - fz) <= abs(a[2] - fz) + 1e-6,
          f"hip-feet {b[2] - fz:+.3f} vs {a[2] - fz:+.3f} m")
    jump = sweep_jump(use_model=True)
    check("model: swaying across the feet line, no hip jumps", jump < 0.006, f"worst {jump * 1000:.1f} mm/frame")
    side = (0.08, 1.63, 0)
    a, _ = model_hip(side, hip_counterbalance=0.0, hip_sway=0.0)
    b, _ = model_hip(side, hip_counterbalance=0.0, hip_sway=0.5)
    check("sway moves the model's hip towards the loaded foot", b[0] - a[0] > 0.015, f"{(b[0] - a[0]) * 100:+.1f} cm")

print("random head poses never produce NaN or wild output")
import random
random.seed(1)
e = fresh()
bad = 0
for i in range(3000):
    q = norm((random.gauss(0, 1), random.gauss(0, 1), random.gauss(0, 1)))
    up = norm((random.gauss(0, 1), random.gauss(0, 1), random.gauss(0, 1)))
    pos = (random.uniform(-1, 1), random.uniform(0.3, 1.9), random.uniform(-1, 1))
    hands = {s: (pos[0] + random.uniform(-0.8, 0.8), pos[1] + random.uniform(-1, 0.5),
                 pos[2] + random.uniform(-0.8, 0.8)) if random.random() < 0.7 else None
             for s in ("left", "right")}
    if random.random() < 0.01:
        hands["left"] = (float("nan"), 0.0, 0.0)
    o = e.update(pos, basis_quat(q, up), 1 / 90, hands)
    for p in o.values():
        if any(math.isnan(c) or abs(c) > 10 for c in p.pos + p.rot):
            bad += 1
check("all outputs finite", bad == 0, f"{bad} bad")

print()
print("FAILED: %d" % failures if failures else "all checks passed")
sys.exit(1 if failures else 0)

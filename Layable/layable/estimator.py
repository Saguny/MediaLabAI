"""Guesses a body pose from the headset pose and, when they are tracked, the
two controllers, with special handling for lying in bed (on your back, side or
stomach) and rolling over.

Ideas:
  * The torso hangs off the neck along the head's own "down" axis, so when you
    lie down the body extends the right way automatically.
  * In bed, the hips are pinned to the mattress (bed_height_cm), so the torso
    tilts between flat and sitting up depending on how high your neck is.
  * Lying down, the body is in one of four roll states (back, left side,
    right side, stomach) picked from where your face points, with hysteresis:
    glancing sideways doesn't roll you, turning fully onto a side does.
    Hands make that call easier or harder: hands that could only be where
    they are if you lay on your back keep you on your back, and the other
    way round.
  * Upright, the body turns lazily with the head (only past the neck twist
    limit). Hands in front of you, and the line between your hands, pull the
    body round to face them.
  * Upright, a head lower than standing height is explained by bending
    forward (if you look down) and otherwise by bending the knees: a crouch
    pitches the torso forward, and a head held still low down means you sat
    on a chair, whose height is then remembered until you stand up.
  * Feet stay planted on the floor and only step when the body has moved or
    turned too far away from them. Where the head is over the planted feet
    moves the hips (on the rules' body and the learned model's alike):
    leaning forward pushes them back, weight on one foot pops them out over
    it, a wide stance lets the torso lean sideways.

Hands are always optional: a hand that isn't tracked, is out of arm's reach
or hasn't moved for a long time (a controller put down) fades out, and with
no hands everything falls back to head-only guessing.
"""

import math
from dataclasses import dataclass

from .vmath import (UP, add, axis_angle, basis_quat, cross, dot, from_to, horiz, length,
                    lerp, mul, nlerp, norm, qmul, reject, rotate, rotate_about,
                    signed_angle, sub)

ROLL_STATES = {"back": 0.0, "left": -90.0, "right": 90.0, "stomach": 180.0}
HANDS = ("left", "right")
SIDE_SIGN = {"left": -1.0, "right": 1.0}


@dataclass
class Pose:
    pos: tuple
    rot: tuple


def mix(a, b, t):
    return a + (b - a) * t


def clamp(x, lo, hi):
    return min(max(x, lo), hi)


def clamp01(x):
    return clamp(x, 0.0, 1.0)


def wrap180(a):
    return (a + 180.0) % 360.0 - 180.0


def smoothstep(t):
    t = clamp01(t)
    return t * t * (3 - 2 * t)


def finite(v):
    return v is not None and all(math.isfinite(c) and abs(c) < 1e4 for c in v)


def exp_smooth(cur, target, dt, rate):
    """Frame-rate independent approach of cur towards target (rate per
    second), snapping once within 0.001."""
    if rate <= 0:
        return target
    if dt <= 0:
        return cur
    v = cur + (target - cur) * clamp01(1.0 - math.exp(-rate * dt))
    return target if abs(target - v) < 0.001 else v


def _stand_cubic(x):
    return clamp((40.0 * x ** 3 - 190.0 * x ** 2 + 217.4 * x - 62.0) / 27.0, 0.0, 0.7)


def crouch_curve(stand):
    """How much a crouch pitches the torso forward, 0..1, from how far up
    you are (1 = standing). Standable's stand-up cubic, minus its standing
    value: 0 standing, peaks in a half squat (0.75), 0 again below ~0.53."""
    base = _stand_cubic(1.0)
    return clamp01((_stand_cubic(stand) - base) / (_stand_cubic(0.75) - base))


def two_bone(root, target, l1, l2, bend_dir):
    """Place the middle joint of a two-bone chain (hip-knee-ankle)."""
    d = sub(target, root)
    dist = length(d)
    axis = norm(d, fallback=(0.0, -1.0, 0.0))
    dist = min(max(dist, abs(l1 - l2) + 1e-3), l1 + l2 - 1e-4)
    end = add(root, mul(axis, dist))
    a = (l1 * l1 - l2 * l2 + dist * dist) / (2 * dist)
    h = math.sqrt(max(l1 * l1 - a * a, 0.0))
    bend = norm(reject(bend_dir, axis), fallback=norm(reject(UP, axis), fallback=(0.0, 0.0, -1.0)))
    mid = add(add(root, mul(axis, a)), mul(bend, h))
    return mid, end


class HandGate:
    """Decides how much one controller can be trusted, and fades it in/out.

    A hand counts when it is tracked and within arm's reach of the neck. A
    controller that stays put while the head moves away (put down on the
    bed) is treated as parked. A hand that is simply held still - lying
    quietly - keeps counting, unless hand_park_seconds (0 = off) says
    otherwise."""

    def __init__(self):
        self.pos = None       # last plausible position
        self.weight = 0.0     # 0 = ignored .. 1 = fully trusted
        self.anchor = None    # (hand, head) when the hand last moved
        self.still = 0.0

    def update(self, pos, head_pos, neck, reach, dt, cfg):
        target = 0.0
        if finite(pos):
            if self.anchor is None or math.dist(pos, self.anchor[0]) > 0.015:
                self.anchor = (pos, head_pos)
                self.still = 0.0
            else:
                self.still += dt
            head_moved = math.dist(head_pos, self.anchor[1])
            timer = cfg["hand_park_seconds"]
            parked = (self.still > 1.0 and head_moved > 0.3) or (timer > 0 and self.still > timer)
            if math.dist(pos, neck) <= reach and not parked:
                target = 1.0
                self.pos = pos
        else:
            self.anchor, self.still = None, 0.0
        if self.pos is None:
            target = 0.0
        rate = dt / max(cfg["hand_fade_seconds"], 1e-3)
        self.weight = clamp(self.weight + clamp(target - self.weight, -rate, rate), 0.0, 1.0)
        if self.weight == 0.0 and target == 0.0:
            self.pos = None
        return self.weight


class Estimator:
    def __init__(self, cfg):
        self.cfg = cfg
        self.front = None      # torso (hip) front direction
        self.in_bed = None
        self.roll_state = "back"
        self.roll = 0.0        # current torso roll angle in degrees (0 = face up)
        self.sideways = 0.0    # 0 = on back/stomach, 1 = fully on a side
        self.heading = None    # horizontal direction from neck to hips, in bed
        self.hands = {s: HandGate() for s in HANDS}
        self.feet = None       # planted feet when upright: side -> state dict
        self.smoothed = {}     # last output per tracker
        self.filters = {}      # one-euro filter state per tracker
        self.status = {}
        self.neural = None     # learned model (layable/neural.py), loaded on first use
        self.neural_error = None
        self.model_weight = 0.0  # 0 = rules, 1 = learned model; fades between them
        self.foot_lock = {}
        self.clock = 0.0
        self.tpose_time = 0.0      # how long the controllers have been in a T-pose
        self.calib_until = -1.0    # show the calibration pose until this time
        self.hip_offset = 0.0      # sideways hip correction (m, + = your right), from hip pushes
        self.push = {}             # per hand: outside / push / passive, for the hip collider
        self.stand = 0.999         # neck height over standing neck height, 0.001..0.999
        self.seated = False        # latched: sitting on a chair (seat estimation)
        self.seat_pos = None       # where the hips sit on it
        self.seat_counter = 0.0    # 0..1, fills while the head is low and still
        self.seat_blend = 0.0      # 0 = free hips .. 1 = hips on the seat
        self.seat_speed = 0.0      # smoothed neck speed, m/s
        self.seat_prev = None      # last neck position

    # -------------------------------------------------------------- helpers
    def dims(self):
        H = self.cfg["height_cm"] / 100.0
        return {
            "torso": 0.29 * H,       # neck (C7) to hip-joint centre
            "upper_leg": 0.245 * H,
            "lower_leg": 0.245 * H,
            "ankle": 0.04 * H,       # ankle height above the sole
            "hip_half": 0.05 * H,    # half the distance between hip joints
            "neck_down": 0.11 * H,   # eyes -> neck, along head down
            "neck_back": 0.05 * H,   # eyes -> neck, along head back
            "shoulder_half": 0.105 * H,  # neck -> shoulder joint, sideways
            "shoulder_drop": 0.02 * H,   # shoulder joints sit a bit below C7
            "arm": 0.40 * H,         # shoulder -> controller, arm stretched
        }

    def _lazy_front(self, target, axis, dt):
        """Front follows the head's facing, but only past the neck twist
        limit (plus an optional slow drift back to centre)."""
        target = norm(reject(target, axis), fallback=None)
        if self.front is None:
            self.front = target or norm(reject((0.0, 0.0, -1.0), axis), fallback=(0.0, 0.0, -1.0))
            return self.front
        front = norm(reject(self.front, axis), fallback=target or (0.0, 0.0, -1.0))
        if target is not None:
            ang = signed_angle(front, target, axis)
            limit = math.radians(self.cfg["neck_twist_deg"])
            step = ang - math.copysign(limit, ang) if abs(ang) > limit else 0.0
            tau = self.cfg["recenter_seconds"]
            if tau > 0:
                step += (ang - step) * min(dt / tau, 1.0)
            front = rotate_about(front, axis, step)
        self.front = norm(front)
        return self.front

    def _valid_hands(self):
        return {s: (g.pos, g.weight) for s, g in self.hands.items() if g.weight > 0.01 and g.pos is not None}

    def _hands_front(self, neck, head_dir, torso_up):
        """Where the hands say the body faces. Returns (direction or None,
        confidence 0..1), in the plane perpendicular to torso_up."""
        valid = self._valid_hands()
        head_dir = norm(reject(head_dir, torso_up), fallback=None)
        if not valid or head_dir is None:
            return None, 0.0
        acc, total = (0.0, 0.0, 0.0), 0.0
        wmin = min(w for _, w in valid.values())
        two = len(valid) == 2

        # 1) hands held out in front of you: the body faces their midpoint
        mid = mul(add(*[p for p, _ in valid.values()]), 0.5) if two else next(iter(valid.values()))[0]
        v = reject(sub(mid, neck), torso_up)
        n = length(v)
        if n > 0.15:
            dir_ = mul(v, 1.0 / n)
            if abs(math.degrees(signed_angle(head_dir, dir_, torso_up))) < (100.0 if two else 70.0):
                k = wmin * clamp01((n - 0.15) / 0.15) * (1.0 if two else 0.5)
                acc, total = add(acc, mul(dir_, k)), total + k

        # 2) the line from the left to the right hand runs roughly across the
        #    shoulders (hands hanging at your sides, or held apart)
        if two:
            lr = reject(sub(valid["right"][0], valid["left"][0]), torso_up)
            n = length(lr)
            if n > 0.2:
                f = norm(cross(torso_up, lr))
                if abs(math.degrees(signed_angle(head_dir, f, torso_up))) < 100.0:
                    k = wmin * clamp01((n - 0.2) / 0.15)
                    acc, total = add(acc, mul(f, k)), total + k

        if total <= 1e-6:
            return None, 0.0
        conf = clamp01(total) * clamp01(self.cfg["hand_trust"])
        return norm(reject(acc, torso_up), fallback=head_dir), conf

    @staticmethod
    def _turn_towards(a, b, axis, t):
        """Rotate a towards b around axis by fraction t (both roughly
        perpendicular to axis)."""
        if b is None or t <= 0:
            return a
        return norm(rotate_about(a, axis, signed_angle(a, b, axis) * t), fallback=a)

    def _roll_costs(self, neck, axis, ref, d):
        """For each roll state, how awkward the current hand positions would
        be (metres-ish). None without usable hands."""
        valid = self._valid_hands()
        if not valid:
            return None
        torso_up = mul(axis, -1.0)
        costs = {}
        for st, deg in ROLL_STATES.items():
            f = rotate_about(ref, axis, math.radians(deg))
            r = cross(f, torso_up)
            c = 0.0
            for side, (p, w) in valid.items():
                sgn = SIDE_SIGN[side]
                shoulder = add(neck, add(mul(r, sgn * d["shoulder_half"]), mul(axis, d["shoulder_drop"])))
                rel = sub(p, shoulder)
                behind = max(0.0, -dot(rel, f) - 0.08)          # reaching behind your back
                crossing = max(0.0, -sgn * dot(sub(p, neck), r) - 0.12)  # hand past the midline
                reach = max(0.0, length(rel) - d["arm"] * 1.05)  # arm would have to stretch
                c += w * (behind + 0.7 * crossing + 3.0 * reach)
            costs[st] = c
        return costs

    def _roll_front(self, head_fwd, axis, dt, costs=None):
        """Lying down: pick back / left / right / stomach from where the face
        points, then roll the torso there smoothly. Hand costs shift the
        thresholds towards the roll state the hands agree with."""
        ref = norm(reject(UP, axis))  # "face up" direction around the body axis
        face = norm(reject(head_fwd, axis), fallback=None)
        K, cap = self.cfg["hand_roll_deg_per_m"], 35.0

        def shift(a, b):  # > 0: hands prefer b over a
            return clamp(K * (costs[a] - costs[b]), -cap, cap) if costs else 0.0

        if face is not None:
            phi = math.degrees(signed_angle(ref, face, axis))
            a = abs(phi)
            enter, leave = self.cfg["side_enter_deg"], self.cfg["side_leave_deg"]
            side = "left" if phi < 0 else "right"
            st = self.roll_state
            if st == "back" and a > clamp(enter - shift("back", side), 20.0, 88.0):
                st = side
            elif st in ("left", "right"):
                if a < clamp(leave + shift(st, "back"), 5.0, 75.0):
                    st = "back"
                elif a > 180 - clamp(leave + shift(st, "stomach"), 5.0, 75.0):
                    st = "stomach"
                elif (phi < 0) != (st == "left") and a > enter:
                    st = side
            elif st == "stomach" and a < 180 - clamp(enter - shift("stomach", side), 20.0, 88.0):
                st = side
            self.roll_state = st
            base = ROLL_STATES[st]
            target = base + self.cfg["head_follow"] * wrap180(phi - base)
        else:
            target = self.roll
        tau = max(self.cfg["roll_seconds"], 1e-3)
        self.roll = wrap180(self.roll + wrap180(target - self.roll) * min(dt / tau, 1.0))
        return norm(rotate_about(ref, axis, math.radians(self.roll)))

    def _smooth(self, name, pose, dt):
        """One-euro filter (as Standable's Motion Smoothing): calm at rest
        (cutoff 1 / (2 pi smoothing_seconds)), opening up with speed
        (+ smoothing_beta per m/s) so fast moves don't lag. Up/down gets its
        own lighter filter with reduce_vertical_smoothing."""
        cfg = self.cfg
        tau = cfg["smoothing_seconds"]
        st = self.filters.get(name)
        if tau <= 0 or st is None or dt <= 0 or not finite(pose.pos):
            self.filters[name] = {"pos": pose.pos, "raw": pose.pos, "vel": (0.0, 0.0, 0.0),
                                  "rot": pose.rot, "raw_rot": pose.rot, "spin": 0.0}
            self.smoothed[name] = pose
            return pose
        dt = max(dt, 0.002)
        alpha = lambda cutoff: (2.0 * math.pi * cutoff * dt) / (2.0 * math.pi * cutoff * dt + 1.0)
        rest, beta = 1.0 / (2.0 * math.pi * tau), cfg["smoothing_beta"]
        ad = alpha(cfg["smoothing_d_cutoff"])
        st["vel"] = lerp(st["vel"], mul(sub(pose.pos, st["raw"]), 1.0 / dt), ad)
        a = alpha(rest + beta * length(st["vel"]))
        p, q = st["pos"], pose.pos
        y = mix(p[1], q[1], a)
        if cfg["reduce_vertical_smoothing"]:
            y = mix(p[1], q[1], alpha(4.0 * rest + 10.0 * beta * abs(st["vel"][1])))
        st["pos"], st["raw"] = (mix(p[0], q[0], a), y, mix(p[2], q[2], a)), q
        # rotation: the same filter on the turning speed (a point 0.57 m out)
        c = abs(sum(u * v for u, v in zip(st["raw_rot"], pose.rot)))
        st["spin"] = mix(st["spin"], 2.0 * math.acos(min(c, 1.0)) / dt, ad)
        st["rot"], st["raw_rot"] = nlerp(st["rot"], pose.rot, alpha(rest + 0.573 * beta * st["spin"])), pose.rot
        out = Pose(st["pos"], st["rot"])
        self.smoothed[name] = out
        return out

    # -------------------------------------------------------------- main
    def update(self, head_pos, head_rot, dt, hands=None):
        """hands: optional {"left": (x, y, z) or None, "right": ...}."""
        cfg, d = self.cfg, self.dims()
        floor = cfg["floor_offset"]
        bed = floor + cfg["bed_height_cm"] / 100.0

        head_up = rotate(head_rot, (0.0, 1.0, 0.0))
        head_fwd = rotate(head_rot, (0.0, 0.0, -1.0))
        neck = add(head_pos, add(mul(head_up, -d["neck_down"]), mul(head_fwd, -d["neck_back"])))

        stand_neck = floor + d["ankle"] + (d["upper_leg"] + d["lower_leg"]) * 0.995 + d["torso"]
        if math.isfinite(neck[1]):
            self.stand = clamp((neck[1] - floor) / max(stand_neck - floor, 0.1), 0.001, 0.999)

        reach = d["shoulder_half"] + d["arm"] + 0.10
        for side, gate in self.hands.items():
            pos = (hands or {}).get(side) if cfg["use_hands"] else None
            gate.update(pos, head_pos, neck, reach, dt, cfg)

        # ---- upright vs in bed. Sitting upright looks the same in bed and on
        # a chair, so history decides: coming from standing, you sit on a
        # chair (or the bed edge, feet on the floor) until you recline or get
        # low; coming from lying, you sit up in bed until you stand up.
        mode = cfg["mode"]
        if mode == "auto":
            T = d["torso"]
            limit = bed + cfg["hip_lift_back"] + T + 0.12
            low = bed + cfg["hip_lift_back"]
            reclined = math.degrees(math.acos(clamp(head_up[1], -1.0, 1.0))) > 35.0
            if self.in_bed is None:
                self.in_bed = neck[1] < limit
            elif self.in_bed and neck[1] > limit + 0.06:
                self.in_bed = False
            elif not self.in_bed and ((neck[1] < low + 0.7 * T and reclined) or neck[1] < low + 0.35 * T):
                self.in_bed = True
        else:
            self.in_bed = mode == "bed"

        if self.in_bed:
            self.feet = None
            self._leave_seat(reset=True)
            joints, (torso_up, front, chest_front) = self._bed(neck, head_up, head_fwd, bed, d, dt)
        else:
            joints, (torso_up, front, chest_front) = self._upright(neck, head_fwd, floor, d, dt)

        out = self._rules_output(neck, joints, torso_up, front, chest_front, d)
        model_out = self._model_output(head_pos, head_up, head_fwd, floor, bed, d, dt)
        # The model learned from motion capture, where hardly anyone lies
        # down; in bed the rules (made for a mattress) are better unless
        # model_in_bed says otherwise. Fade between them over half a second.
        want = 1.0 if model_out and (not self.in_bed or cfg["model_in_bed"]) else 0.0
        if model_out is None:
            self.model_weight = 0.0
        else:
            self.model_weight = clamp(self.model_weight + clamp(want - self.model_weight, -dt / 0.5, dt / 0.5), 0.0, 1.0)
        w = self.model_weight
        if w > 0:
            for k, pose in model_out.items():
                if k in out:
                    out[k] = Pose(lerp(out[k].pos, pose.pos, w), nlerp(out[k].rot, pose.rot, w))
                elif w > 0.5:
                    out[k] = pose  # whole skeleton, for the viewer

        valid = self._valid_hands()
        self.status["hands"] = ", ".join(f"{s} {w:.0%}" for s, (_, w) in valid.items()) or "none"
        self.status["mode"] = "bed" if self.in_bed else self.status.get("upright", "standing")
        self.status["head_y"] = head_pos[1] - floor
        self.status["source"] = ("model" if w > 0.5 else "rules") if model_out else "rules"

        self._balance(out, d)
        self._hip_push(out, dt)

        # VRChat calibrates full-body tracking while you hold a T-pose, and
        # expects the trackers on a straight, standing body. Lying in bed
        # you can't give it one, so while you T-pose Layable sends one.
        self.clock += dt
        if self._calibration_active(head_pos, d, dt):
            out = self._calibration_pose(head_pos, head_up, head_fwd, d)
            self.status["lying"] = "calibration pose (T-pose)"
            self.status["source"] = "calibration"
        return {k: self._smooth(k, v, dt) for k, v in out.items()}

    # ------------------------------------------------------------- balance
    BALANCE_NEEDS = ("hip", "neck", "chest", "left_foot", "right_foot", "left_hipjoint", "right_hipjoint",
                     "left_knee", "right_knee")

    def _balance(self, out, d):
        """Upright, on whichever body is being sent (rules or model): where
        the head is over the planted feet moves the hips. Leaning forward
        pushes them back (counterbalance), weight on one foot pops them out
        over it (sway), a wide stance keeps them between the feet (lean).
        The torso swings around the neck, the feet stay, the knees bend."""
        cfg = self.cfg
        if self.in_bed or not all(k in out for k in self.BALANCE_NEEDS):
            return
        free = 1.0 - self.seat_blend
        upright = self.stand * free
        if upright <= 0.01:
            return
        neck, hip = out["neck"].pos, out["hip"].pos
        lf, rf = out["left_foot"].pos, out["right_foot"].pos
        front = norm(horiz(rotate(out["hip"].rot, (0.0, 0.0, -1.0))), fallback=self.front or (0.0, 0.0, -1.0))
        right = cross(front, UP)
        support = mul(add(lf, rf), 0.5)
        head_off, hip_off = horiz(sub(neck, support)), horiz(sub(hip, support))
        e_f, h_f, e_r = dot(head_off, front), dot(hip_off, front), dot(head_off, right)

        # counterbalance: hips settle between "under the head" (0) and "over
        # the feet" (2.5); only ever pulled back, by at most what the head lean
        # asks for (so it fades to nothing as the head comes over the feet)
        cb = cfg["hip_counterbalance"]
        sgn = 1.0 if e_f >= 0 else -1.0
        excess = min(max(0.0, sgn * (h_f - e_f * (1.0 - 0.4 * cb))), 0.4 * cb * abs(e_f))
        shift_f = -sgn * excess * upright

        # weight distribution: -1 = all on the left foot, +1 = all on the right
        span = max(abs(dot(sub(rf, lf), right)) * 0.5, d["hip_half"])
        weight = clamp(e_r / span, -1.0, 1.0)
        sway = cfg["hip_sway"] * clamp01((self.stand - 0.7) / 0.3) * free
        shift_r = sway * weight * d["hip_half"]
        if cfg["experimental_lean"]:
            stance = clamp(math.dist(lf, rf) / (cfg["height_cm"] / 100.0), 0.0, 0.25)
            shift_r -= stance * e_r * upright
        shift = add(mul(front, clamp(shift_f, -0.15, 0.15)), mul(right, clamp(shift_r, -0.10, 0.10)))
        roll = clamp(math.radians(10.0) * sway * weight, -math.radians(8.0), math.radians(8.0))
        if length(shift) < 1e-4 and abs(roll) < 1e-4:
            return

        # swing the torso around the neck so the hip lands on the shifted spot
        # (keeps the torso length), tilt the pelvis up on the loaded side
        swing = from_to(sub(hip, neck), sub(add(hip, shift), neck))
        tilt = axis_angle(front, -roll)
        swung = lambda p: add(neck, rotate(swing, sub(p, neck)))
        new_hip = swung(hip)
        out["hip"] = Pose(new_hip, qmul(tilt, out["hip"].rot))
        out["chest"] = Pose(swung(out["chest"].pos), qmul(swing, out["chest"].rot))
        for k in ("skel_pelvis", "skel_spine1", "skel_spine2", "skel_spine3"):
            if k in out:
                out[k] = Pose(swung(out[k].pos), out[k].rot)
        for side in HANDS:
            hipj, knee, foot = out[side + "_hipjoint"].pos, out[side + "_knee"].pos, out[side + "_foot"].pos
            new_hipj = add(new_hip, rotate(tilt, sub(hipj, hip)))
            l1, l2 = math.dist(hipj, knee), math.dist(knee, foot)
            if l1 < 0.05 or l2 < 0.05:
                continue
            bend = sub(knee, lerp(hipj, foot, 0.5))
            new_knee, _ = two_bone(new_hipj, foot, l1, l2, norm(bend, fallback=front))
            out[side + "_hipjoint"] = Pose(new_hipj, qmul(tilt, out[side + "_hipjoint"].rot))
            q = from_to(sub(hipj, knee), sub(new_hipj, new_knee))
            out[side + "_knee"] = Pose(new_knee, qmul(q, out[side + "_knee"].rot))
            for k, p in (("skel_" + side + "_hip", new_hipj), ("skel_" + side + "_knee", new_knee)):
                if k in out:
                    out[k] = Pose(p, out[k].rot)

    # ------------------------------------------------------------ hip push
    # what swings when you push the hip: everything below the neck except the arms
    BODY = ("chest", "hip", "left_hipjoint", "right_hipjoint", "left_knee", "right_knee", "left_foot", "right_foot",
            "skel_pelvis", "skel_spine1", "skel_spine2", "skel_spine3", "skel_left_hip", "skel_right_hip",
            "skel_left_knee", "skel_right_knee", "skel_left_ankle", "skel_right_ankle", "skel_left_foot",
            "skel_right_foot")

    def _swing(self, out, lat_dir):
        """The rotation (pivot, axis, angle) that swings the body around the
        neck so the hip moves hip_offset sideways and everything stays in a
        straight line."""
        neck = out["neck"].pos
        arm = sub(out["hip"].pos, neck)
        L = max(length(arm), 0.2)
        axis = norm(cross(arm, lat_dir), fallback=UP)
        ang = math.asin(clamp(self.hip_offset / L, -0.9, 0.9))
        return neck, axis, ang

    def _swung(self, p, swing):
        neck, axis, ang = swing
        return add(neck, rotate_about(sub(p, neck), axis, ang))

    def _lateral(self, out):
        """Sideways direction of the body, kept horizontal: across the bed
        when lying, left-right when upright."""
        right = rotate(out["hip"].rot, (1.0, 0.0, 0.0))
        axis = sub(out["hip"].pos, out["neck"].pos)
        across = cross(horiz(axis), UP)  # the body's right when lying on your back
        return norm(add(horiz(right), mul(norm(across, fallback=(0.0, 0.0, 0.0)), length(horiz(axis)) / 0.3)),
                    fallback=horiz(right))

    def _hip_push(self, out, dt):
        """The hip is a solid block your hands can shove sideways. A hand that
        comes in from the side pushes the hip (and legs) away so the hand
        stays at the block's edge; the hip stays where you pushed it. A hand
        that comes in from above or in front (resting on your belly) passes
        through without pushing, until it leaves the block again."""
        cfg = self.cfg
        if not cfg["hip_push"] or "hip" not in out or "neck" not in out:
            return
        lat_dir = self._lateral(out)
        hip_raw = out["hip"].pos
        along_dir = norm(sub(hip_raw, out["neck"].pos))
        w = cfg["hip_push_width_cm"] / 100.0      # half-width of the block, sideways
        reach = cfg["hip_push_reach_cm"] / 100.0   # half-size the other two ways
        limit = cfg["hip_push_max_cm"] / 100.0
        pushing = False
        for side, g in self.hands.items():
            st = self.push.setdefault(side, {"mode": "outside", "prev_side": False, "dir": 0.0})
            h = g.pos if g.weight > 0.5 else None
            if h is None:
                st.update(mode="outside", prev_side=False)
                continue
            rel = sub(h, self._swung(hip_raw, self._swing(out, lat_dir)))
            lat = dot(rel, lat_dir)
            along = dot(rel, along_dir)
            depth = length(sub(rel, add(mul(lat_dir, lat), mul(along_dir, along))))
            near = abs(along) < reach and depth < reach
            inside = near and abs(lat) < w
            if not inside:
                st.update(mode="outside", prev_side=near, dir=math.copysign(1.0, lat))
                continue
            if st["mode"] == "outside":
                # entering: from the side pushes, from anywhere else passes through
                st["mode"] = "push" if st["prev_side"] else "passive"
            if st["mode"] == "push":
                d = st["dir"]  # the side the hand came from
                # move the hip away until the hand sits at the block's edge
                self.hip_offset = clamp(self.hip_offset + (lat - d * w), -limit, limit)
                pushing = True
        if pushing:
            self.status["hip"] = "pushing"
        elif self.status.get("hip") == "pushing":
            self.status["hip"] = f"shifted {self.hip_offset * 100:+.0f} cm"
        if abs(self.hip_offset) > 1e-4:
            swing = self._swing(out, lat_dir)
            q = axis_angle(swing[1], swing[2])
            for k in self.BODY:
                if k in out:
                    out[k] = Pose(self._swung(out[k].pos, swing), qmul(q, out[k].rot))

    def reset_hip(self):
        self.hip_offset = 0.0
        self.push = {}
        self.status["hip"] = "reset"

    def calibrate_for(self, seconds):
        """Show the calibration pose for this long, whatever you're doing."""
        self.calib_until = self.clock + seconds

    def _calibration_active(self, head_pos, d, dt):
        cfg = self.cfg
        H = cfg["height_cm"] / 100.0
        l, r = self.hands["left"], self.hands["right"]
        tpose = False
        if cfg["tpose_calibration"] and l.weight > 0.5 and r.weight > 0.5 and l.pos and r.pos:
            span = math.dist(l.pos, r.pos)
            mid = mul(add(l.pos, r.pos), 0.5)
            # arms spread wide, hands on either side of your chest
            tpose = span > cfg["tpose_span"] * H and math.dist(mid, head_pos) < 0.45
        self.tpose_time = self.tpose_time + dt if tpose else 0.0
        if self.tpose_time > 0.3:
            self.calib_until = max(self.calib_until, self.clock + cfg["calibration_hold_seconds"])
        return self.clock < self.calib_until

    def _calibration_pose(self, head_pos, head_up, head_fwd, d):
        """A straight standing body hanging from the headset, facing where
        the head faces, legs straight down: what VRChat's T-pose expects."""
        f = norm(horiz(sub(head_fwd, head_up)), fallback=self.front or (0.0, 0.0, -1.0))
        right = cross(f, UP)
        neck = add(head_pos, add((0.0, -d["neck_down"], 0.0), mul(f, -d["neck_back"])))
        hip = add(neck, (0.0, -d["torso"], 0.0))
        rot = basis_quat(f, UP)
        out = {"hip": Pose(hip, rot), "chest": Pose(lerp(neck, hip, 0.3), rot), "neck": Pose(neck, rot)}
        for side in HANDS:
            hipj = add(hip, mul(right, SIDE_SIGN[side] * d["hip_half"]))
            knee = add(hipj, (0.0, -d["upper_leg"], 0.0))
            ankle = add(knee, (0.0, -d["lower_leg"], 0.0))
            out[side + "_hipjoint"] = Pose(hipj, rot)
            out[side + "_knee"] = Pose(knee, rot)
            out[side + "_foot"] = Pose(ankle, rot)
            out[side + "_shoulder"] = Pose(add(neck, mul(right, SIDE_SIGN[side] * d["shoulder_half"])), rot)
        return out

    def _model_output(self, head_pos, head_up, head_fwd, floor, bed, d, dt):
        """The learned model's body, or None (no model, numpy missing, or
        use_model = false). History is kept even while it isn't used."""
        if not self.cfg.get("use_model", True) or self.neural_error:
            return None
        if self.neural is None:
            try:
                from .neural import NeuralBody
                self.neural = NeuralBody()
            except Exception as e:  # missing file or numpy: rules only
                self.neural_error = str(e)
                return None
        from .neural import JOINTS
        hands = {s: g.pos if g.weight > 0.5 else None for s, g in self.hands.items()}
        self.neural.push(head_pos, head_fwd, head_up, hands, dt)
        surface = bed if self.in_bed else floor
        pos, rot = self.neural.predict(self.cfg["height_cm"] / 100.0, surface)
        P = {n: tuple(float(c) for c in p) for n, p in zip(JOINTS, pos)}
        R = {n: (tuple(float(c) for c in f), tuple(float(c) for c in u)) for n, (f, u) in rot.items()}
        if not self.in_bed:
            P, R = self._model_yaw_memory(P, R, head_pos)
        if self.in_bed:  # nothing sinks into the mattress
            low = bed + 0.03
            P = {n: (p[0], max(p[1], low), p[2]) for n, p in P.items()}
        self._lock_feet(P, dt, surface, d)
        q = lambda n: basis_quat(R[n][0], R[n][1])
        out = {
            "hip": Pose(P["pelvis"], q("pelvis")),
            "chest": Pose(P["spine3"], q("spine3")),
            "neck": Pose(P["neck"], q("neck")),
        }
        for side in ("left", "right"):
            # knee tracker is declared as the upper leg: orient it like the thigh
            out[side + "_knee"] = Pose(P[side + "_knee"], q(side + "_hip"))
            out[side + "_foot"] = Pose(P[side + "_ankle"], q(side + "_ankle"))
            out[side + "_hipjoint"] = Pose(P[side + "_hip"], q(side + "_hip"))
            out[side + "_shoulder"] = Pose(P[side + "_shoulder"], q(side + "_shoulder"))
        for n in JOINTS:  # whole skeleton, for the viewer
            out["skel_" + n] = Pose(P[n], (0.0, 0.0, 0.0, 1.0))
        return out

    def _model_yaw_memory(self, P, R, head_pos):
        """Without controllers the model only sees a second of head motion, so
        after you look aside for a while it turns the whole body with the
        head. The rules remember which way the body faced (it only turns past
        the neck twist limit); turn the model's body towards that, as far as
        the controllers are missing."""
        if self.front is None:
            return P, R
        conf = sum(g.weight for g in self.hands.values()) / 2.0
        pf = R["pelvis"][0]
        if abs(pf[0]) + abs(pf[2]) < 1e-3:
            return P, R
        delta = signed_angle(norm(horiz(pf)), norm(horiz(self.front)), UP)
        ang = 0.8 * (1.0 - conf) * delta
        if abs(ang) < 1e-4:
            return P, R
        c = (head_pos[0], 0.0, head_pos[2])
        turn = lambda v: rotate_about(v, UP, ang)
        P = {n: add(turn(sub(p, c)), c) for n, p in P.items()}
        R = {n: (turn(f), turn(u)) for n, (f, u) in R.items()}
        return P, R

    def _lock_feet(self, P, dt, surface, d):
        """The model's feet wobble a little even when yours stand still. A
        foot that is low and barely moving locks in place, and lets go once
        the model moves it clearly away (a step) or lifts it."""
        for side in HANDS:
            k = side + "_ankle"
            p = P[k]
            st = self.foot_lock.setdefault(side, {"ema": p, "lock": None})
            prev = st["ema"]
            st["ema"] = lerp(prev, p, min(dt / 0.06, 1.0))
            speed = math.dist(st["ema"], prev) / max(dt, 1e-4)
            low = p[1] < surface + d["ankle"] + 0.10
            if st["lock"] is None:
                if low and speed < 0.3:
                    st["lock"] = p
            elif math.dist(p, st["lock"]) > 0.10 or not low:
                st["lock"] = None
            if st["lock"] is not None:
                P[k] = st["lock"]

    def _rules_output(self, neck, joints, torso_up, front, chest_front, d):
        out = {}
        hip_rot = basis_quat(front, torso_up)
        chest_up = norm(sub(neck, joints["hip"]), fallback=torso_up)
        chest_rot = basis_quat(norm(reject(chest_front, chest_up), fallback=front), chest_up)
        out["hip"] = Pose(joints["hip"], hip_rot)
        out["chest"] = Pose(lerp(neck, joints["hip"], 0.3), chest_rot)
        out["neck"] = Pose(neck, chest_rot)
        shoulder_r = cross(norm(reject(chest_front, chest_up), fallback=front), chest_up)
        hip_right = cross(front, torso_up)
        for side in ("left", "right"):
            knee, ankle, hipj = joints[side + "_knee"], joints[side + "_ankle"], joints[side + "_hip"]
            shin_up = norm(sub(knee, ankle))
            toes = norm(reject(front, shin_up), fallback=front)
            # knee tracker = the thigh: up runs knee -> hip, forward is the
            # front of the thigh (faces up when you sit), like the model's
            thigh_up = norm(sub(hipj, knee))
            thigh_fwd = norm(cross(thigh_up, hip_right), fallback=toes)
            out[side + "_knee"] = Pose(knee, basis_quat(thigh_fwd, thigh_up))
            out[side + "_foot"] = Pose(ankle, basis_quat(toes, shin_up))
            out[side + "_hipjoint"] = Pose(hipj, hip_rot)
            sh = add(neck, add(mul(shoulder_r, SIDE_SIGN[side] * d["shoulder_half"]),
                               mul(chest_up, -d["shoulder_drop"])))
            out[side + "_shoulder"] = Pose(sh, chest_rot)
        return out

    # -------------------------------------------------------------- upright
    def _upright(self, neck, head_fwd, floor, d, dt):
        cfg, T = self.cfg, d["torso"]
        legs = d["upper_leg"] + d["lower_leg"]

        # facing: follows the head lazily, then gets pulled round to what the
        # hands say (hands are attached to the shoulders, the head can twist)
        head_dir = norm(horiz(head_fwd), fallback=self.front or (0.0, 0.0, -1.0))
        hands_dir, conf = self._hands_front(neck, head_dir, UP)
        front = self._turn_towards(self._lazy_front(head_dir, UP, dt), hands_dir, UP, conf)
        right = cross(front, UP)
        chest_target = self._turn_towards(head_dir, hands_dir, UP, min(1.0, conf * 1.3))

        # how far below standing height is the neck, and why
        stand_hip = floor + d["ankle"] + legs * 0.995
        drop = max(0.0, stand_hip + T - neck[1])
        pitch = math.degrees(math.asin(clamp(-head_fwd[1], -1.0, 1.0)))  # + = looking down
        lean = clamp01((pitch - cfg["lean_start_deg"]) / 40.0)
        max_bend = math.acos(clamp(1.0 - drop / T, -1.0, 1.0))
        bend = lean * min(max_bend, math.radians(75))
        bent_hip = lambda b: add(neck, add((0.0, -T * math.cos(b), 0.0), mul(front, -T * math.sin(b) - 0.03)))
        hip = bent_hip(bend)
        seat = self._seat(neck, front, drop, lean, floor, d, dt)
        sit = self.seat_blend if cfg["seat_estimation"] else clamp01((max(0.0, stand_hip - hip[1]) - 0.10) / 0.20)

        # crouching (not sitting): the torso pitches forward and the hips go back
        crouch = min(math.radians(cfg["crouch_lean_deg"]) * crouch_curve(self.stand) * (1.0 - sit), max_bend)
        if crouch > bend:
            bend = crouch
            hip = bent_hip(bend)
        if seat is not None:  # on a chair the hips stay on the seat, however you lean
            off = sub(seat, neck)
            n = length(off)
            if n > T:
                seat = add(neck, mul(off, T / n))
            hip = lerp(hip, seat, self.seat_blend)
        knee_drop = max(0.0, stand_hip - hip[1])

        # where the feet want to be: under the hips, forward when sitting,
        # under the middle of the body when bending over
        reach_fwd = (0.03 + sit * cfg["seat_depth"] * d["upper_leg"]
                     + 0.5 * T * math.sin(bend) * (1.0 - self.seat_blend))
        spread = d["hip_half"] + 0.5 * sit * cfg["seat_spread"] * d["upper_leg"]
        want = {}
        for side in HANDS:
            hipj = add(hip, mul(right, SIDE_SIGN[side] * spread))
            want[side] = add((hipj[0], floor + d["ankle"], hipj[2]), mul(front, reach_fwd))
        feet = self._plant_feet(want, front, floor + d["ankle"], dt)

        bend_dir = add(front, (0.0, 0.3 * sit, 0.0))
        joints = {"hip": hip}
        for side in HANDS:
            hipj = add(hip, mul(right, SIDE_SIGN[side] * d["hip_half"]))
            knee, ankle = two_bone(hipj, feet[side], d["upper_leg"], d["lower_leg"], bend_dir)
            joints.update({side + "_hip": hipj, side + "_knee": knee, side + "_ankle": ankle})

        self.roll_state, self.roll, self.sideways = "back", 0.0, 0.0
        self.heading = None
        if sit > 0.5:
            posture = "sitting on a seat" if self.seated else "sitting"
        elif knee_drop > 0.08:
            posture = "crouching"
        elif math.degrees(bend) > 20:
            posture = "bending over"
        else:
            posture = "upright"
        self.status["upright"] = "sitting" if sit > 0.5 else "standing"
        self.status["lying"] = posture + (f", hands steer {conf:.0%}" if conf > 0.05 else "")
        chest_front = norm(lerp(front, chest_target, 0.5), fallback=front)
        return joints, (UP, front, chest_front)

    def _seat(self, neck, front, drop, lean, floor, d, dt):
        """Seat estimation. A chair holds you still: a head that stays low
        (and isn't looking down at the floor) for a moment means you sat
        down, and the seat height is latched. Dips and moving crouches don't
        get that far; a crouch held very still does, and seat_sensitivity
        trades that off. Returns where the hips sit, while seat_blend is
        above 0, else None."""
        cfg, T = self.cfg, d["torso"]
        if not finite(neck):
            return self.seat_pos if self.seat_blend > 0 else None
        prev, self.seat_prev = self.seat_prev, neck
        if prev is not None and dt > 0:
            self.seat_speed = exp_smooth(self.seat_speed, math.dist(neck, prev) / dt, dt, 10.0)
        if not cfg["seat_estimation"]:
            self._leave_seat(reset=True)
            return None
        s = clamp01(cfg["seat_sensitivity"])
        legs = d["upper_leg"] + d["lower_leg"]
        if not self.seated:
            low = drop > 0.2 * legs and lean < 0.5
            still = self.seat_speed < mix(0.05, 0.25, s)
            step = dt / mix(1.0, 0.2, s)
            self.seat_counter = clamp01(self.seat_counter + (step if low and still else -step))
            if self.seat_counter >= 1.0:
                y = max(neck[1] - T, floor + 0.25 * legs)
                self.seated, self.seat_pos = True, add((neck[0], y, neck[2]), mul(front, -0.03))
        else:
            rise = neck[1] - self.seat_pos[1]
            if rise > T + 0.10 or rise < 0.4 * T:  # stood up, or slid down to the floor
                self._leave_seat()
            else:  # a rolling chair: the seat follows once you're off further than a lean
                off = horiz(sub(neck, self.seat_pos))
                n, lim = length(off), 0.7 * T
                if n > lim:
                    self.seat_pos = add(self.seat_pos, mul(off, (n - lim) / n))
        self.seat_blend = exp_smooth(self.seat_blend, 1.0 if self.seated else 0.0, dt, 6.0)
        return self.seat_pos if self.seat_blend > 0 and self.seat_pos is not None else None

    def _leave_seat(self, reset=False):
        self.seated, self.seat_counter = False, 0.0
        if reset:
            self.seat_pos, self.seat_blend, self.seat_prev = None, 0.0, None

    def _plant_feet(self, want, front, ground, dt):
        """Feet stay where they are until the body moves or turns too far,
        then step (one at a time) to where they want to be. Once the body
        has stood still for a moment, a foot left behind steps back in."""
        cfg = self.cfg
        step_m = cfg["step_distance_cm"] / 100.0
        centre = mul(add(want["left"], want["right"]), 0.5)
        if self.feet is None or step_m <= 0:
            self.feet = {s: {"pos": want[s], "from": want[s], "t": None, "front": front} for s in HANDS}
            self.feet_still = {"centre": centre, "time": 0.0}
            if step_m <= 0:
                return {s: want[s] for s in HANDS}
        still = self.feet_still
        if math.dist(centre, still["centre"]) > 0.03:
            still["centre"], still["time"] = centre, 0.0
        else:
            still["time"] += dt
        if still["time"] > 0.8:
            step_m = min(step_m, 0.06)  # settle
        stepping = any(f["t"] is not None for f in self.feet.values())
        if not stepping:
            def need(s):
                f = self.feet[s]
                far = math.dist(horiz(f["pos"]), horiz(want[s]))
                turned = abs(math.degrees(signed_angle(f["front"], front, UP)))
                return max(far / step_m, turned / 35.0)
            side = max(HANDS, key=need)
            if need(side) > 1.0:
                f = self.feet[side]
                f["from"], f["t"] = f["pos"], 0.0
        out = {}
        for s in HANDS:
            f = self.feet[s]
            if f["t"] is not None:
                f["t"] += dt / max(cfg["step_seconds"], 1e-3)
                e = smoothstep(f["t"])
                p = lerp(f["from"], want[s], e)
                lift = 0.06 * math.sin(math.pi * min(f["t"], 1.0))
                f["pos"] = (p[0], ground, p[2])
                out[s] = (p[0], ground + lift, p[2])
                if f["t"] >= 1.0:
                    f["t"], f["front"] = None, front
            else:
                out[s] = (f["pos"][0], ground, f["pos"][2])
        return out

    # -------------------------------------------------------------- bed
    def _bed(self, neck, head_up, head_fwd, bed, d, dt):
        cfg = self.cfg
        T = d["torso"]

        # Heading of the body on the mattress: the head's "down" axis. When the
        # head is upright (sitting up) that says nothing, so fall back to the
        # face direction - sitting in bed you recline back, hips go forward.
        down_h = horiz(mul(head_up, -1.0))
        w = clamp01(1.0 - length(down_h) / 0.4)
        dir_h = norm(add(down_h, mul(horiz(head_fwd), w)), fallback=norm(horiz(head_fwd)))

        # Nodding or tilting the head on a pillow swings that axis around, but
        # the body stays put: only follow once it's off by more than the dead zone.
        if self.heading is None:
            self.heading = dir_h
        else:
            ang = signed_angle(self.heading, dir_h, UP)
            dz = math.radians(cfg["heading_deadzone_deg"])
            if abs(ang) > dz:
                self.heading = norm(rotate_about(self.heading, UP, ang - math.copysign(dz, ang)))
        dir_h = self.heading

        # Pin hips to the mattress: keep that heading, solve the tilt.
        hip_y = bed + mix(cfg["hip_lift_back"], cfg["hip_lift_side"], self.sideways)
        dy = min(max(neck[1] - hip_y, -0.1), T)
        h = math.sqrt(max(T * T - dy * dy, 0.0))
        hip = add(neck, add(mul(dir_h, h), (0.0, -dy, 0.0)))
        torso_up = norm(sub(neck, hip))
        axis = mul(torso_up, -1.0)  # neck -> hips
        tilt = math.degrees(math.asin(max(-1.0, min(1.0, dy / T))))

        # Torso front: roll states when lying, lazy follow when sitting up.
        upright = clamp01((tilt - 35.0) / 25.0)
        hands_dir, conf = self._hands_front(neck, head_fwd, torso_up)
        if upright >= 1.0:
            front = self._turn_towards(self._lazy_front(head_fwd, axis, dt), hands_dir, axis, conf)
            self.roll_state, self.roll = "back", 0.0
        else:
            costs = self._roll_costs(neck, axis, norm(reject(UP, axis)), d)
            front = self._roll_front(head_fwd, axis, dt, costs)
            if upright > 0:
                lazy = self._turn_towards(self._lazy_front(head_fwd, axis, dt), hands_dir, axis, conf)
                front = norm(lerp(front, lazy, upright))
            else:
                self.front = front
        self.sideways = abs(math.sin(math.radians(self.roll))) * (1.0 - upright)
        right = cross(front, torso_up)

        # Legs lie on the bed: lying, they continue the torso (plus a bit
        # forward on your side - curled up); sitting up, they point ahead.
        flex = mix(cfg["hip_flex_back"], cfg["hip_flex_side"], self.sideways)
        up2 = clamp01(dy / T) ** 2
        leg_dir = norm(add(mul(horiz(axis), 1.0 - up2), mul(horiz(front), flex + 1.5 * up2 + 1e-3)),
                       fallback=dir_h)
        bend_deg = mix(cfg["knee_bend_back_deg"], cfg["knee_bend_side_deg"], self.sideways)
        reach = (d["upper_leg"] + d["lower_leg"]) * math.cos(math.radians(bend_deg))
        bend_dir = add(front, (0.0, 0.5, 0.0))
        min_y = bed + cfg["leg_radius"]

        joints = {"hip": hip}
        for side, sgn in (("left", -1), ("right", 1)):
            hipj = add(hip, mul(right, sgn * d["hip_half"]))
            target_foot = add(hipj, mul(leg_dir, reach))
            target_foot = (target_foot[0], max(target_foot[1], min_y), target_foot[2])
            knee, ankle = two_bone(hipj, target_foot, d["upper_leg"], d["lower_leg"], bend_dir)
            knee = (knee[0], max(knee[1], min_y), knee[2])
            joints.update({side + "_hip": hipj, side + "_knee": knee, side + "_ankle": ankle})

        if upright >= 0.5:
            lying = "sitting up"
        else:
            lying = {"back": "on back", "left": "on left side", "right": "on right side",
                     "stomach": "on stomach"}[self.roll_state]
        self.status["lying"] = f"{lying}, torso {tilt:.0f} deg up"
        chest_front = front
        if upright > 0:
            head_dir = norm(reject(head_fwd, torso_up), fallback=front)
            chest_target = self._turn_towards(head_dir, hands_dir, torso_up, min(1.0, conf * 1.3))
            chest_front = norm(lerp(front, chest_target, 0.5 * upright), fallback=front)
        return joints, (torso_up, front, chest_front)

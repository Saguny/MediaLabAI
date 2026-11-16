"""Postures for the MediaLabAI character: where her (virtual) head goes.

Layable builds a body from a head pose. A person in VR moves their own head;
the character has none, so this makes one up.

* Standing, the head is her real, animated head (wherever the animation and
  the turn to the visitor put it), plus a slow drift a standing person
  makes: the weight moves from one foot to the other every few seconds and
  the body sways a little. Layable turns that into planted feet, a hip that
  goes out over the standing leg, and a step when she has drifted or turned
  too far.
* Every other posture is a head pose in her own space (Layable coordinates:
  +Y up, -Z is the way she faces, floor at 0), placed where she stood when
  she left standing. Changing posture moves the head there along a route a
  body can follow: standing -> crouching -> sitting on the ground -> lying.
"""

import math
import random
from collections import deque

from .vmath import add, axis_angle, basis_quat, nlerp, qmul, rotate

POSTURES = ("Stand", "Lean", "Crouch", "SitGround", "LieBack", "LieSide")

# Which postures a body can move between directly, and how long it takes.
# SitAcross is only a waypoint: before lying down she turns on her bottom
# (sitting, facing her left), so she lies across the view without her legs
# sweeping round as she goes down.
LINKS = {
    ("Stand", "Lean"): 0.9,
    ("Stand", "Crouch"): 1.1,
    ("Crouch", "SitGround"): 1.3,
    ("SitGround", "SitAcross"): 1.4,
    ("SitAcross", "LieBack"): 1.6,
    ("LieBack", "LieSide"): 1.0,
}

BED = {"mode": "bed", "bed_height_cm": 0.0, "heading_deadzone_deg": 0.0}
UPRIGHT = {"mode": "standing"}


def pitch_down(deg):
    return axis_angle((1.0, 0.0, 0.0), math.radians(-deg))


def target(name, eye, height):
    """Head pose (pos, rot) and settings for a posture, in her own space.
    eye: her standing eye height; height: her height, both in metres."""
    if name == "Lean":       # bending over towards the visitor
        return (0.0, 0.90 * eye, -0.15 * eye), pitch_down(50.0), UPRIGHT
    if name == "Crouch":     # crouched, peering down; looking down keeps Layable from seating her on air
        return (0.0, 0.62 * eye, -0.10 * eye), pitch_down(55.0), UPRIGHT
    if name == "SitGround":  # sitting on the ground, legs out towards the visitor
        return (0.0, 0.45 * height, 0.25 * eye), (0.0, 0.0, 0.0, 1.0), BED
    if name == "SitAcross":  # the same, turned to face her left: her head goes over her hips
        return (0.05 * eye, 0.45 * height, 0.20 * eye), axis_angle((0.0, 1.0, 0.0), math.radians(90.0)), BED
    if name == "LieBack":    # on her back across the view, head to her right, face up
        return (0.5 * height, 0.12, 0.25 * eye), basis_quat((0.0, 1.0, 0.0), (1.0, 0.0, 0.0)), BED
    if name == "LieSide":    # on her side, facing the visitor
        return (0.5 * height, 0.18, 0.25 * eye), basis_quat((0.0, 0.0, -1.0), (1.0, 0.0, 0.0)), BED
    raise KeyError(name)


def neighbours(name):
    for (a, b), secs in LINKS.items():
        if a == name:
            yield b, secs
        elif b == name:
            yield a, secs


def route(start, goal):
    """Postures to pass through from start to goal (excluding start)."""
    if start == goal:
        return []
    prev = {start: None}
    todo = deque([start])
    while todo:
        cur = todo.popleft()
        if cur == goal:
            break
        for nxt, _ in neighbours(cur):
            if nxt not in prev:
                prev[nxt] = cur
                todo.append(nxt)
    path, cur = [], goal
    while cur != start:
        path.append(cur)
        cur = prev[cur]
    return path[::-1]


def link_seconds(a, b):
    return LINKS.get((a, b)) or LINKS.get((b, a)) or 1.0


def smoothstep(t):
    t = min(max(t, 0.0), 1.0)
    return t * t * (3.0 - 2.0 * t)


class IdleDrift:
    """How a standing person's head wanders: the weight goes onto one foot,
    stays a few seconds, moves to the other (or back to the middle), and a
    slow sway rides on top. Offsets are in her own space (x = her right)."""

    def __init__(self, seed=None):
        self.rng = random.Random(seed)
        self.cur = (0.0, 0.0)
        self.start = (0.0, 0.0)
        self.goal = (0.0, 0.0)
        self.t = 0.0
        self.move = 1.5
        self.hold = self.rng.uniform(2.0, 4.0)
        self.clock = self.rng.uniform(0.0, 100.0)

    def update(self, dt, amount=1.0):
        self.clock += dt
        self.t += dt
        if self.t >= self.move + self.hold:
            side = self.rng.choice((-1.0, 1.0, 0.0, -1.0, 1.0))  # mostly onto a foot
            self.start, self.goal = self.cur, (side * self.rng.uniform(0.03, 0.05), self.rng.uniform(-0.015, 0.015))
            self.t, self.move, self.hold = 0.0, self.rng.uniform(1.2, 2.0), self.rng.uniform(3.0, 7.0)
        s = smoothstep(self.t / self.move)
        self.cur = tuple(a + (b - a) * s for a, b in zip(self.start, self.goal))
        sway = (0.006 * math.sin(self.clock * 2 * math.pi * 0.11),
                0.004 * math.sin(self.clock * 2 * math.pi * 0.07 + 1.3))
        return ((self.cur[0] + sway[0]) * amount, (self.cur[1] + sway[1]) * amount)


class PostureDriver:
    """Moves the virtual head between postures. update() returns
    (head_pos, head_rot, settings) for this frame, in the stage space the
    real head came in."""

    def __init__(self, seed=None):
        self.at = "Stand"          # last posture reached
        self.goal = "Stand"
        self.leg = None            # (from_pose, to_name, seconds, elapsed)
        self.head = None           # current virtual head (pos, rot)
        self.settings = UPRIGHT
        self.anchor = None         # (pos, rot) of her own space, latched when she leaves standing
        self.drift = IdleDrift(seed)

    @property
    def busy(self):
        return self.leg is not None or self.at != self.goal

    def set_goal(self, name):
        if name in POSTURES:
            self.goal = name

    def update(self, real_head, frame, eye, height, dt, drift=1.0):
        """real_head: her animated head (pos, rot). frame: her own space (her
        root: pos on the floor, rot with -Z the way she faces)."""
        # standing: the real head plus a standing person's drift, sideways and
        # forward in her own space
        dx, dz = self.drift.update(dt, drift)
        standing = (add(real_head[0], add(rotate(frame[1], (dx, 0.0, 0.0)), rotate(frame[1], (0.0, 0.0, -dz)))),
                    real_head[1])

        def pose_of(name):
            if name == "Stand":
                return standing[0], standing[1], UPRIGHT
            anchor = self.anchor or frame
            pos, rot, settings = target(name, eye, height)
            return add(anchor[0], rotate(anchor[1], pos)), qmul(anchor[1], rot), settings

        if self.head is None:
            self.head = standing
        if self.leg is None and self.at != self.goal:
            if self.at == "Stand":
                self.anchor = frame
            nxt = route(self.at, self.goal)[0]
            self.leg = (self.head, nxt, link_seconds(self.at, nxt), 0.0)

        if self.leg is not None:
            start, to, secs, elapsed = self.leg
            elapsed += dt
            t = smoothstep(elapsed / secs)
            pos, rot, settings = pose_of(to)
            self.head = (tuple(a + (b - a) * t for a, b in zip(start[0], pos)), nlerp(start[1], rot, t))
            # the settings switch as the move starts, so the body heads for the new way of standing or lying
            self.settings = settings
            if elapsed >= secs:
                self.at, self.leg = to, None
            else:
                self.leg = (start, to, secs, elapsed)
        else:
            pos, rot, self.settings = pose_of(self.at)
            self.head = (pos, rot)
        return self.head[0], self.head[1], self.settings

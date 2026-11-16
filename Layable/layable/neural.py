"""Learned body pose: a small network trained on motion capture (AMASS) that
predicts the whole skeleton from the last second of headset and controller
motion. Runs with numpy only; training lives in train/ (PyTorch).

The feature code here is shared with training, so the network sees exactly
the same numbers live as it saw while learning.

Coordinates: Y up, -Z forward, metres (Monado/OpenXR). Each window is
expressed in a frame that follows the head: origin under the latest head
position (at the height of the surface you're on: floor, or the mattress in
bed), turned so the head's heading points along -Z. That makes the network
independent of where you stand in the room and which way you face.
"""

import math
from pathlib import Path

try:
    import numpy as np
except ImportError:  # Layable still runs, rules only
    np = None

FPS = 30          # history is resampled to this rate
WINDOW = 32       # frames of history the network sees (~1.07 s)
PER_FRAME = 17    # head pos 3, head fwd 3, head up 3, left hand 3 + valid 1, right hand 3 + valid 1

# SMPL body joints, in the order the network outputs them
JOINTS = ["pelvis", "left_hip", "right_hip", "spine1", "left_knee", "right_knee", "spine2",
          "left_ankle", "right_ankle", "spine3", "left_foot", "right_foot", "neck",
          "left_collar", "right_collar", "head", "left_shoulder", "right_shoulder",
          "left_elbow", "right_elbow", "left_wrist", "right_wrist"]
PARENTS = [-1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19]
MIRROR = [JOINTS.index(n.replace("left", "@").replace("right", "left").replace("@", "right")) for n in JOINTS]
# joints whose orientation the network also predicts (they become trackers)
ROT_JOINTS = ["pelvis", "spine3", "left_knee", "right_knee", "left_ankle", "right_ankle",
              "left_hip", "right_hip", "neck", "left_shoulder", "right_shoulder"]
OUT_POS = 3 * len(JOINTS)
OUT_DIM = OUT_POS + 6 * len(ROT_JOINTS)
IN_DIM = WINDOW * PER_FRAME + 1  # + body height


# ------------------------------------------------------------------ frames
def heading(fwd, up):
    """A horizontal direction that turns with the head and never vanishes:
    the facing (from the head's right axis) plus "towards the feet" (from
    -up). Works upright, lying on your back, side or stomach, and looking
    straight down. fwd, up: (..., 3) arrays. Returns unit (..., 3)."""
    right = np.cross(fwd, up)
    h1 = np.stack([right[..., 2], np.zeros_like(right[..., 0]), -right[..., 0]], -1)  # UP x right
    d = fwd - up
    h2 = np.stack([d[..., 0], np.zeros_like(d[..., 0]), d[..., 2]], -1) / math.sqrt(2)
    v = h1 + h2
    n = np.linalg.norm(v, axis=-1, keepdims=True)
    return np.where(n > 1e-6, v / np.maximum(n, 1e-9), np.array([0.0, 0.0, -1.0]))


def canon_rotation(h):
    """3x3 rotation (about Y) taking heading h to -Z. h: (..., 3)."""
    yaw = np.arctan2(-h[..., 0], -h[..., 2])  # angle of h from -Z, + = towards -X (left)
    c, s = np.cos(yaw), np.sin(yaw)
    z, o = np.zeros_like(c), np.ones_like(c)
    # rotate by -yaw about Y
    return np.stack([np.stack([c, z, -s], -1), np.stack([z, o, z], -1), np.stack([s, z, c], -1)], -2)


def window_features(win, height_m, surface_y):
    """win: dict of arrays over WINDOW frames, world space:
         head_pos, head_fwd, head_up, lhand, rhand: (W, 3); lvalid, rvalid: (W,)
    Returns (features (IN_DIM,), R (3,3), origin (3,)). Positions are in
    the head-following frame (y measured from surface_y)."""
    h = heading(win["head_fwd"][-1], win["head_up"][-1])
    R = canon_rotation(h)
    origin = np.array([win["head_pos"][-1][0], surface_y, win["head_pos"][-1][2]])
    tp = lambda p: (p - origin) @ R.T
    tv = lambda v: v @ R.T
    lv = win["lvalid"][:, None].astype(np.float64)
    rv = win["rvalid"][:, None].astype(np.float64)
    f = np.concatenate([tp(win["head_pos"]), tv(win["head_fwd"]), tv(win["head_up"]),
                        tp(win["lhand"]) * lv, lv, tp(win["rhand"]) * rv, rv], -1)
    return np.concatenate([f.reshape(-1), [height_m]]), R, origin


def decode(out, R, origin):
    """Network output -> world joint positions (22, 3) and rotations
    {joint: (fwd, up)} for ROT_JOINTS."""
    pos = out[:OUT_POS].reshape(len(JOINTS), 3) @ R + origin
    rot = {}
    r6 = out[OUT_POS:].reshape(len(ROT_JOINTS), 6)
    for name, v in zip(ROT_JOINTS, r6):
        fwd = v[:3] @ R
        up = v[3:] @ R
        fwd = fwd / (np.linalg.norm(fwd) + 1e-9)
        up = up - fwd * np.dot(up, fwd)
        up = up / (np.linalg.norm(up) + 1e-9)
        rot[name] = (fwd, up)
    return pos, rot


# ------------------------------------------------------------------ network
class Net:
    """MLP with GELU, weights from an .npz exported by train/train.py."""

    def __init__(self, path):
        z = np.load(path)
        self.meta = {k[5:]: z[k] for k in z.files if k.startswith("meta_")}
        n = int(self.meta["layers"])
        self.W = [z[f"W{i}"] for i in range(n)]
        self.b = [z[f"b{i}"] for i in range(n)]
        self.in_mean, self.in_std = z["in_mean"], z["in_std"]
        self.out_mean, self.out_std = z["out_mean"], z["out_std"]
        if self.W[0].shape[0] != IN_DIM or self.W[-1].shape[1] != OUT_DIM:
            raise ValueError(f"{path}: model was made for different features; retrain it")

    def __call__(self, x):
        h = (x - self.in_mean) / self.in_std
        for i, (W, b) in enumerate(zip(self.W, self.b)):
            h = h @ W + b
            if i < len(self.W) - 1:
                h = 0.5 * h * (1 + np.tanh(0.7978845608 * (h + 0.044715 * h ** 3)))
        return h * self.out_std + self.out_mean


DEFAULT_MODEL = Path(__file__).with_name("models") / "body.npz"


class NeuralBody:
    """Keeps a second or so of raw, timestamped inputs and runs the network
    on a window sampled at exact 1/FPS steps back from the newest pose,
    the same spacing the network was trained on, whatever the update rate."""

    KEYS = ("head_pos", "head_fwd", "head_up", "lhand", "rhand")

    def __init__(self, path=DEFAULT_MODEL):
        if np is None:
            raise RuntimeError("numpy is not installed")
        self.net = Net(path)
        self.path = Path(path)
        self.t = 0.0
        self.times, self.rows, self.valid = [], [], []  # rows: 15 floats per sample

    def push(self, head_pos, head_fwd, head_up, hands, dt):
        """Call every update with the newest pose."""
        self.t += max(dt, 1e-4)
        lh = hands.get("left") if hands else None
        rh = hands.get("right") if hands else None
        self.times.append(self.t)
        self.rows.append(tuple(head_pos) + tuple(head_fwd) + tuple(head_up)
                         + (tuple(lh) if lh is not None else (0.0, 0.0, 0.0))
                         + (tuple(rh) if rh is not None else (0.0, 0.0, 0.0)))
        self.valid.append((lh is not None, rh is not None))
        keep = self.t - (WINDOW / FPS + 0.2)
        while len(self.times) > 2 and self.times[1] < keep:
            del self.times[0], self.rows[0], self.valid[0]

    def window(self):
        """The inputs at exact 1/FPS steps back from now (oldest first);
        before there's enough history, the oldest sample is repeated."""
        ts = np.array(self.times)
        want = self.t - np.arange(WINDOW - 1, -1, -1) / FPS
        rows = np.array(self.rows)
        cols = np.stack([np.interp(want, ts, rows[:, c]) for c in range(rows.shape[1])], -1)
        win = {k: cols[:, 3 * i: 3 * i + 3] for i, k in enumerate(self.KEYS)}
        for k in ("head_fwd", "head_up"):
            win[k] = win[k] / np.maximum(np.linalg.norm(win[k], axis=-1, keepdims=True), 1e-9)
        nearest = np.clip(np.searchsorted(ts, want), 0, len(ts) - 1)
        v = np.array(self.valid)[nearest]
        win["lvalid"], win["rvalid"] = v[:, 0], v[:, 1]
        return win

    def predict(self, height_m, surface_y):
        if not self.times:
            return None
        x, R, origin = window_features(self.window(), height_m, surface_y)
        return decode(self.net(x), R, origin)

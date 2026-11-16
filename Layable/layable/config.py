import os
import tomllib
from pathlib import Path

DEFAULTS = {
    # --- you ---
    "height_cm": 196.0,        # your height in centimetres
    # --- your room ---
    "bed_height_cm": 55.0,     # top of the mattress, centimetres above the floor
    "floor_offset": 0.0,       # where the floor is in WiVRn's space (usually 0)
    # --- behaviour ---
    "mode": "auto",            # "auto", "bed" or "standing"
    "trackers": ["hip", "chest", "left_knee", "right_knee", "left_foot", "right_foot"],
    "neck_twist_deg": 50.0,    # upright: head can turn this far before the body turns along
    "side_enter_deg": 60.0,    # lying: face this far off the ceiling = you rolled onto a side
    "side_leave_deg": 40.0,    # ... and back under this = on your back again
    "head_follow": 0.3,        # how much the torso copies small head turns (0..1)
    "roll_seconds": 0.3,       # how long a roll-over takes to animate
    "heading_deadzone_deg": 30.0,  # lying: head nods/tilts this big don't swing the body
    "recenter_seconds": 2.0,   # >0: torso slowly turns back under the head (a person squares up to what they look at)
    "smoothing_seconds": 0.08, # output smoothing at rest (higher = calmer, laggier; 0 = off)
    "smoothing_beta": 2.0,     # ... less of it the faster you move (per m/s; 0 = same everywhere)
    "smoothing_d_cutoff": 1.0, # how smoothly that speed is measured (Hz)
    "reduce_vertical_smoothing": True,  # less smoothing up/down, so crouches and jumps don't lag
    # --- learned model ---
    "use_model": False,        # use layable/models/body.npz when it exists (needs numpy)
    "model_in_bed": False,     # also in bed (the model has seen little lying down)
    # --- VRChat calibration ---
    "tpose_calibration": False, # T-pose with the controllers = send a straight standing body
    "tpose_span": 0.7,         # ... when the controllers are this far apart (x your height)
    "calibration_hold_seconds": 1.0,  # keep it this long after you drop the T-pose
    # --- hip push ---
    "hip_push": False,         # the hip is a block your controllers can shove sideways
    "hip_push_width_cm": 15.0, # half-width of that block (hip centre to its side)
    "hip_push_reach_cm": 18.0, # how far it reaches towards your head/feet and front/back
    "hip_push_max_cm": 25.0,   # largest sideways shift
    # --- hands (controllers) ---
    "use_hands": False,        # use controller positions to judge how the body is turned
    "hand_trust": 0.7,         # upright: how strongly hands steer the body's facing (0..1)
    "hand_roll_deg_per_m": 90.0,  # lying: how much hand positions move the roll-over thresholds
    "hand_park_seconds": 0.0,  # >0: a controller that doesn't move for this long is ignored
    "hand_fade_seconds": 0.3,  # hands fade in/out over this time
    # --- upright ---
    "lean_start_deg": 30.0,    # looking further down than this = bending over, not crouching
    "step_distance_cm": 22.0,  # feet stay planted until the body is this far away (0 = no planting)
    "step_seconds": 0.3,       # how long one step takes
    "hip_counterbalance": 1.3, # hips move back against a head lean over planted feet (0 = hips under the head)
    "hip_sway": 0.5,           # hips pop out over the leg you put your weight on (0 = off)
    "experimental_lean": True, # wide stance: the torso leans sideways, the hips stay between the feet
    "crouch_lean_deg": 25.0,   # crouching: how far the torso pitches forward (hips go back)
    "seat_estimation": True,   # head held still low down = sitting on a chair: hips stay on the seat
    "seat_sensitivity": 0.5,   # 0..1: higher = takes less stillness, and less time, to sit
    "seat_depth": 0.85,        # seated: feet forward of the hips, times the thigh length
    "seat_spread": 0.2,        # seated: extra distance between the knees, times the thigh length
    # --- body on the mattress ---
    "hip_lift_back": 0.10,     # hip joint height above the mattress, on your back
    "hip_lift_side": 0.17,     # ... and on your side
    "hip_flex_back": 0.0,      # how far the legs swing forward, on your back
    "hip_flex_side": 0.5,      # ... on your side (curled up a bit)
    "knee_bend_back_deg": 10.0,
    "knee_bend_side_deg": 35.0,
    "leg_radius": 0.06,        # keeps knees and feet from sinking into the bed
    # --- plumbing ---
    "rate_hz": 90.0,
}

CONFIG_PATH = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "layable-medialab" / "config.toml"

TEMPLATE = """# Layable settings. Saved changes apply live, no restart needed
# (except "trackers", which needs a restart and a headset reconnect).

height_cm = 196          # your height in centimetres
bed_height_cm = 55       # top of your mattress above the floor, in cm (e.g. 25)
floor_offset = 0.0       # raise/lower if WiVRn's floor isn't at 0
mode = "auto"            # "auto", "bed" or "standing"

trackers = ["hip", "chest", "left_knee", "right_knee", "left_foot", "right_foot"]

# Lying down: rolling onto a side is detected when your face points this far
# away from the ceiling, and undone when it comes back under side_leave_deg.
side_enter_deg = 60.0
side_leave_deg = 40.0
head_follow = 0.3        # how much the body copies small head turns (0..1)
roll_seconds = 0.3       # how long a roll-over takes
heading_deadzone_deg = 30.0  # nodding/tilting your head this much won't swing your body

neck_twist_deg = 50.0    # standing/sitting: head turn before the body turns along
recenter_seconds = 2.0   # >0 makes the body slowly turn to follow the head
# Smoothing adapts to speed (a one-euro filter, like Standable's): calm at rest,
# almost none on fast moves, so the body neither jitters nor lags.
smoothing_seconds = 0.08  # at rest; 0 turns smoothing off
smoothing_beta = 2.0      # less smoothing per m/s of movement (0 = the same at any speed)
smoothing_d_cutoff = 1.0
reduce_vertical_smoothing = true  # crouches and jumps follow faster than sideways wobble

# Learned body model (layable/models/body.npz, made with train/). Without it,
# or without numpy, Layable uses its hand-written rules.
use_model = false
model_in_bed = false     # in bed, use the rules (the model has seen little lying down)

# VRChat calibration: while you hold a T-pose (controllers spread wide),
# Layable sends a straight standing body, even if you're lying in bed.
tpose_calibration = false
tpose_span = 0.7         # controllers at least this far apart, times your height
calibration_hold_seconds = 1.0

# Hip push: the hip is a block your controllers can shove sideways, coming in
# from the side (a hand resting on your belly doesn't push). It stays where
# you pushed it; `layable --reset-hip` puts it back.
hip_push = false
hip_push_width_cm = 15
hip_push_reach_cm = 18
hip_push_max_cm = 25

# Controllers: used (when tracked and in reach) to tell which way your body
# faces and which way you're lying. A controller put down is ignored.
use_hands = false
hand_trust = 0.7         # upright: how strongly hands steer the body's facing (0..1)
hand_roll_deg_per_m = 90.0  # lying: how much hands can move the roll-over thresholds
hand_park_seconds = 0     # >0: ignore a controller that hasn't moved for this long

# Upright: a lower head means bending over (if looking down), crouching or sitting.
lean_start_deg = 30.0
step_distance_cm = 22.0  # feet stay planted until the body moves this far (0 = off)
step_seconds = 0.3

# Upright balance (also applied to the learned model's body). Leaning your head
# over planted feet pushes the hips back (counterbalance); shifting your weight
# onto one leg pops the hip out over it (sway). experimental_lean lets the torso
# lean sideways over a wide stance instead of dragging the hips along.
hip_counterbalance = 1.3  # 0 = hips straight under the head, 1 = whole body pivots at the ankles
hip_sway = 0.5
experimental_lean = true
crouch_lean_deg = 25.0    # crouching: torso pitches forward, hips go back

# Seat estimation: holding your head still, low down, means you sat on a chair.
# The hips then stay on the seat while you lean, until you stand up.
seat_estimation = true
seat_sensitivity = 0.5    # 0..1: higher = sits down sooner, with more head motion
seat_depth = 0.85         # seated: feet forward of the hips, times the thigh length
seat_spread = 0.2         # seated: knees apart, times the thigh length

hip_lift_back = 0.10
hip_lift_side = 0.17
hip_flex_back = 0.0
hip_flex_side = 0.5
knee_bend_back_deg = 10.0
knee_bend_side_deg = 35.0
leg_radius = 0.06
"""


class Config(dict):
    def __init__(self, path=CONFIG_PATH):
        super().__init__(DEFAULTS)
        self.path = Path(path)
        self.mtime = None
        if not self.path.exists():
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self.path.write_text(TEMPLATE)
        self.reload(force=True)

    def reload(self, force=False):
        """Returns True if the file changed and was reloaded."""
        try:
            mtime = self.path.stat().st_mtime
        except FileNotFoundError:
            return False
        if not force and mtime == self.mtime:
            return False
        self.mtime = mtime
        try:
            data = tomllib.loads(self.path.read_text())
        except (tomllib.TOMLDecodeError, OSError) as e:
            print(f"[config] {self.path}: {e} (keeping previous values)")
            return False
        new = dict(DEFAULTS)
        for k, v in data.items():
            if k == "height":
                print("[config] 'height' is now 'height_cm', in centimetres (e.g. 196)")
                continue
            if k == "bed_height":
                print("[config] 'bed_height' is now 'bed_height_cm', in centimetres (e.g. 55)")
                continue
            if k not in DEFAULTS:
                print(f"[config] unknown setting '{k}' ignored")
                continue
            problem = _check(k, v)
            if problem:
                print(f"[config] {k} = {v!r}: {problem}; keeping {self.get(k, DEFAULTS[k])!r}")
                v = self.get(k, DEFAULTS[k])
            new[k] = float(v) if isinstance(DEFAULTS[k], float) else v
        self.clear()
        self.update(new)
        return True


def _check(key, value):
    default = DEFAULTS[key]
    if isinstance(default, bool):
        if not isinstance(value, bool):
            return "must be true or false"
    elif isinstance(default, float):
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return "must be a number"
        if value < 0 and key != "floor_offset":
            return "must not be negative"
        if key == "bed_height_cm":
            if 0 < value < 3:
                return "looks like metres; write centimetres, e.g. 55"
            if value > 150:
                return "should be centimetres, at most 150"
        if key == "height_cm":
            if value < 3:
                return "looks like metres; write centimetres, e.g. 196"
            if not 100 <= value <= 250:
                return "should be your height in centimetres"
        if key in ("hand_trust", "seat_sensitivity") and value > 1:
            return "must be between 0 and 1"
        if key == "hip_counterbalance" and value > 3:
            return "must be between 0 and 3"
        if key == "hip_sway" and value > 15:
            return "must be between 0 and 15"
        if key == "crouch_lean_deg" and value > 60:
            return "must be between 0 and 60"
        if key == "rate_hz" and value < 1:
            return "must be at least 1"
    elif key == "mode":
        if value not in ("auto", "bed", "standing"):
            return 'must be "auto", "bed" or "standing"'
    elif key == "trackers":
        if not isinstance(value, list) or not all(isinstance(t, str) for t in value):
            return "must be a list of names"
    return None

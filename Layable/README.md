# Layable for the MediaLabAI character

A copy of Layable, the full-body estimator from `~/Documents/tools/layable`
(the VRChat one). Here it gives the AI character her body, the way a real
person would hold it:

- Feet planted on the ground, never floating and never sinking into it.
- Her weight moves from one foot to the other, and the hip goes out over the
  standing leg.
- When she turns round, her feet step round with her.
- She chooses her postures herself: `Stand`, `Lean`, `Crouch`, `SitGround`,
  `LieBack`, `LieSide`. She gets there the way a person would: crouching,
  then sitting, turning on her bottom, then lying back.

The two copies don't touch each other:

|  | VRChat copy | This copy |
| --- | --- | --- |
| Folder | `~/Documents/tools/layable` | `MediaLabAI/Layable` |
| Input | your headset, through WiVRn | her animated head, from Unity |
| Settings | `~/.config/layable/config.toml` | `~/.config/layable-medialab/config.toml` |
| Ports | 39572 control, 39571 viewer, 8765 web | 39580 Unity, 39581 viewer, 8766 web |
| Runs as | `layable.service` | started by Unity (`LayableBody`) |

## How it works

1. `Assets/HexeRolePlay/LayableBody.cs` runs on her each frame, after the
   Animator. It sends her animated head, where she stands and the posture she
   wants to `layable.puppet` over UDP (127.0.0.1:39580).
2. `layable/posture.py` makes up the head a real person would have:
   - Standing, it is her own head, plus a slow weight shift and sway.
   - In every other posture it is a head pose placed where she stood, and
     changing posture moves it there along a route a body can follow.
3. `layable/estimator.py` is Layable's own estimator. It turns that head into
   a body: hip, chest, knees and feet. Two copies of it run side by side,
   standing and lying, and the body cross-fades between them, so sitting down
   doesn't make her jump.
4. Unity puts the body on her bones:
   - hips and spine to Layable's trackers
   - legs by two-bone IK to its ankles, feet never below the ground
   - arms stay the Animator's, so gestures still play

   The film records all of it.

If Layable isn't running, Unity starts it (`python3 -m layable.puppet`). If
that fails too, her body fades back to the plain Animator pose and the
session carries on.

**Who picks the postures:**
- Scripted lines, with a `"posture"` field in `hexe_scenario.json` or a
  `[p:SitGround]` tag in a coach line.
- The model: the witch's improvised answers and the coach's reactions carry a
  `"posture"` field.

## Running it by hand

```bash
cd MediaLabAI/Layable
python3 -m layable.puppet          # what Unity starts
python3 -m layable.viewer          # http://localhost:8766, Live headset view shows her body
python3 tests/test_puppet.py       # does she move like a person? (feet, steps, postures)
python3 tests/test_estimator.py    # Layable's own checks
```

You need Python 3.11 or newer. numpy is only needed for the learned model.

## Settings

`~/.config/layable-medialab/config.toml` is written on the first run, and
changes apply live. The defaults differ from the VRChat copy:

- No controllers, no hip push and no T-pose calibration.
- `recenter_seconds = 2`, so her body squares up to where she looks, the way
  a person turns towards someone.
- The learned model is off.

The head poses for each posture are in `layable/posture.py` (`target()`).
Unity sends her height, measured from her eye height, with every frame.

## The learned model

The model the VRChat copy uses (`layable/models/body.npz`) was trained on
Ubisoft LAFAN1, which is CC BY-NC-ND. It must not be shared, and this repo is
public, so `Layable/layable/models/` is in `.gitignore`. To try it locally:

1. Copy the file there.
2. Set `use_model = true`.

On this character it keeps her feet even stiller while standing. But it gets
her crouch wrong, and `tests/test_puppet.py --model` shows that. So the rules
are the default.

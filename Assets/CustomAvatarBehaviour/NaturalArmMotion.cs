using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Her arms and hands, the way a person holds and uses them.
//
// At rest a relaxed arm is never straight: the elbow keeps a slight bend, the palm turns in
// towards the thigh, and the fingers curl a little more from the index to the little finger.
// People also don't hold one rest for long - hands hang at the sides for a while, then come
// together in front, then drop again.
//
// While talking, the hands come up into gesture space (in front of the lower chest) and
// beat: a short drop of the hand with a flick of the wrist, landing on the stressed syllable.
// The stroke of a real beat coincides with the pitch accent or slightly precedes it, so the
// strokes are planned from the whole utterance's loudness and pitch curves (SAiD delivers
// them before playback) and started ahead of the syllable rather than reacting to it.
// Not every sentence gets the same hands: some are told with both, some with one, a few
// with hardly any movement at all.
//
// Placed by inverse kinematics in body space, in arm lengths, so a pose means the same on any
// humanoid; the elbow bends about the rig's own elbow axis and the fingers curl about their
// own, both measured once at start through Unity's muscle space - so nothing here depends on
// how the avatar's bones happen to be oriented.
//
// Runs after BreathingMotion (it moves the chest the arms hang from) and after LayableBody.
// Authored gestures (the Animator's hand layers) take over an arm while they play: whoever
// plays them sets LeftGesture/RightGesture, and this layer steps aside by that much.
[DefaultExecutionOrder(-15)]
public class NaturalArmMotion : MonoBehaviour
{
    [Header("Blend")]
    [Range(0f, 1f)] public float Weight = 1f;
    [Tooltip("Seconds to take over the arms at start and to give them back when disabled.")]
    public float FadeSeconds = 0.5f;
    // How much of each arm an authored gesture clip has right now (0-1), set every frame by
    // whoever plays them (AvatarPerformer).
    [NonSerialized] public float LeftGesture, RightGesture;
    [Range(0f, 1f)] [Tooltip("0 the coach, 1 the witch: hands held in front like claws, smaller beats.")]
    public float WitchStyle;

    [Header("Rest")]
    [Tooltip("Elbow bend of a relaxed hanging arm, in degrees. Straight arms are the first thing that reads as a doll.")]
    public float RestElbowBend = 16f;
    [Tooltip("How far the hanging arms stand out from the sides, in degrees, so they clear hips and skirt.")]
    public float RestAbduction = 8f;
    [Tooltip("How far the hanging arms swing forward of straight down, in degrees.")]
    public float RestForward = 5f;
    [Tooltip("Hands loosely together in front, from the shoulder, in arm lengths: x outwards, y up, z forwards.")]
    public Vector3 FrontRestHand = new Vector3(-0.16f, -0.78f, 0.26f);
    [Tooltip("Seconds between changes of rest (at the sides, together in front).")]
    public Vector2 RestChangeSeconds = new Vector2(12f, 30f);

    [Header("Talking")]
    [Tooltip("Where the hands go while she talks, from the shoulder, in arm lengths: x outwards, y up, z forwards.")]
    public Vector3 TalkHand = new Vector3(-0.06f, -0.62f, 0.42f);
    [Tooltip("The witch's hands, held before her like claws.")]
    public Vector3 WitchHand = new Vector3(-0.12f, -0.56f, 0.36f);
    [Tooltip("Seconds the hands stay up after she stops talking.")]
    public float TalkHold = 1.1f;
    [Range(0f, 2f)] public float Expressiveness = 1f;

    [Header("Beats (strokes on stressed syllables)")]
    [Tooltip("How far the hand drops on a stroke, in arm lengths.")]
    public float BeatDrop = 0.06f;
    [Tooltip("Wrist flick on a stroke, in degrees.")]
    public float BeatWrist = 14f;
    [Tooltip("Fewest seconds between two strokes.")]
    public float MinBeatGap = 0.55f;
    [Tooltip("Seconds the stroke's apex comes before the syllable's loudness peak: speakers' strokes lead the stressed vowel " +
             "by about 60 ms, and the hand's own follow-through lags its target by about 40 ms.")]
    public float StrokeLead = 0.1f;

    [Header("Motion")]
    [Tooltip("How quickly the hands follow a change of pose (Hz) and how much they settle without overshoot (1 = none).")]
    public float Frequency = 1.3f;
    public float Damping = 0.9f;
    public float BeatFrequency = 4.5f;
    public float BeatDamping = 0.55f;
    [Tooltip("Slow wandering of the hands, in metres.")]
    public float Drift = 0.012f;
    [Tooltip("How far the hanging hands lag behind when her body moves (pendulum), in metres per m/s^2.")]
    public float Swing = 0.01f;
    [Range(0f, 1f)] [Tooltip("Share of the palm's turn done by the forearm; the wrist does the rest.")]
    public float ForearmTwistShare = 0.6f;
    [Tooltip("Furthest the hand bends away from the forearm, in degrees.")]
    public float MaxWristBend = 60f;
    [Tooltip("How quickly the hands follow a named gesture (Hz) and how much they settle without overshoot.")]
    public float GestureFrequency = 4f;
    public float GestureDamping = 0.75f;

    [Header("Fingers")]
    public bool DriveFingers = true;

    // ---------------------------------------------------------------- state

    private class Dynamics
    {
        // Second-order system (frequency, damping): what a hand does when it is asked to go
        // somewhere - accelerate, arrive, settle - instead of snapping or sliding linearly.
        private Vector3 y, yd, xp;
        private float k1, k2;
        public void Init(float f, float z, Vector3 x0)
        {
            Tune(f, z);
            y = xp = x0;
            yd = Vector3.zero;
        }
        public void Tune(float f, float z)
        {
            f = Mathf.Max(0.05f, f);
            k1 = z / (Mathf.PI * f);
            k2 = 1f / ((2f * Mathf.PI * f) * (2f * Mathf.PI * f));
        }
        public Vector3 Step(float dt, Vector3 x)
        {
            if (dt <= 0f) return y;
            xp = x;
            var k2s = Mathf.Max(k2, 1.1f * (dt * dt / 4f + dt * k1 / 2f));
            y += dt * yd;
            yd += dt * (x - y - k1 * yd) / k2s;
            return y;
        }
    }

    private class Arm
    {
        public int Side;                     // +1 right, -1 left
        public Transform Clavicle, Upper, Lower, Hand;
        public float A, B;                   // upper arm and forearm lengths
        public Vector3 HingeLocal;           // elbow flexion axis, in the frame of the forearm's parent
        public Vector3 FingersLocal, PalmLocal;
        public Transform[] Fingers = Array.Empty<Transform>();
        public Quaternion[] Relaxed, Open, Curled;
        public Quaternion[][] Shapes;        // by ArmGestures.Shape
        public readonly Dynamics Pose = new Dynamics(), Beat = new Dynamics();
        public bool Ready, Started;
        public float Engage, Share = 1f;
        public float BeatApex = -99f, BeatStrength;
        public float Seed;
        public Quaternion[] Saved = new Quaternion[3];
    }

    private Animator animator;
    private Transform root, hips, chest, neck, head, leftEye, rightEye;
    private Vector3 headUpLocal, headForwardLocal, eyesLocal;
    private Arm left, right;
    private ArmGestures.Gesture gesture;
    private float gestureStart = -99f;
    private float fade;
    private float restVariant, restTarget, nextRestChange;
    private float lastSpeech = -99f;
    private float utteranceEngage = 1f;
    private float palmUpUntil = -1f, palmUp;
    private Vector3 lastHips, hipsVelocity, hipsAccel;
    private float[] plannedFor;
    private List<BeatPlanner.Beat> beats = new List<BeatPlanner.Beat>();
    private int nextBeat;

    // Seconds before the apex a stroke starts (its preparation): what the planner looks ahead.
    private const float BeatLead = 0.32f;

    // ---------------------------------------------------------------- setup

    private void Start()
    {
        animator = GetComponent<Animator>();
        if (animator == null || !animator.isHuman || animator.avatar == null)
        {
            enabled = false;
            return;
        }
        root = animator.transform;
        hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest) ?? animator.GetBoneTransform(HumanBodyBones.Spine);
        neck = animator.GetBoneTransform(HumanBodyBones.Neck) ?? animator.GetBoneTransform(HumanBodyBones.Head);
        right = MakeArm(+1);
        left = MakeArm(-1);
        if (hips == null || chest == null || neck == null || right == null || left == null)
        {
            Debug.LogWarning("NaturalArmMotion: the avatar is missing arm or spine bones - arms stay the Animator's.");
            enabled = false;
            return;
        }
        Measure();

        // The head's own up and forward, taken while she faces the way her body does, and where
        // her eyes are if the rig has no eye bones: the face landmarks gestures reach for.
        head = animator.GetBoneTransform(HumanBodyBones.Head);
        leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
        rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
        if (head != null)
        {
            var up = (neck.position - hips.position).normalized;
            var side = Vector3.ProjectOnPlane(right.Upper.position - left.Upper.position, up).normalized;
            var forward = Vector3.Cross(side, up);
            headUpLocal = Quaternion.Inverse(head.rotation) * up;
            headForwardLocal = Quaternion.Inverse(head.rotation) * forward;
            var scale = (right.A + right.B) / 0.53f;
            eyesLocal = head.InverseTransformPoint(head.position + up * (0.08f * scale) + forward * (0.07f * scale));
        }
        lastHips = hips.position;
        nextRestChange = Time.time + UnityEngine.Random.Range(RestChangeSeconds.x, RestChangeSeconds.y);
    }

    private Arm MakeArm(int side)
    {
        Transform Bone(HumanBodyBones l, HumanBodyBones r) => animator.GetBoneTransform(side > 0 ? r : l);
        var arm = new Arm
        {
            Side = side,
            Clavicle = Bone(HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder),
            Upper = Bone(HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm),
            Lower = Bone(HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm),
            Hand = Bone(HumanBodyBones.LeftHand, HumanBodyBones.RightHand),
            Seed = UnityEngine.Random.Range(0f, 100f),
        };
        if (arm.Upper == null || arm.Lower == null || arm.Hand == null) return null;
        arm.A = Vector3.Distance(arm.Upper.position, arm.Lower.position);
        arm.B = Vector3.Distance(arm.Lower.position, arm.Hand.position);

        // The hand's own axes: fingers point from the wrist to the middle finger, and the palm
        // faces away from the back of the hand. Index and little finger bases give its plane
        // (for a right hand index x little points out of the back of the hand, for a left one
        // out of the palm). Positions inside the hand don't change with the pose.
        var index = Bone(HumanBodyBones.LeftIndexProximal, HumanBodyBones.RightIndexProximal);
        var middle = Bone(HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightMiddleProximal);
        var little = Bone(HumanBodyBones.LeftLittleProximal, HumanBodyBones.RightLittleProximal);
        var toFingers = (middle != null ? middle.position : arm.Hand.position + (arm.Hand.position - arm.Lower.position) * 0.3f) - arm.Hand.position;
        var palm = index != null && little != null
            ? -side * Vector3.Cross(index.position - arm.Hand.position, little.position - arm.Hand.position)
            : Vector3.zero;
        if (palm.sqrMagnitude < 1e-10f) palm = Vector3.Cross(toFingers, Vector3.up) * -side;
        arm.FingersLocal = Quaternion.Inverse(arm.Hand.rotation) * toFingers.normalized;
        arm.PalmLocal = Quaternion.Inverse(arm.Hand.rotation) * Vector3.ProjectOnPlane(palm, toFingers).normalized;

        var bones = new List<Transform>();
        var first = side > 0 ? HumanBodyBones.RightThumbProximal : HumanBodyBones.LeftThumbProximal;
        for (var i = 0; i < 15; i++)
        {
            var b = animator.GetBoneTransform((HumanBodyBones)((int)first + i));
            bones.Add(b);
        }
        arm.Fingers = bones.ToArray();
        return arm;
    }

    // Unity's muscle space knows how this rig's elbows and fingers bend; ask it once. Each
    // muscle is moved, the bone's turn is read off, and the pose is put back.
    private void Measure()
    {
        using var handler = new HumanPoseHandler(animator.avatar, root);
        var pose = new HumanPose();
        handler.GetHumanPose(ref pose);
        var original = pose.muscles.ToArray();
        var names = HumanTrait.MuscleName;
        int M(string name) => Array.IndexOf(names, name);
        void Apply() => handler.SetHumanPose(ref pose);

        foreach (var arm in new[] { left, right })
        {
            var side = arm.Side > 0 ? "Right" : "Left";
            var stretch = M(side + " Forearm Stretch");
            if (stretch >= 0)
            {
                pose.muscles[stretch] = 0.6f; Apply();
                var straighter = arm.Lower.localRotation;
                pose.muscles[stretch] = -0.4f; Apply();
                var bent = arm.Lower.localRotation;
                // Local rotations are relative to the parent, so this turn is in the parent's frame.
                (bent * Quaternion.Inverse(straighter)).ToAngleAxis(out var angle, out var axis);
                if (angle > 180f) { angle = 360f - angle; axis = -axis; }
                if (angle > 1f && !float.IsNaN(axis.x)) arm.HingeLocal = axis.normalized;
                pose.muscles[stretch] = original[stretch];
            }

            // Finger presets, in the rig's muscle values (-1 curled, +1 straight). The cascade -
            // more curl towards the little finger - is what reads as a hand at rest.
            arm.Relaxed = FingerPreset(handler, ref pose, side, new[,] { { 0.1f, 0f, -0.1f }, { -0.15f, -0.3f, -0.2f }, { -0.25f, -0.4f, -0.25f }, { -0.35f, -0.5f, -0.3f }, { -0.45f, -0.55f, -0.35f } }, -0.1f, 0.1f, 0f);
            arm.Open = FingerPreset(handler, ref pose, side, new[,] { { 0.3f, 0.2f, 0.1f }, { 0.15f, 0f, -0.05f }, { 0.05f, -0.1f, -0.1f }, { -0.05f, -0.2f, -0.15f }, { -0.15f, -0.25f, -0.2f } }, 0.1f, 0.3f, 0.2f);
            arm.Curled = FingerPreset(handler, ref pose, side, new[,] { { -0.2f, -0.3f, -0.4f }, { -0.3f, -0.75f, -0.7f }, { -0.4f, -0.8f, -0.75f }, { -0.5f, -0.85f, -0.75f }, { -0.6f, -0.85f, -0.8f } }, 0.2f, 0.4f, 0f);
            // For the named gestures: a flat open hand (presenting, clapping), a pointing index
            // with the thumb over the curled fingers, and the thinker's hand (thumb under the
            // chin, index along the jaw, the rest curled).
            var flat = FingerPreset(handler, ref pose, side, new[,] { { 0.5f, 0.6f, 0.6f }, { 0.9f, 0.9f, 0.9f }, { 0.9f, 0.9f, 0.9f }, { 0.85f, 0.85f, 0.85f }, { 0.8f, 0.8f, 0.8f } }, 0.2f, 0.4f, 0.3f);
            var point = FingerPreset(handler, ref pose, side, new[,] { { -0.3f, -0.4f, -0.3f }, { 0.9f, 0.9f, 0.9f }, { -0.8f, -0.9f, -0.8f }, { -0.85f, -0.9f, -0.8f }, { -0.85f, -0.9f, -0.8f } }, 0f, 0f, -0.2f);
            var think = FingerPreset(handler, ref pose, side, new[,] { { 0.3f, 0.1f, 0f }, { 0f, -0.3f, -0.2f }, { -0.6f, -0.85f, -0.7f }, { -0.65f, -0.85f, -0.7f }, { -0.7f, -0.85f, -0.7f } }, 0f, 0f, 0.3f);
            arm.Shapes = new[] { arm.Relaxed, arm.Open, flat, point, think, arm.Curled };
            arm.Ready = true;
        }

        Array.Copy(original, pose.muscles, original.Length);
        Apply();
        if (right.HingeLocal == Vector3.zero || left.HingeLocal == Vector3.zero)
            Debug.LogWarning("NaturalArmMotion: couldn't measure the elbow axis - elbows bend wherever the target puts them.");
    }

    private static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Little" };

    private Quaternion[] FingerPreset(HumanPoseHandler handler, ref HumanPose pose, string side, float[,] stretched, float indexSpread, float littleSpread, float thumbSpread)
    {
        var names = HumanTrait.MuscleName;
        int M(string name) => Array.IndexOf(names, name);
        var muscles = pose.muscles;
        var saved = muscles.ToArray();
        for (var f = 0; f < 5; f++)
        {
            for (var j = 0; j < 3; j++)
            {
                var m = M($"{side} {FingerNames[f]} {j + 1} Stretched");
                if (m >= 0) muscles[m] = stretched[f, j];
            }
        }
        void Spread(string finger, float v) { var m = M($"{side} {finger} Spread"); if (m >= 0) muscles[m] = v; }
        Spread("Index", indexSpread);
        Spread("Little", littleSpread);
        Spread("Thumb", thumbSpread);
        Spread("Middle", 0f);
        Spread("Ring", 0f);
        handler.SetHumanPose(ref pose);
        var arm = side == "Right" ? right : left;
        var result = arm.Fingers.Select(b => b != null ? b.localRotation : Quaternion.identity).ToArray();
        Array.Copy(saved, pose.muscles, saved.Length);
        return result;
    }

    // ---------------------------------------------------------------- cues

    // A question or an offer: the palms turn up and the hands part, for this long.
    public void Present(float seconds) => palmUpUntil = Time.time + Mathf.Max(0.3f, seconds);

    // Plays a named gesture (ArmGestures.All) with the arm IK. False if it isn't one, or the
    // layer isn't running, so the caller can fall back to an Animator clip.
    public bool PlayGesture(string name)
    {
        if (!isActiveAndEnabled || left == null || !left.Ready || string.IsNullOrEmpty(name) || !ArmGestures.All.TryGetValue(name, out var g)) return false;
        gesture = g;
        gestureStart = Time.time;
        return true;
    }

    public bool IsGesturing => gesture != null && Time.time - gestureStart < gesture.End;

    // ---------------------------------------------------------------- per frame

    private void LateUpdate()
    {
        if (!left.Ready || !right.Ready) return;
        var dt = Time.deltaTime;
        fade = Mathf.MoveTowards(fade, Weight, dt / Mathf.Max(0.05f, FadeSeconds));
        if (fade <= 0f) return;

        // Body frame: up the spine, across the shoulders, forward out of the chest.
        var up = (neck.position - hips.position).normalized;
        var across = right.Upper.position - left.Upper.position;
        var side = Vector3.ProjectOnPlane(across, up).normalized;
        var forward = Vector3.Cross(side, up);
        var uprightness = Mathf.Clamp01(Vector3.Dot(up, Vector3.up));

        // The body's acceleration, for the pendulum lag of hanging hands.
        if (dt > 1e-4f)
        {
            var v = (hips.position - lastHips) / dt;
            hipsAccel = Vector3.Lerp(hipsAccel, (v - hipsVelocity) / dt, 1f - Mathf.Exp(-dt / 0.15f));
            hipsVelocity = v;
            lastHips = hips.position;
        }

        UpdateSpeech();
        UpdateRest();
        palmUp = Mathf.MoveTowards(palmUp, Time.time < palmUpUntil ? 1f : 0f, dt / 0.35f);

        var body = new ArmGestures.Body
        {
            Up = up, Right = side, Forward = forward,
            LeftShoulder = left.Upper.position, RightShoulder = right.Upper.position,
            Scale = (right.A + right.B) / 0.53f,
        };
        if (head != null)
        {
            body.HeadUp = (head.rotation * headUpLocal).normalized;
            body.HeadForward = (head.rotation * headForwardLocal).normalized;
            body.HeadRight = Vector3.Cross(body.HeadUp, body.HeadForward).normalized;
            body.Eyes = leftEye != null && rightEye != null ? (leftEye.position + rightEye.position) * 0.5f : head.TransformPoint(eyesLocal);
        }
        else
        {
            body.HeadUp = up; body.HeadForward = forward; body.HeadRight = side;
            body.Eyes = neck.position + up * (0.12f * body.Scale) + forward * (0.07f * body.Scale);
        }
        if (gesture != null && Time.time - gestureStart >= gesture.End) gesture = null;

        foreach (var arm in new[] { right, left })
        {
            var clip = arm.Side > 0 ? RightGesture : LeftGesture;
            var w = fade * Mathf.Clamp01(1f - clip);
            if (w <= 0.001f) continue;
            PoseArm(arm, dt, up, side * arm.Side, forward, uprightness, w, body);
        }
    }

    private void UpdateRest()
    {
        if (Time.time >= nextRestChange)
        {
            // Sides most of the time, together in front now and then.
            restTarget = restTarget > 0.5f ? 0f : (UnityEngine.Random.value < 0.45f ? 1f : 0f);
            nextRestChange = Time.time + UnityEngine.Random.Range(RestChangeSeconds.x, RestChangeSeconds.y);
        }
        restVariant = Mathf.MoveTowards(restVariant, restTarget, Time.deltaTime / 1.2f);
    }

    private void UpdateSpeech()
    {
        var said = SaidBlendshapePlayer.Instance;
        if (said != null && said.IsSpeaking) lastSpeech = Time.time;
        var talking = Time.time - lastSpeech < TalkHold;

        if (said != null && said.TryGetPlayback(out var loud, out var f0, out var voiced, out var fps, out var elapsed))
        {
            if (!ReferenceEquals(loud, plannedFor))
            {
                plannedFor = loud;
                beats = BeatPlanner.Find(loud, f0, voiced, fps, MinBeatGap, BeatLead + StrokeLead);
                nextBeat = 0;
                NewUtterance();
            }
            while (nextBeat < beats.Count && beats[nextBeat].Time - StrokeLead - elapsed <= BeatLead)
            {
                var b = beats[nextBeat++];
                Fire(Time.time + Mathf.Max(0f, b.Time - StrokeLead - elapsed), b.Strength);
            }
        }

        foreach (var arm in new[] { right, left })
        {
            var want = talking ? utteranceEngage * arm.Share : 0f;
            arm.Engage = Mathf.MoveTowards(arm.Engage, want, Time.deltaTime / (want > arm.Engage ? 0.35f : 0.9f));
        }
    }

    // A new sentence: how much it is told with the hands, and with which.
    private void NewUtterance()
    {
        var r = UnityEngine.Random.value;
        utteranceEngage = r < 0.15f ? 0.25f : UnityEngine.Random.Range(0.6f, 1f);
        var hands = UnityEngine.Random.value;
        if (hands < 0.5f) { right.Share = 1f; left.Share = 1f; }
        else if (hands < 0.85f) { right.Share = 1f; left.Share = 0.35f; }
        else { right.Share = 0.35f; left.Share = 1f; }
    }

    private void Fire(float apex, float strength)
    {
        // The hand that is up the most takes the beat; a strong one takes both.
        var both = strength > 0.85f && left.Engage > 0.5f && right.Engage > 0.5f;
        var main = right.Engage * right.Share >= left.Engage * left.Share ? right : left;
        foreach (var arm in both ? new[] { right, left } : new[] { main })
        {
            if (Time.time < arm.BeatApex + 0.08f) continue;   // still in the last stroke
            arm.BeatApex = apex;
            arm.BeatStrength = strength;
        }
    }

    // Preparation (the hand lifts a little), stroke (down to the apex), a short hold, retraction.
    private static float BeatEnvelope(float t)
    {
        float S(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        if (t < -BeatLead) return 0f;
        if (t < -0.14f) return -0.35f * S((t + BeatLead) / (BeatLead - 0.14f));
        if (t < 0f) return Mathf.Lerp(-0.35f, 1f, S((t + 0.14f) / 0.14f));
        if (t < 0.07f) return 1f;
        if (t < 0.55f) return 1f - S((t - 0.07f) / 0.48f);
        return 0f;
    }

    private void PoseArm(Arm arm, float dt, Vector3 up, Vector3 outward, Vector3 forward, float uprightness, float weight, ArmGestures.Body body)
    {
        // A named gesture this arm takes part in, and how far into it.
        var gs = default(ArmGestures.Sample);
        var gw = 0f;
        if (gesture != null && (gesture.BothHands || arm.Side > 0))
        {
            gs = ArmGestures.Evaluate(gesture, Time.time - gestureStart);
            gw = gs.Weight;
        }
        // A shrug lifts the shoulder first: the arm hangs from it.
        if (gw > 0f && gs.Shoulder != 0f && arm.Clavicle != null)
        {
            arm.Clavicle.rotation = Quaternion.AngleAxis(gs.Shoulder * gw * weight, Vector3.Cross(outward, up)) * arm.Clavicle.rotation;
        }

        var shoulder = arm.Upper.position;
        var length = arm.A + arm.B;
        Vector3 Local(Vector3 world) => new Vector3(Vector3.Dot(world, outward), Vector3.Dot(world, up), Vector3.Dot(world, forward)) / length;
        Vector3 World(Vector3 local) => (outward * local.x + up * local.y + forward * local.z) * length;
        Vector3 Dir(Vector3 local) => (outward * local.x + up * local.y + forward * local.z).normalized;

        // Hanging: with gravity while she is upright, along the body when she lies down.
        var g = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.85f, uprightness));
        var hang = (Vector3.down * g - up * (1f - g)).normalized;
        hang = (hang + outward * Mathf.Tan(RestAbduction * Mathf.Deg2Rad) + forward * Mathf.Tan(RestForward * Mathf.Deg2Rad)).normalized;
        var reach = Mathf.Sqrt(arm.A * arm.A + arm.B * arm.B + 2f * arm.A * arm.B * Mathf.Cos(RestElbowBend * Mathf.Deg2Rad));
        var sides = Local(hang * reach);

        // Where the hand wants to be, blended from the poses, in arm lengths from the shoulder.
        var witch = WitchStyle;
        var rest = Vector3.Lerp(Vector3.Lerp(sides, FrontRestHand, restVariant), WitchHand, witch);
        var talk = Vector3.Lerp(TalkHand, WitchHand + new Vector3(0f, 0.04f, 0.04f), witch) + new Vector3(0.1f, 0.03f, 0.02f) * palmUp;
        var engage = arm.Engage * Mathf.Lerp(0.4f, 1f, uprightness);
        var target = Vector3.Lerp(rest, talk, engage);

        // Slow wandering, a little different for each hand.
        var t = Time.time * 0.22f;
        target += new Vector3(Mathf.PerlinNoise(arm.Seed, t) - 0.5f, Mathf.PerlinNoise(arm.Seed + 7f, t) - 0.5f, Mathf.PerlinNoise(arm.Seed + 13f, t) - 0.5f) * (2f * Drift / length * (1f - 0.7f * gw));

        // The gesture's hand, on her body as it is this frame.
        if (gw > 0f) target = Vector3.Lerp(target, Local(ArmGestures.Target(gesture, gs, body, arm.Side, length) - shoulder), gw);

        if (!arm.Started)
        {
            arm.Pose.Init(Frequency, Damping, target);
            arm.Beat.Init(BeatFrequency, BeatDamping, Vector3.zero);
            arm.Started = true;
        }
        // A gesture is quicker than drifting between rests: a wave swings twice a second.
        arm.Pose.Tune(Mathf.Lerp(Frequency, GestureFrequency, gw), Mathf.Lerp(Damping, GestureDamping, gw));
        var smooth = arm.Pose.Step(dt, target);

        // The beat: a drop of the hand, a flick of the wrist, the fingers opening a touch.
        // Not on top of a named gesture.
        var e = BeatEnvelope(Time.time - arm.BeatApex) * arm.BeatStrength * Expressiveness * Mathf.Lerp(1f, 0.6f, witch) * (1f - gw);
        var lift = Mathf.Lerp(0.35f, 1f, engage);   // from the rest a beat is small
        arm.Beat.Tune(BeatFrequency, BeatDamping);
        var beat = arm.Beat.Step(dt, new Vector3(0f, -BeatDrop, 0.25f * BeatDrop) * e * lift);
        var flick = e * BeatWrist * lift;

        var hand = shoulder + World(smooth + beat) - Vector3.ClampMagnitude(hipsAccel * (Swing * (1f - Mathf.Max(engage, gw))), 0.05f);
        hand.y = Mathf.Max(hand.y, root.position.y + 0.05f);

        // Elbows point back at rest and out and down while gesturing.
        var pole = Dir(Vector3.Lerp(Vector3.Lerp(new Vector3(0.3f, -0.1f, -1f), new Vector3(0.8f, -0.3f, -0.6f), restVariant), new Vector3(0.9f, -0.5f, -0.35f), Mathf.Max(engage, witch)));
        if (gw > 0f) pole = Vector3.Lerp(pole, ArmGestures.Direction(gs.Pole, body, arm.Side), gw).normalized;

        // The hand's turn: palm to the thigh at rest, palms facing and tilted up while talking,
        // palm up for a question, palm down for the witch's claws.
        var fingers = Vector3.Lerp(Vector3.Lerp(hang + forward * 0.12f, Dir(new Vector3(-0.8f, -0.2f, 0.6f)), restVariant), Dir(new Vector3(-0.25f, 0.1f, 1f)), engage);
        var palm = Vector3.Lerp(Vector3.Lerp(Dir(new Vector3(-0.95f, 0f, -0.3f)), Dir(new Vector3(-0.7f, 0.6f, 0f)), restVariant), Dir(new Vector3(-0.8f, 0.6f, 0f)), engage);
        fingers = Vector3.Lerp(fingers, Dir(new Vector3(-0.3f, -0.2f, 0.8f)), witch);
        palm = Vector3.Lerp(palm, Dir(new Vector3(-0.5f, -0.7f, 0.1f)), witch);
        fingers = Vector3.Lerp(fingers, Dir(new Vector3(0.3f, 0f, 1f)), palmUp * engage);
        palm = Vector3.Lerp(palm, up, palmUp * engage);
        if (gw > 0f)
        {
            fingers = Vector3.Lerp(fingers, ArmGestures.Direction(gs.Fingers, body, arm.Side), gw);
            palm = Vector3.Lerp(palm, ArmGestures.Direction(gs.Palm, body, arm.Side), gw);
        }
        fingers = Vector3.ProjectOnPlane(fingers, palm).normalized;
        palm = palm.normalized;
        var lateral = Vector3.Cross(fingers, palm);
        if (Mathf.Abs(flick) > 0.01f && lateral.sqrMagnitude > 1e-6f)
        {
            var q = Quaternion.AngleAxis(flick, lateral.normalized);
            fingers = q * fingers;
            palm = q * palm;
        }

        // What the Animator gave this arm, to blend from.
        var bones = new[] { arm.Upper, arm.Lower, arm.Hand };
        for (var i = 0; i < 3; i++) arm.Saved[i] = bones[i].localRotation;
        var fingerSaved = arm.Fingers.Select(b => b != null ? b.localRotation : Quaternion.identity).ToArray();

        Solve(arm, hand, pole, fingers, palm);

        for (var i = 0; i < 3; i++) bones[i].localRotation = Quaternion.Slerp(arm.Saved[i], bones[i].localRotation, weight);

        if (DriveFingers && arm.Relaxed != null)
        {
            var open = Mathf.Clamp01(engage * 0.45f + Mathf.Max(0f, e) * 0.35f + palmUp * engage * 0.4f);
            for (var i = 0; i < arm.Fingers.Length; i++)
            {
                var b = arm.Fingers[i];
                if (b == null) continue;
                var q = Quaternion.Slerp(arm.Relaxed[i], arm.Open[i], open);
                q = Quaternion.Slerp(q, arm.Curled[i], witch * 0.7f);
                if (gw > 0f)
                {
                    var shape = Quaternion.Slerp(arm.Shapes[(int)gs.ShapeA][i], arm.Shapes[(int)gs.ShapeB][i], gs.ShapeMix);
                    q = Quaternion.Slerp(q, shape, gw);
                }
                b.localRotation = Quaternion.Slerp(fingerSaved[i], q, weight);
            }
        }
    }

    // Two-bone IK: the elbow where the pole says, bending about the rig's own elbow axis, then
    // the palm turned partly by the forearm and the rest at the wrist.
    private void Solve(Arm arm, Vector3 target, Vector3 pole, Vector3 fingers, Vector3 palm)
    {
        var s = arm.Upper.position;
        var toTarget = target - s;
        var d = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(arm.A - arm.B) + 1e-3f, arm.A + arm.B - 1e-4f);
        var dir = toTarget.sqrMagnitude > 1e-8f ? toTarget.normalized : Vector3.down;
        var bend = Vector3.ProjectOnPlane(pole, dir);
        if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(Vector3.back, dir);
        bend.Normalize();
        var x = (arm.A * arm.A - arm.B * arm.B + d * d) / (2f * d);
        var h = Mathf.Sqrt(Mathf.Max(arm.A * arm.A - x * x, 0f));
        var elbow = s + dir * x + bend * h;
        var wrist = s + dir * d;

        // Upper arm: point at the elbow, then turn about itself so the elbow's hinge is the one
        // that bends the forearm towards the wrist.
        arm.Upper.rotation = Quaternion.FromToRotation(arm.Lower.position - s, elbow - s) * arm.Upper.rotation;
        var u = (elbow - s).normalized;
        var want = Vector3.Cross(u, (wrist - elbow).normalized);
        if (arm.HingeLocal != Vector3.zero && want.sqrMagnitude > 1e-6f)
        {
            // Measured in the forearm's parent: the upper arm, or a twist bone under it.
            var have = Vector3.ProjectOnPlane(arm.Lower.parent.rotation * arm.HingeLocal, u);
            var wantP = Vector3.ProjectOnPlane(want, u);
            if (have.sqrMagnitude > 1e-6f) arm.Upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(have, wantP, u), u) * arm.Upper.rotation;
        }

        // Forearm: point at the wrist.
        var e = arm.Lower.position;
        arm.Lower.rotation = Quaternion.FromToRotation(arm.Hand.position - e, wrist - e) * arm.Lower.rotation;

        // Wrist: no further from the forearm's line than a wrist bends.
        var fa = (arm.Hand.position - e).normalized;
        if (Vector3.Angle(fa, fingers) > MaxWristBend)
        {
            var limited = Vector3.RotateTowards(fa, fingers, MaxWristBend * Mathf.Deg2Rad, 0f);
            var q = Quaternion.FromToRotation(fingers, limited);
            fingers = limited;
            palm = q * palm;
        }
        var handRot = Quaternion.LookRotation(fingers, palm) * Quaternion.Inverse(Quaternion.LookRotation(arm.FingersLocal, arm.PalmLocal));

        // Forearm twist (pronation) takes its share of the palm's turn, about its own axis.
        var palmNow = Vector3.ProjectOnPlane(arm.Hand.rotation * arm.PalmLocal, fa);
        var palmWant = Vector3.ProjectOnPlane(handRot * arm.PalmLocal, fa);
        if (palmNow.sqrMagnitude > 1e-6f && palmWant.sqrMagnitude > 1e-6f)
        {
            arm.Lower.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(palmNow, palmWant, fa) * ForearmTwistShare, fa) * arm.Lower.rotation;
        }
        arm.Hand.rotation = handRot;
    }
}

// Picks the syllables a speaker would beat on: the prominent loudness peaks of voiced speech,
// favouring those with a pitch accent, no closer together than a hand can stroke, and about
// one a second at most. Pure computation on the curves.
public static class BeatPlanner
{
    public struct Beat
    {
        public float Time;       // seconds into the utterance, the stroke's apex
        public float Strength;   // 0.5-1
    }

    public static List<Beat> Find(float[] loudness, float[] f0, float[] voiced, float fps, float minGap, float earliest)
    {
        var result = new List<Beat>();
        if (loudness == null || loudness.Length < 8 || fps <= 0f) return result;
        var n = loudness.Length;

        // About 80 ms of smoothing: syllable nuclei, not the flicker inside them.
        var half = Mathf.Max(1, Mathf.RoundToInt(0.04f * fps));
        var s = new float[n];
        for (var i = 0; i < n; i++)
        {
            float sum = 0f; var c = 0;
            for (var k = Mathf.Max(0, i - half); k <= Mathf.Min(n - 1, i + half); k++) { sum += loudness[k]; c++; }
            s[i] = sum / c;
        }

        var medianF0 = 0.5f;
        if (f0 != null && f0.Length == n)
        {
            var vals = Enumerable.Range(0, n).Where(i => voiced == null || voiced.Length != n || voiced[i] > 0.5f).Select(i => f0[i]).OrderBy(v => v).ToList();
            if (vals.Count > 0) medianF0 = vals[vals.Count / 2];
        }

        var window = Mathf.RoundToInt(0.25f * fps);
        var accent = Mathf.RoundToInt(0.06f * fps);
        var candidates = new List<(int Index, float Score)>();
        for (var i = 1; i < n - 1; i++)
        {
            if (!(s[i] >= s[i - 1] && s[i] > s[i + 1]) || s[i] < 0.15f) continue;
            if (voiced != null && voiced.Length == n && voiced[i] < 0.5f) continue;
            if (i / fps < earliest) continue;
            float minL = s[i], minR = s[i];
            for (var k = i - 1; k >= Mathf.Max(0, i - window) && s[k] <= s[i]; k--) minL = Mathf.Min(minL, s[k]);
            for (var k = i + 1; k <= Mathf.Min(n - 1, i + window) && s[k] <= s[i]; k++) minR = Mathf.Min(minR, s[k]);
            var prominence = s[i] - Mathf.Max(minL, minR);
            if (prominence < 0.08f) continue;
            var pitch = 0f;
            if (f0 != null && f0.Length == n)
            {
                for (var k = Mathf.Max(0, i - accent); k <= Mathf.Min(n - 1, i + accent); k++) pitch = Mathf.Max(pitch, f0[k] - medianF0);
            }
            candidates.Add((i, 0.6f * s[i] + 0.6f * prominence + 0.5f * Mathf.Max(0f, pitch)));
        }
        if (candidates.Count == 0) return result;

        // The stronger half, about one a second at most, never two within a stroke of each other.
        var scores = candidates.Select(c => c.Score).OrderBy(v => v).ToList();
        var floor = scores[Mathf.Clamp(Mathf.FloorToInt(scores.Count * 0.4f), 0, scores.Count - 1)];
        var max = scores[scores.Count - 1];
        var limit = Mathf.FloorToInt(n / fps / 0.8f) + 1;
        var chosen = new List<(int Index, float Score)>();
        foreach (var c in candidates.Where(c => c.Score >= floor).OrderByDescending(c => c.Score))
        {
            if (chosen.Count >= limit) break;
            if (chosen.Any(o => Mathf.Abs(o.Index - c.Index) / fps < minGap)) continue;
            chosen.Add(c);
        }
        foreach (var c in chosen.OrderBy(c => c.Index))
        {
            var strength = max > floor ? Mathf.Lerp(0.5f, 1f, (c.Score - floor) / (max - floor)) : 0.75f;
            result.Add(new Beat { Time = c.Index / fps, Strength = strength });
        }
        return result;
    }
}

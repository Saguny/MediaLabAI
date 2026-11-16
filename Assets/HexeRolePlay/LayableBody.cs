using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Her body, the way a real person would hold it: feet planted on the ground, the weight going
// from one foot to the other, a step when she turns round, and postures she chooses herself
// (leaning in, crouching, sitting on the ground, lying down).
//
// The body comes from Layable, the full-body estimator also used for VRChat. This is its own
// copy, in <project>/Layable, with its own settings and ports. It runs as a small Python
// process (layable.puppet, started here if it isn't running) that gets her animated head and
// the posture she wants about once a frame over local UDP, and answers with hip, chest, knee and
// foot poses. Here those are put on the humanoid bones:
//   hips      moved and turned to Layable's hip
//   spine     turned (spread over Spine, Chest, UpperChest) to Layable's chest
//   neck/head turned to the head Layable used (her own while standing)
//   legs      two-bone IK to Layable's ankles, knees bending towards its knees, feet never
//             below the ground
//   arms      left as the Animator made them, so gestures still play on top
//
// Runs in LateUpdate after the Animator and before BreathingMotion (-20) and NaturalHeadMotion
// (-10), which layer on top of this pose, and long before SceneRecorder (1000), so the film
// records it. It only writes bone rotations and the hips position, which is what the
// recording keeps.
//
// Without Layable (Python missing, process not answering) the body fades back to the Animator's
// within FadeSeconds - the session carries on as before.
[DefaultExecutionOrder(-30)]
public class LayableBody : MonoBehaviour
{
    public enum Posture { Stand, Lean, Crouch, SitGround, LieBack, LieSide }

    [Header("Layable process")]
    [Tooltip("Start layable.puppet if it doesn't answer.")]
    public bool AutoStart = true;
    public string PythonPath = "python3";
    [Tooltip("Folder holding the 'layable' package. Empty: <project>/Layable.")]
    public string LayableFolder = "";
    public int Port = 39580;

    [Header("Body")]
    [Tooltip("Let Layable pose her. Off: the Animator alone, as before.")]
    public bool Drive = true;
    [Range(0f, 1f)] [Tooltip("How much she shifts her weight and sways while standing.")]
    public float Drift = 1f;
    public float FadeSeconds = 0.4f;
    [Tooltip("Seconds without an answer before her body goes back to the Animator's.")]
    public float StaleSeconds = 0.5f;

    public Posture CurrentPosture { get; private set; } = Posture.Stand;
    public Posture GoalPosture { get; private set; } = Posture.Stand;
    // Moving between postures (or not there yet).
    public bool IsBusy => Weight > 0.01f && (busy || CurrentPosture != GoalPosture);
    public bool IsDriving => Weight > 0.5f;
    public float Weight { get; private set; }
    // Live head and chest height above her feet, smoothed, for the camera.
    public float HeadHeight { get; private set; }
    public float ChestHeight { get; private set; }

    private Animator animator;
    private Transform root, hips, spine, chest, upperChest, neck, head, leftEye, rightEye;
    private Leg left, right;
    private Transform[] spineChain;
    private Vector3 stagePos;
    private Quaternion stageRot = Quaternion.identity;

    private UdpClient udp;
    private IPEndPoint endpoint;
    private Process process;
    private int seq;
    private bool resetPending = true;
    private float lastReply = -100f, firstSend = -1f, startedAt = -100f;
    private bool warnedSilent, busy;
    private int calibrationFrames;
    private JObject reply;

    // calibration: how each bone sits on its Layable tracker, measured while she stands
    private bool calibrated;
    private Quaternion headToFacing = Quaternion.identity;
    private Vector3 eyeOffset;          // eyes from the head bone, in head space (no eye bones)
    private float eyeHeight = 1.4f;
    private float layableHeight = 1.5f;  // the height Layable builds her body for, metres
    private Offset hipOffset, chestOffset;

    private struct Offset
    {
        public Vector3 Pos;
        public Quaternion Rot;
        public static Offset Between(Vector3 tPos, Quaternion tRot, Vector3 bPos, Quaternion bRot)
        {
            var inv = Quaternion.Inverse(tRot);
            return new Offset { Pos = inv * (bPos - tPos), Rot = inv * bRot };
        }
        public void Apply(Vector3 tPos, Quaternion tRot, out Vector3 pos, out Quaternion rot)
        {
            pos = tPos + tRot * Pos;
            rot = tRot * Rot;
        }
    }

    private class Leg
    {
        public string Side;
        public Transform Upper, Lower, Foot;
        public float UpperLength, LowerLength;
        public Vector3 KneeForwardLocal;   // the way the knee points, in the thigh's space
        public Offset FootOffset;
        public float AnkleHeight;          // foot bone above the ground, standing
    }

    // ------------------------------------------------------------------ setup

    private void Awake()
    {
        animator = GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
        {
            Debug.LogWarning("LayableBody: needs a humanoid Animator - her body stays the Animator's.");
            enabled = false;
            return;
        }
        root = animator.transform;
        Transform Bone(HumanBodyBones b) => animator.GetBoneTransform(b);
        hips = Bone(HumanBodyBones.Hips);
        spine = Bone(HumanBodyBones.Spine);
        chest = Bone(HumanBodyBones.Chest);
        upperChest = Bone(HumanBodyBones.UpperChest);
        neck = Bone(HumanBodyBones.Neck);
        head = Bone(HumanBodyBones.Head);
        leftEye = Bone(HumanBodyBones.LeftEye);
        rightEye = Bone(HumanBodyBones.RightEye);
        left = MakeLeg("left", Bone(HumanBodyBones.LeftUpperLeg), Bone(HumanBodyBones.LeftLowerLeg), Bone(HumanBodyBones.LeftFoot));
        right = MakeLeg("right", Bone(HumanBodyBones.RightUpperLeg), Bone(HumanBodyBones.RightLowerLeg), Bone(HumanBodyBones.RightFoot));
        spineChain = Array.FindAll(new[] { spine, chest, upperChest }, t => t != null);
        if (hips == null || head == null || left == null || right == null || spineChain.Length == 0)
        {
            Debug.LogWarning("LayableBody: the rig is missing hips, spine, head or leg bones - her body stays the Animator's.");
            enabled = false;
            return;
        }
        // How high the ankle sits above the sole, from the rig itself: before the Animator has
        // run, the skeleton is in its bind pose, standing on the floor. (Not from the idle clip,
        // which could hold the feet a little off the ground.)
        foreach (var leg in new[] { left, right }) leg.AnkleHeight = leg.Foot.position.y - root.position.y;
    }

    private static Leg MakeLeg(string side, Transform upper, Transform lower, Transform foot)
    {
        if (upper == null || lower == null || foot == null) return null;
        return new Leg { Side = side, Upper = upper, Lower = lower, Foot = foot };
    }

    private void Start()
    {
        // A fixed stage space: where she stands at the start, facing the way she faces. Her
        // root can then turn (the costume change) and Layable sees her turn, and steps round.
        stagePos = root.position;
        stageRot = Quaternion.Euler(0f, root.eulerAngles.y, 0f);
        HeadHeight = head.position.y - root.position.y;
        ChestHeight = (chest != null ? chest : spineChain[spineChain.Length - 1]).position.y - root.position.y;

        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            udp.Client.Blocking = false;
            endpoint = new IPEndPoint(IPAddress.Loopback, Port);
        }
        catch (SocketException e)
        {
            Debug.LogWarning($"LayableBody: no UDP socket ({e.Message}) - her body stays the Animator's.");
            enabled = false;
        }
    }

    // ------------------------------------------------------------------ API

    public void SetPosture(Posture posture) => GoalPosture = posture;

    public bool TrySetPosture(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!Enum.TryParse(name, true, out Posture p) || !Enum.IsDefined(typeof(Posture), p)) return false;
        SetPosture(p);
        return true;
    }

    // A new session: standing, with a fresh body (feet planted where she is now).
    public void ResetBody()
    {
        GoalPosture = Posture.Stand;
        resetPending = true;
    }

    // ------------------------------------------------------------------ per frame

    private void LateUpdate()
    {
        if (udp == null) return;
        Receive();

        // What the Animator made of her this frame: her head is the input while she stands.
        var facing = head.rotation * headToFacing;
        var eyes = EyePoint();
        if (!calibrated) eyeHeight = Mathf.Max(0.5f, eyes.y - root.position.y);
        Send(eyes, facing);

        var fresh = reply != null && Time.unscaledTime - lastReply < StaleSeconds;
        if (!fresh) WarnIfSilent();
        if (fresh && !calibrated) Calibrate();

        var want = Drive && fresh && calibrated ? 1f : 0f;
        Weight = Mathf.MoveTowards(Weight, want, Time.deltaTime / Mathf.Max(0.05f, FadeSeconds));
        if (Weight > 0f && reply != null && calibrated) Pose(Weight);

        var k = 1f - Mathf.Exp(-Time.deltaTime / 0.3f);
        HeadHeight = Mathf.Lerp(HeadHeight, head.position.y - root.position.y, k);
        var c = chest != null ? chest : spineChain[spineChain.Length - 1];
        ChestHeight = Mathf.Lerp(ChestHeight, c.position.y - root.position.y, k);
    }

    private Vector3 EyePoint()
    {
        if (leftEye != null && rightEye != null) return (leftEye.position + rightEye.position) * 0.5f;
        return head.TransformPoint(eyeOffset);
    }

    private void Send(Vector3 eyes, Quaternion facing)
    {
        if (firstSend < 0f) firstSend = Time.unscaledTime;
        var frameRot = Quaternion.Euler(0f, root.eulerAngles.y, 0f);
        var height = Mathf.Clamp(eyeHeight / 0.927f * 100f, 100f, 250f);
        var msg = new JObject
        {
            ["seq"] = ++seq,
            ["dt"] = Time.deltaTime,
            ["eye"] = eyeHeight,
            ["posture"] = GoalPosture.ToString(),
            ["drift"] = calibrated ? Drift : 0f,
            ["head"] = PoseJson(ToStage(eyes), ToStage(facing)),
            ["frame"] = PoseJson(ToStage(new Vector3(root.position.x, stagePos.y, root.position.z)), ToStage(frameRot)),
            ["cfg"] = new JObject { ["height_cm"] = height },
        };
        if (resetPending) msg["reset"] = true;
        resetPending = false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToString(Newtonsoft.Json.Formatting.None));
            udp.Send(bytes, bytes.Length, endpoint);
        }
        catch (SocketException) { }   // not listening yet: Receive notices the silence
    }

    private void Receive()
    {
        while (true)
        {
            try
            {
                if (udp.Available <= 0) return;
                IPEndPoint from = null;
                var data = udp.Receive(ref from);
                var json = JObject.Parse(Encoding.UTF8.GetString(data));
                if (json["trackers"] is JObject t && t["hip"] != null)
                {
                    reply = json;
                    lastReply = Time.unscaledTime;
                    warnedSilent = false;
                    busy = json.Value<bool?>("busy") ?? false;
                    if (Enum.TryParse(json.Value<string>("posture") ?? "Stand", out Posture p)) CurrentPosture = p;
                }
            }
            catch (SocketException) { return; }
            catch (Exception e) when (e is Newtonsoft.Json.JsonException || e is InvalidCastException)
            {
                Debug.LogWarning($"LayableBody: bad answer from Layable ({e.Message}).");
            }
        }
    }

    private void WarnIfSilent()
    {
        var silentFor = Time.unscaledTime - Mathf.Max(lastReply, firstSend);
        if (AutoStart && silentFor > 1f && (process == null || process.HasExited) && Time.unscaledTime - startedAt > 10f)
        {
            StartProcess();
        }
        if (!warnedSilent && silentFor > 5f)
        {
            warnedSilent = true;
            Debug.LogWarning($"LayableBody: Layable isn't answering on port {Port} - her body follows the Animator. " +
                             $"Start it with: cd {Folder()} && {PythonPath} -m layable.puppet");
        }
    }

    private string Folder() => string.IsNullOrEmpty(LayableFolder)
        ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Layable"))
        : LayableFolder;

    private void StartProcess()
    {
        startedAt = Time.unscaledTime;
        var folder = Folder();
        if (!Directory.Exists(Path.Combine(folder, "layable")))
        {
            Debug.LogWarning($"LayableBody: no Layable at {folder} - set LayableFolder.");
            AutoStart = false;
            return;
        }
        try
        {
            var info = new ProcessStartInfo(PythonPath, $"-u -m layable.puppet --port {Port}")
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.Log("Layable: " + e.Data); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Debug.LogWarning("Layable: " + e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Debug.Log($"LayableBody: started Layable ({PythonPath} -m layable.puppet in {folder}).");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"LayableBody: couldn't start Layable with '{PythonPath}' ({e.Message}). Python 3.11 or newer is needed.");
            process = null;
            AutoStart = false;
        }
    }

    private void OnDestroy()
    {
        udp?.Close();
        udp = null;
        try
        {
            if (process != null && !process.HasExited) process.Kill();
        }
        catch (InvalidOperationException) { }
        process = null;
    }

    // ------------------------------------------------------------------ calibration

    // Once, standing in her idle stance with Layable's body settled on it: how each bone sits on
    // its tracker. From then on the trackers say where the bones go.
    private void Calibrate()
    {
        if (++calibrationFrames == 1)
        {
            // Her facing, from the head: level, the way her root faces. And where her eyes are.
            headToFacing = Quaternion.Inverse(head.rotation) * Quaternion.Euler(0f, root.eulerAngles.y, 0f);
            if (leftEye == null || rightEye == null)
            {
                var up = root.up * 0.07f + root.forward * 0.08f;
                eyeOffset = head.InverseTransformPoint(head.position + up);
            }
            eyeHeight = Mathf.Max(0.5f, EyePoint().y - root.position.y);
            resetPending = true;   // a fresh body for the measured height
            return;
        }
        if (calibrationFrames < 45 || CurrentPosture != Posture.Stand) return;

        Tracker("hip", out var hipPos, out var hipRot);
        hipOffset = Offset.Between(hipPos, hipRot, hips.position, hips.rotation);
        Tracker("chest", out var chestPos, out var chestRot);
        var top = spineChain[spineChain.Length - 1];
        chestOffset = Offset.Between(chestPos, chestRot, top.position, top.rotation);
        foreach (var leg in new[] { left, right })
        {
            Tracker(leg.Side + "_foot", out var footPos, out var footRot);
            leg.FootOffset = Offset.Between(footPos, footRot, leg.Foot.position, leg.Foot.rotation);
            leg.UpperLength = Vector3.Distance(leg.Upper.position, leg.Lower.position);
            leg.LowerLength = Vector3.Distance(leg.Lower.position, leg.Foot.position);
            leg.KneeForwardLocal = Quaternion.Inverse(leg.Upper.rotation) * root.forward;
            // a bind pose that isn't standing on the floor (T-pose in the air, odd import): use
            // the idle stance instead
            if (leg.AnkleHeight < 0.02f || leg.AnkleHeight > 0.25f) leg.AnkleHeight = leg.Foot.position.y - root.position.y;
        }
        // If the idle clip holds her feet off the ground (or in it), her hips are that much off
        // too: take it out, so her legs reach the ground they're put on.
        var footFloat = 0f;
        foreach (var leg in new[] { left, right }) footFloat += (leg.Foot.position.y - root.position.y - leg.AnkleHeight) * 0.5f;
        hipOffset.Pos -= Quaternion.Inverse(hipRot) * (Vector3.up * footFloat);
        if (Mathf.Abs(footFloat) > 0.01f) Debug.Log($"LayableBody: the idle clip holds her feet {footFloat * 100f:F1} cm off the ground - corrected.");
        layableHeight = Mathf.Clamp(eyeHeight / 0.927f, 1f, 2.5f);
        calibrated = true;
        Debug.Log($"LayableBody: calibrated (eyes at {eyeHeight:F2} m) - Layable is moving her body now.");
    }

    // ------------------------------------------------------------------ posing

    private Quaternion[] animLocal;

    private void Pose(float w)
    {
        // Everything is solved at full strength, then blended with the Animator's pose in
        // local space, so fading in and out is smooth whatever the bones did.
        var bones = PosedBones();
        if (animLocal == null) animLocal = new Quaternion[bones.Length];
        for (var i = 0; i < bones.Length; i++) animLocal[i] = bones[i].localRotation;
        var animHipPos = hips.localPosition;

        // hips
        Tracker("hip", out var hp, out var hr);
        hipOffset.Apply(hp, hr, out var hipPos, out var hipRot);
        hips.SetPositionAndRotation(hipPos, hipRot);

        // spine, spread over its bones, to Layable's chest
        Tracker("chest", out var cp, out var cr);
        chestOffset.Apply(cp, cr, out _, out var chestRot);
        var top = spineChain[spineChain.Length - 1];
        var bend = chestRot * Quaternion.Inverse(top.rotation);
        var share = Quaternion.Slerp(Quaternion.identity, bend, 1f / spineChain.Length);
        foreach (var b in spineChain) b.rotation = share * b.rotation;

        // neck and head, to the head Layable used (her own while standing)
        if (reply["head"] is JObject vh)
        {
            var headRot = FromStage(ReadRot(vh["q"])) * Quaternion.Inverse(headToFacing);
            var turn = headRot * Quaternion.Inverse(head.rotation);
            if (neck != null)
            {
                neck.rotation = Quaternion.Slerp(Quaternion.identity, turn, 0.4f) * neck.rotation;
                turn = headRot * Quaternion.Inverse(head.rotation);
            }
            head.rotation = turn * head.rotation;
        }

        // legs
        foreach (var leg in new[] { left, right }) SolveLeg(leg);

        // blend with the Animator
        hips.localPosition = Vector3.Lerp(animHipPos, hips.localPosition, w);
        for (var i = 0; i < bones.Length; i++) bones[i].localRotation = Quaternion.Slerp(animLocal[i], bones[i].localRotation, w);
    }

    private Transform[] posedBones;

    private Transform[] PosedBones()
    {
        if (posedBones != null) return posedBones;
        var list = new System.Collections.Generic.List<Transform> { hips };
        list.AddRange(spineChain);
        if (neck != null) list.Add(neck);
        list.Add(head);
        foreach (var leg in new[] { left, right }) list.AddRange(new[] { leg.Upper, leg.Lower, leg.Foot });
        return posedBones = list.ToArray();
    }

    private void SolveLeg(Leg leg)
    {
        Tracker(leg.Side + "_foot", out var fp, out var fr);
        leg.FootOffset.Apply(fp, fr, out var footPos, out var footRot);
        Tracker(leg.Side + "_knee", out var kneeHint, out _);

        // On the ground, exactly: a foot Layable has planted (its ankle at standing height) goes
        // to the rig's own ankle height, so it neither floats nor sinks, whatever the offset from
        // the idle clip says. A foot in the air (a step, legs out on the ground) is never lower.
        var ground = stagePos.y + leg.AnkleHeight;
        var planted = fp.y - stagePos.y < 0.04f * layableHeight + 0.015f;
        if (planted || footPos.y < ground) footPos.y = ground;

        var a = leg.Upper.position;
        var toTarget = footPos - a;
        var reach = leg.UpperLength + leg.LowerLength;
        var d = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(leg.UpperLength - leg.LowerLength) + 1e-3f, reach - 1e-4f);
        var dir = toTarget.sqrMagnitude > 1e-8f ? toTarget.normalized : Vector3.down;
        var bendDir = Vector3.ProjectOnPlane(kneeHint - a, dir);
        if (bendDir.sqrMagnitude < 1e-6f) bendDir = Vector3.ProjectOnPlane(leg.Upper.rotation * leg.KneeForwardLocal, dir);
        bendDir.Normalize();

        var x = (leg.UpperLength * leg.UpperLength - leg.LowerLength * leg.LowerLength + d * d) / (2f * d);
        var h = Mathf.Sqrt(Mathf.Max(leg.UpperLength * leg.UpperLength - x * x, 0f));
        var knee = a + dir * x + bendDir * h;

        // thigh: point at the knee, then twist so the kneecap faces the way the knee bends
        leg.Upper.rotation = Quaternion.FromToRotation(leg.Lower.position - a, knee - a) * leg.Upper.rotation;
        var thighAxis = (leg.Lower.position - a).normalized;
        var kneeFacing = Vector3.ProjectOnPlane(leg.Upper.rotation * leg.KneeForwardLocal, thighAxis);
        var wantFacing = Vector3.ProjectOnPlane(bendDir, thighAxis);
        if (kneeFacing.sqrMagnitude > 1e-6f && wantFacing.sqrMagnitude > 1e-6f)
        {
            leg.Upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(kneeFacing, wantFacing, thighAxis), thighAxis) * leg.Upper.rotation;
        }
        // shin: point at the ankle
        var b = leg.Lower.position;
        leg.Lower.rotation = Quaternion.FromToRotation(leg.Foot.position - b, footPos - b) * leg.Lower.rotation;
        // foot: flat the way Layable has it
        leg.Foot.rotation = footRot;
    }

    // ------------------------------------------------------------------ coordinates
    // Layable (OpenXR) is right-handed with -Z forward; Unity is left-handed with +Z forward.
    // Mirroring Z converts between them both ways: (x, y, z) -> (x, y, -z), and for rotations
    // (x, y, z, w) -> (-x, -y, z, w). Layable works in the fixed stage space set at Start.

    private Vector3 ToStage(Vector3 world)
    {
        var v = Quaternion.Inverse(stageRot) * (world - stagePos);
        return new Vector3(v.x, v.y, -v.z);
    }

    private Quaternion ToStage(Quaternion world)
    {
        var q = Quaternion.Inverse(stageRot) * world;
        return new Quaternion(-q.x, -q.y, q.z, q.w);
    }

    private Vector3 FromStage(Vector3 layable) => stagePos + stageRot * new Vector3(layable.x, layable.y, -layable.z);

    private Quaternion FromStage(Quaternion layable) => stageRot * new Quaternion(-layable.x, -layable.y, layable.z, layable.w);

    private void Tracker(string name, out Vector3 pos, out Quaternion rot)
    {
        var t = reply["trackers"][name] ?? reply["trackers"]["hip"];
        pos = FromStage(ReadPos(t["p"]));
        rot = FromStage(ReadRot(t["q"]));
    }

    private static JObject PoseJson(Vector3 p, Quaternion q) => new JObject
    {
        ["p"] = new JArray(p.x, p.y, p.z),
        ["q"] = new JArray(q.x, q.y, q.z, q.w),
    };

    private static Vector3 ReadPos(JToken t) => new Vector3(t[0].Value<float>(), t[1].Value<float>(), t[2].Value<float>());

    private static Quaternion ReadRot(JToken t) =>
        new Quaternion(t[0].Value<float>(), t[1].Value<float>(), t[2].Value<float>(), t[3].Value<float>()).normalized;
}

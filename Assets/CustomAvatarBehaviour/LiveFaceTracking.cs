using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using Newtonsoft.Json.Linq;

// Receives live ARKit blendshapes from the MediaPipe tracker over UDP and drives the face
// with them directly.
//
// This is the puppeteered mode: a real person's face, tracked at ~30fps, replacing the
// generated behaviour. It exists for two reasons - it is the most direct demonstration
// that the avatar speaks ARKit properly, and it supplies the eye and brow channels the
// speech model cannot produce, from an actual face rather than from a procedural model.
//
// Receiving happens on a background thread because Socket.Receive blocks; Unity's API is
// not thread-safe, so the thread only parks the latest values and all mesh writes happen
// on the main thread in LateUpdate.
[DefaultExecutionOrder(20)]   // after the procedural layers it overrides
public class LiveFaceTracking : MonoBehaviour
{
    [Header("Source")]
    public int Port = 39540;
    [Tooltip("Start receiving as soon as the scene runs.")]
    public bool AutoStart = true;

    [Header("Target")]
    public SkinnedMeshRenderer FaceRenderer;

    [Header("Blending")]
    [Tooltip("How strongly tracking overrides the generated animation. 1 = fully puppeteered, 0 = ignored. Values between let a tracked performance sit on top of the procedural layer.")]
    [Range(0f, 1f)] public float Weight = 1f;
    [Tooltip("Seconds of smoothing. Webcam tracking is noisier than a phone depth sensor, and raw values visibly shimmer.")]
    public float SmoothTime = 0.06f;
    [Tooltip("Seconds without a packet before tracking is considered lost and control returns to the generated animation.")]
    public float TimeoutSeconds = 0.6f;

    [Tooltip("Write the tracked face onto FaceRenderer. Off, the tracker is still received and calibrated - the role-play records it for the movie - but the avatar keeps her own face.")]
    public bool ApplyToFace = true;

    [Header("Scope")]
    [Tooltip("Drive only the eye and brow channels, leaving the mouth to the speech model. Useful when you want her own speech but a real person's upper face.")]
    public bool UpperFaceOnly = false;

    [Header("Calibration")]
    // Measured on RAVDESS: a subject's resting face reported browDown 0.518 and eyeSquint
    // 0.638. The tracker is not calibrated per identity, so every face carries a constant
    // offset and expression rides on top of it. Without subtracting the performer's own
    // neutral, the avatar wears their resting face as a permanent expression.
    [Tooltip("Subtract the performer's neutral face before applying. Press the calibrate key with a relaxed expression to set it.")]
    public bool UseCalibration = true;
    [Tooltip("Hold a neutral face and press this to capture the baseline.")]
    public KeyCode CalibrateKey = KeyCode.N;
    [Tooltip("Seconds of frames averaged into the baseline, so a single noisy frame doesn't define it.")]
    public float CalibrationSeconds = 1.0f;
    public bool IsCalibrated { get; private set; }

    [Header("Demo overlay")]
    [Tooltip("Draws the incoming channels and where each one lands on the avatar - shows the retarget happening rather than asking people to take it on trust.")]
    public bool ShowOverlay = true;
    public int OverlayChannels = 12;

    public bool IsReceiving { get; private set; }
    public int PacketsReceived { get; private set; }
    // The latest frame, calibrated, every channel the tracker sent (not only those on this
    // avatar's mesh), for recording. Valid while IsReceiving.
    public IReadOnlyDictionary<string, float> Latest => latest;
    // Head rotation as the tracker reports it (pitch, yaw, roll in degrees), if it sends one.
    public Vector3 HeadEuler { get; private set; }
    public bool HasHead { get; private set; }

    private static readonly HashSet<string> UpperFaceChannels = new HashSet<string>
    {
        "eyeBlinkLeft", "eyeBlinkRight", "eyeSquintLeft", "eyeSquintRight",
        "eyeWideLeft", "eyeWideRight",
        "eyeLookInLeft", "eyeLookInRight", "eyeLookOutLeft", "eyeLookOutRight",
        "eyeLookUpLeft", "eyeLookUpRight", "eyeLookDownLeft", "eyeLookDownRight",
        "browDownLeft", "browDownRight", "browInnerUp",
        "browOuterUpLeft", "browOuterUpRight",
    };

    private UdpClient client;
    private Thread thread;
    private volatile bool running;

    // Written by the receive thread, read by the main thread. Guarded rather than
    // lock-free because the payload is a dictionary, not a single word.
    private readonly Dictionary<string, float> incoming = new Dictionary<string, float>();
    private readonly object incomingLock = new object();
    private volatile bool hasNew;
    private Vector3 incomingHead;
    private bool incomingHasHead;
    private readonly Dictionary<string, float> latest = new Dictionary<string, float>();
    private float lastPacketAt = -99f;

    private Dictionary<string, int> nameToIndex;
    private float[] baseline;
    private float[] current;
    private float[] velocity;
    private readonly Dictionary<int, float> targets = new Dictionary<int, float>();
    private readonly List<int> driven = new List<int>();

    // Per-channel neutral offsets captured from the performer.
    private readonly Dictionary<string, float> neutral = new Dictionary<string, float>();
    private readonly Dictionary<string, float> calibAccum = new Dictionary<string, float>();
    private int calibFrames;
    private float calibratingUntil = -1f;

    // Latest applied values, kept for the overlay so it shows what actually reached the
    // avatar rather than what arrived on the wire.
    private readonly List<(string channel, string shape, float value)> applied =
        new List<(string, string, float)>();

    private void Start()
    {
        if (FaceRenderer == null || FaceRenderer.sharedMesh == null)
        {
            Debug.LogError("LiveFaceTracking: FaceRenderer not assigned.");
            enabled = false;
            return;
        }

        var mesh = FaceRenderer.sharedMesh;
        nameToIndex = new Dictionary<string, int>();
        for (var i = 0; i < mesh.blendShapeCount; i++) nameToIndex[mesh.GetBlendShapeName(i)] = i;

        baseline = new float[mesh.blendShapeCount];
        current = new float[mesh.blendShapeCount];
        velocity = new float[mesh.blendShapeCount];
        for (var i = 0; i < mesh.blendShapeCount; i++)
        {
            baseline[i] = FaceRenderer.GetBlendShapeWeight(i);
            current[i] = baseline[i];
        }

        if (AutoStart) StartReceiving();
    }

    public void StartReceiving()
    {
        if (running) return;

        try
        {
            client = new UdpClient(Port);
        }
        catch (Exception ex)
        {
            Debug.LogError($"LiveFaceTracking: could not bind UDP port {Port}: {ex.Message}");
            return;
        }

        running = true;
        thread = new Thread(ReceiveLoop) { IsBackground = true };
        thread.Start();
        Debug.Log($"LiveFaceTracking: listening on UDP {Port}");
    }

    public void StopReceiving()
    {
        running = false;
        client?.Close();
        client = null;
        thread = null;
        IsReceiving = false;
    }

    private void OnDestroy() => StopReceiving();
    private void OnApplicationQuit() => StopReceiving();

    private void ReceiveLoop()
    {
        var endpoint = new IPEndPoint(IPAddress.Any, Port);
        while (running)
        {
            try
            {
                var bytes = client.Receive(ref endpoint);
                var json = System.Text.Encoding.UTF8.GetString(bytes);
                var packet = JObject.Parse(json);
                var bs = packet["bs"] as JObject;
                if (bs == null) continue;
                var head = packet["head"] as JArray;

                lock (incomingLock)
                {
                    // Cleared each packet: a channel the tracker stopped sending has
                    // dropped below its threshold, which means zero, not "hold last".
                    incoming.Clear();
                    foreach (var kv in bs) incoming[kv.Key] = (float)kv.Value;
                    incomingHasHead = head != null && head.Count >= 3;
                    if (incomingHasHead) incomingHead = new Vector3((float)head[0], (float)head[1], (float)head[2]);
                }
                hasNew = true;
                PacketsReceived++;
            }
            catch (SocketException)
            {
                // Socket closed during shutdown, or a transient receive error.
                if (!running) return;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"LiveFaceTracking: bad packet ({ex.Message})");
            }
        }
    }

    // Averages a second of frames rather than snapshotting one, so a blink or a twitch
    // during calibration doesn't get baked in as the performer's resting face.
    public void BeginCalibration()
    {
        calibAccum.Clear();
        calibFrames = 0;
        calibratingUntil = Time.time + CalibrationSeconds;
        Debug.Log($"LiveFaceTracking: hold a neutral face - calibrating for {CalibrationSeconds:F1}s");
    }

    private void AccumulateCalibration()
    {
        foreach (var kv in incoming)
        {
            calibAccum.TryGetValue(kv.Key, out var sum);
            calibAccum[kv.Key] = sum + kv.Value;
        }
        calibFrames++;
    }

    private void FinishCalibration()
    {
        calibratingUntil = -1f;
        if (calibFrames == 0)
        {
            Debug.LogWarning("LiveFaceTracking: no frames received during calibration - is the tracker running?");
            return;
        }

        neutral.Clear();
        foreach (var kv in calibAccum) neutral[kv.Key] = kv.Value / calibFrames;
        IsCalibrated = true;

        var notable = new List<string>();
        foreach (var kv in neutral)
        {
            if (kv.Value > 0.1f) notable.Add($"{kv.Key}={kv.Value:F2}");
        }
        Debug.Log($"LiveFaceTracking: calibrated over {calibFrames} frames. " +
                  $"Resting offsets removed: {(notable.Count > 0 ? string.Join(", ", notable) : "none significant")}");
    }

    private void LateUpdate()
    {
        if (FaceRenderer == null || baseline == null) return;

        if (Input.GetKeyDown(CalibrateKey)) BeginCalibration();

        if (hasNew)
        {
            hasNew = false;
            lastPacketAt = Time.time;

            lock (incomingLock)
            {
                if (calibratingUntil > 0f) AccumulateCalibration();

                targets.Clear();
                applied.Clear();
                latest.Clear();
                HasHead = incomingHasHead;
                HeadEuler = incomingHead;
                foreach (var kv in incoming)
                {
                    var v = kv.Value;
                    if (UseCalibration && IsCalibrated && neutral.TryGetValue(kv.Key, out var n))
                    {
                        // Only the deviation from the performer's own neutral is expression.
                        // Clamped at zero: a channel below its resting value means "relaxed",
                        // not "negative", and ARKit weights have no negative direction.
                        v = Mathf.Max(0f, v - n);
                    }
                    latest[kv.Key] = v;

                    if (UpperFaceOnly && !UpperFaceChannels.Contains(kv.Key)) continue;
                    if (!nameToIndex.TryGetValue(kv.Key, out var idx)) continue;
                    if (v <= 0.001f) continue;
                    targets[idx] = v * 100f;
                    applied.Add((kv.Key, FaceRenderer.sharedMesh.GetBlendShapeName(idx), v));
                }
            }

            applied.Sort((a, b) => b.value.CompareTo(a.value));

            driven.Clear();
            foreach (var kv in targets) driven.Add(kv.Key);
        }

        if (calibratingUntil > 0f && Time.time >= calibratingUntil) FinishCalibration();

        IsReceiving = Time.time - lastPacketAt < TimeoutSeconds;

        // Hand control back to the generated animation when tracking drops out, rather
        // than freezing on the last tracked pose.
        if (!ApplyToFace) return;
        var w = IsReceiving ? Weight : 0f;
        if (w <= 0.001f && driven.Count == 0) return;

        for (var i = 0; i < driven.Count; i++)
        {
            var idx = driven[i];
            if (idx >= baseline.Length) continue;

            targets.TryGetValue(idx, out var tracked);
            // Blend against whatever the procedural layers already wrote this frame, so a
            // partial weight genuinely mixes the two rather than fighting them.
            var existing = FaceRenderer.GetBlendShapeWeight(idx);
            var desired = Mathf.Lerp(existing, Mathf.Clamp(baseline[idx] + tracked, 0f, 100f), w);

            current[idx] = Mathf.SmoothDamp(current[idx], desired, ref velocity[idx], SmoothTime);
            FaceRenderer.SetBlendShapeWeight(idx, current[idx]);
        }
    }

    // Shows the tracked channel, the avatar shape it drives, and the live value. On an
    // ARKit avatar those two names are identical, which is the point worth seeing: the
    // retarget is a name match, not a table of guesses.
    private void OnGUI()
    {
        if (!ShowOverlay) return;

        const float w = 330f;
        var h = 92f + Mathf.Min(applied.Count, OverlayChannels) * 17f;
        var box = new Rect(12, 12, w, h);

        GUI.color = new Color(0f, 0f, 0f, 0.72f);
        GUI.Box(box, GUIContent.none);
        GUI.color = Color.white;

        GUILayout.BeginArea(new Rect(box.x + 12, box.y + 10, box.width - 24, box.height - 20));

        var status = !IsReceiving ? "NO SIGNAL"
                   : calibratingUntil > 0f ? "CALIBRATING - hold neutral"
                   : IsCalibrated ? "tracking (calibrated)"
                   : "tracking - UNCALIBRATED";

        GUI.color = !IsReceiving ? new Color(1f, 0.45f, 0.4f)
                  : IsCalibrated ? new Color(0.5f, 0.95f, 0.6f)
                  : new Color(1f, 0.85f, 0.4f);
        GUILayout.Label($"webcam -> ARKit -> Lapwing   [{status}]");
        GUI.color = Color.white;

        GUILayout.Label($"udp :{Port}   {PacketsReceived} packets   weight {Weight:F2}" +
                        (UpperFaceOnly ? "   upper-face only" : ""));

        if (!IsCalibrated)
        {
            GUI.color = new Color(1f, 0.85f, 0.4f);
            GUILayout.Label($"press {CalibrateKey} with a relaxed face to calibrate");
            GUI.color = Color.white;
        }

        GUILayout.Space(4);

        var shown = Mathf.Min(applied.Count, OverlayChannels);
        for (var i = 0; i < shown; i++)
        {
            var (channel, shape, value) = applied[i];
            var bar = new string('|', Mathf.RoundToInt(value * 18f));
            // Channel and target shape are printed separately even when identical - seeing
            // them match is what demonstrates the avatar speaks ARKit natively.
            var same = channel == shape;
            GUI.color = same ? new Color(0.65f, 0.85f, 1f) : new Color(1f, 0.8f, 0.55f);
            GUILayout.Label($"{channel,-22}{(same ? "=" : "→")} {value:F2} {bar}");
        }
        GUI.color = Color.white;

        if (applied.Count == 0 && IsReceiving)
        {
            GUILayout.Label("(face relaxed - no channels above threshold)");
        }

        GUILayout.EndArea();
    }
}

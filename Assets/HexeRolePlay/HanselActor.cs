using System;
using System.Collections.Generic;
using UnityEngine;

// The visitor's stand-in in the replay: a second avatar that plays their part with their
// own face, recorded from the webcam while they read.
//
// The tracker speaks ARKit, the avatar does not - Milltina has the Japanese-style shape set
// (eye_close, mouth_a, brow_sad...). The map below retargets channel by channel. Mouth
// channels are damped while the witch speaks: visitors often mouth along with the script,
// and a Hansel who moves his lips during the witch's line looks like a dubbing error.
//
// Without tracking data (no webcam, or the tracker was not running) the actor still plays:
// the mouth follows the loudness of the visitor's voice, the eyes blink and the head drifts.
public class HanselActor : MonoBehaviour
{
    [Serializable]
    public class Mapping
    {
        public string Arkit;
        public string Shape;
        public float Gain = 1f;
        public bool Mouth;   // damped while the visitor is not speaking
    }

    public Animator Avatar;
    [Tooltip("The mesh carrying the face shapes. Left empty, the one with the most blendshapes.")]
    public SkinnedMeshRenderer Face;
    public List<Mapping> Map = DefaultMap();

    [Header("Head")]
    [Tooltip("Sign per axis (pitch, yaw, roll) turning the tracker's head angles into the avatar's. The tracker mirrors the image, so yaw and roll usually flip.")]
    public Vector3 HeadSigns = new Vector3(1f, -1f, -1f);
    [Range(0f, 1.5f)] public float HeadScale = 0.85f;
    public float MaxHeadDegrees = 30f;
    public float EyeDegrees = 14f;

    [Header("Mouth")]
    [Range(0f, 1f)] public float MouthWhileListening = 0.2f;
    [Tooltip("Mouth opening from voice loudness, mixed under the tracked jaw so speech always shows.")]
    public float VoiceMouthGain = 3.5f;
    public string VoiceMouthShape = "mouth_a_1";
    public string BlinkLeft = "eye_close_L", BlinkRight = "eye_close_R";

    [Header("Gestures")]
    [Tooltip("Gesture played at the start of a visitor line, by the line's intended emotion.")]
    public List<string> MoodGestures = new List<string> { "scared=OpenArms", "frustrated=Shrug", "suspicious=ThinkChin", "nervous=Shrug", "brave=PointForward", "surprised=OpenArms" };
    public float GestureSeconds = 1.6f;

    private Transform head, neck, leftEye, rightEye;
    private readonly Dictionary<string, int> shapeIndex = new Dictionary<string, int>();
    private readonly Dictionary<int, float> weights = new Dictionary<int, float>();
    private readonly HashSet<int> touched = new HashSet<int>();
    private float blinkAt, blinkUntil;
    private int gestureLayer = -1, bothLayer = -1;
    private float gestureWeight, gestureUntil;
    private int activeLayer = -1;

    public static List<Mapping> DefaultMap() => new List<Mapping>
    {
        new Mapping { Arkit = "eyeBlinkLeft", Shape = "eye_close_L" },
        new Mapping { Arkit = "eyeBlinkRight", Shape = "eye_close_R" },
        new Mapping { Arkit = "eyeWideLeft", Shape = "eye_bikkuri_L", Gain = 0.7f },
        new Mapping { Arkit = "eyeWideRight", Shape = "eye_bikkuri_R", Gain = 0.7f },
        new Mapping { Arkit = "eyeSquintLeft", Shape = "eye_joy_L", Gain = 0.45f },
        new Mapping { Arkit = "eyeSquintRight", Shape = "eye_joy_R", Gain = 0.45f },
        new Mapping { Arkit = "browInnerUp", Shape = "brow_sad_1" },
        new Mapping { Arkit = "browOuterUpLeft", Shape = "brow_up_L" },
        new Mapping { Arkit = "browOuterUpRight", Shape = "brow_up_R" },
        new Mapping { Arkit = "browDownLeft", Shape = "brow_anger_1_L", Gain = 0.9f },
        new Mapping { Arkit = "browDownRight", Shape = "brow_anger_1_R", Gain = 0.9f },
        new Mapping { Arkit = "jawOpen", Shape = "mouth_a_1", Gain = 1.3f, Mouth = true },
        new Mapping { Arkit = "mouthFunnel", Shape = "mouth_o_1", Mouth = true },
        new Mapping { Arkit = "mouthPucker", Shape = "mouth_u_1", Mouth = true },
        new Mapping { Arkit = "mouthStretchLeft", Shape = "mouth_wide_L", Gain = 0.7f, Mouth = true },
        new Mapping { Arkit = "mouthStretchRight", Shape = "mouth_wide_R", Gain = 0.7f, Mouth = true },
        new Mapping { Arkit = "mouthSmileLeft", Shape = "mouth_happy_1_L" },
        new Mapping { Arkit = "mouthSmileRight", Shape = "mouth_happy_1_R" },
        new Mapping { Arkit = "mouthFrownLeft", Shape = "mouth_corner_angle_down_L" },
        new Mapping { Arkit = "mouthFrownRight", Shape = "mouth_corner_angle_down_R" },
        new Mapping { Arkit = "cheekPuff", Shape = "option_cheek_pout" },
    };

    private void Awake()
    {
        if (Avatar == null) Avatar = GetComponentInChildren<Animator>(true);
        if (Avatar != null && Avatar.isHuman)
        {
            head = Avatar.GetBoneTransform(HumanBodyBones.Head);
            neck = Avatar.GetBoneTransform(HumanBodyBones.Neck);
            leftEye = Avatar.GetBoneTransform(HumanBodyBones.LeftEye);
            rightEye = Avatar.GetBoneTransform(HumanBodyBones.RightEye);
            if (Avatar.runtimeAnimatorController != null)
            {
                gestureLayer = Avatar.GetLayerIndex("RightHand");
                bothLayer = Avatar.GetLayerIndex("BothHands");
            }
        }
        if (Face == null)
        {
            var best = 0;
            foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.sharedMesh != null && r.sharedMesh.blendShapeCount > best) { best = r.sharedMesh.blendShapeCount; Face = r; }
            }
        }
        if (Face != null)
        {
            for (var i = 0; i < Face.sharedMesh.blendShapeCount; i++) shapeIndex[Face.sharedMesh.GetBlendShapeName(i)] = i;
            var missing = new List<string>();
            foreach (var m in Map) if (!shapeIndex.ContainsKey(m.Shape)) missing.Add(m.Shape);
            if (missing.Count > 0) Debug.LogWarning($"HanselActor: shapes not on {Face.name}: {string.Join(", ", missing)}");
        }
    }

    public void SetVisible(bool visible)
    {
        gameObject.SetActive(visible);
        if (visible && Avatar != null) Avatar.SetFloat("Witch", 0f);
    }

    public void PlayGestureForMood(string mood)
    {
        if (Avatar == null || string.IsNullOrEmpty(mood)) return;
        foreach (var entry in MoodGestures)
        {
            var parts = entry.Split('=');
            if (parts.Length != 2 || !string.Equals(parts[0].Trim(), mood, StringComparison.OrdinalIgnoreCase)) continue;
            var gesture = parts[1].Trim();
            var both = gesture == "OpenArms" || gesture == "Shrug" || gesture == "Clap" || gesture == "RubHands";
            activeLayer = both ? bothLayer : gestureLayer;
            if (activeLayer < 0) return;
            Avatar.CrossFadeInFixedTime(gesture, 0.1f, activeLayer, 0f);
            gestureUntil = Time.time + GestureSeconds;
            return;
        }
    }

    // Poses the actor for one frame of the film. Called from LateUpdate, after the Animator.
    //   a, b, k    the recorded face frames around this moment (either may be null)
    //   speaking   whether this is the visitor's own line
    //   voice      loudness of the visitor's voice right now, 0..1
    public void Perform(SceneRecorder.Frame a, SceneRecorder.Frame b, float k, bool speaking, float voice)
    {
        if (Avatar == null) return;
        UpdateGestureLayer();

        weights.Clear();
        var tracked = a != null && a.VisitorFace != null;
        if (tracked)
        {
            foreach (var m in Map)
            {
                if (!shapeIndex.TryGetValue(m.Shape, out var idx)) continue;
                var v = Lerp(a.VisitorFace, b != null ? b.VisitorFace : null, m.Arkit, k) * m.Gain;
                if (m.Mouth && !speaking) v *= MouthWhileListening;
                Accumulate(idx, v);
            }
        }
        else
        {
            AutoBlink();
        }

        if (speaking && shapeIndex.TryGetValue(VoiceMouthShape, out var mouth))
        {
            var fromVoice = Mathf.Clamp01(voice * VoiceMouthGain);
            weights.TryGetValue(mouth, out var current);
            weights[mouth] = Mathf.Max(current, tracked ? fromVoice * 0.6f : fromVoice);
        }

        // Shapes the map drove last frame but not this one go back to rest.
        foreach (var idx in touched) if (!weights.ContainsKey(idx)) Face.SetBlendShapeWeight(idx, 0f);
        touched.Clear();
        foreach (var kv in weights)
        {
            Face.SetBlendShapeWeight(kv.Key, Mathf.Clamp01(kv.Value) * 100f);
            touched.Add(kv.Key);
        }

        PoseHead(a, b, k, tracked);
        if (tracked) PoseEyes(a, b, k);
    }

    private void Accumulate(int idx, float v)
    {
        weights.TryGetValue(idx, out var current);
        weights[idx] = Mathf.Max(current, v);
    }

    private void PoseHead(SceneRecorder.Frame a, SceneRecorder.Frame b, float k, bool tracked)
    {
        if (head == null) return;
        Vector3 euler;
        if (tracked && a.HasVisitorHead)
        {
            var hb = b != null && b.HasVisitorHead ? b.VisitorHead : a.VisitorHead;
            euler = Vector3.Lerp(a.VisitorHead, hb, k);
            euler = Vector3.Scale(euler, HeadSigns) * HeadScale;
        }
        else
        {
            // Idle drift: never quite still, never doing anything.
            var t = Time.time * 0.35f;
            euler = new Vector3((Mathf.PerlinNoise(t, 1.3f) - 0.5f) * 8f, (Mathf.PerlinNoise(t, 7.1f) - 0.5f) * 12f, (Mathf.PerlinNoise(t, 3.7f) - 0.5f) * 5f);
        }
        euler.x = Mathf.Clamp(euler.x, -MaxHeadDegrees, MaxHeadDegrees);
        euler.y = Mathf.Clamp(euler.y, -MaxHeadDegrees, MaxHeadDegrees);
        euler.z = Mathf.Clamp(euler.z, -MaxHeadDegrees, MaxHeadDegrees);

        // Shared between neck and head, about the body's own axes.
        var root = Avatar.transform;
        var turn = Quaternion.AngleAxis(euler.y, root.up) * Quaternion.AngleAxis(euler.x, root.right) * Quaternion.AngleAxis(euler.z, root.forward);
        if (neck != null)
        {
            var part = Quaternion.Slerp(Quaternion.identity, turn, 0.4f);
            neck.rotation = part * neck.rotation;
            head.rotation = part * head.rotation;
        }
        else
        {
            head.rotation = turn * head.rotation;
        }
    }

    private void PoseEyes(SceneRecorder.Frame a, SceneRecorder.Frame b, float k)
    {
        if (leftEye == null || rightEye == null) return;
        float Get(string name) => Lerp(a.VisitorFace, b != null ? b.VisitorFace : null, name, k);
        // ARKit: "in" turns toward the nose, "out" away, per eye.
        var yaw = (Get("eyeLookOutLeft") - Get("eyeLookInLeft") + Get("eyeLookInRight") - Get("eyeLookOutRight")) * 0.5f;
        var pitch = (Get("eyeLookDownLeft") + Get("eyeLookDownRight") - Get("eyeLookUpLeft") - Get("eyeLookUpRight")) * 0.5f;
        var root = Avatar.transform;
        var turn = Quaternion.AngleAxis(yaw * EyeDegrees * HeadSigns.y, root.up) * Quaternion.AngleAxis(pitch * EyeDegrees, root.right);
        leftEye.rotation = turn * leftEye.rotation;
        rightEye.rotation = turn * rightEye.rotation;
    }

    private void AutoBlink()
    {
        var now = Time.time;
        if (now > blinkAt)
        {
            blinkUntil = now + 0.12f;
            blinkAt = now + UnityEngine.Random.Range(2f, 5f);
        }
        if (now < blinkUntil)
        {
            if (shapeIndex.TryGetValue(BlinkLeft, out var l)) Accumulate(l, 1f);
            if (shapeIndex.TryGetValue(BlinkRight, out var r)) Accumulate(r, 1f);
        }
    }

    private void UpdateGestureLayer()
    {
        if (activeLayer < 0) return;
        var target = Time.time < gestureUntil ? 1f : 0f;
        gestureWeight = Mathf.MoveTowards(gestureWeight, target, Time.deltaTime / 0.25f);
        Avatar.SetLayerWeight(activeLayer, gestureWeight);
    }

    private static float Lerp(Dictionary<string, float> a, Dictionary<string, float> b, string key, float k)
    {
        a.TryGetValue(key, out var va);
        var vb = va;
        if (b != null) b.TryGetValue(key, out vb);
        return Mathf.Lerp(va, vb, k);
    }
}

using System.Collections.Generic;
using UnityEngine;

// The visitor's stand-in in the replay: a second Lapwing that plays their part with their
// own face, recorded from the webcam while they read.
//
// Lapwing carries the ARKit shape set, so the tracker's channels drive the face one to one.
// Mouth channels are damped while the witch speaks: visitors often mouth along with the
// script, and a Hansel who moves his lips during the witch's line looks like a dubbing error.
//
// Nothing here invents an emotion: no mood gestures, no expression presets. The face is the
// visitor's own, and without tracking data it stays neutral - the mouth follows the loudness
// of their voice, the eyes blink and the head drifts a little, so the avatar is not frozen.
public class HanselActor : MonoBehaviour
{
    public Animator Avatar;
    [Tooltip("The mesh carrying the ARKit shapes. Left empty, the one with the most blendshapes.")]
    public SkinnedMeshRenderer Face;

    [Header("Head")]
    [Tooltip("Sign per axis (pitch, yaw, roll) turning the tracker's head angles into the avatar's. The tracker mirrors the image, so yaw and roll usually flip.")]
    public Vector3 HeadSigns = new Vector3(1f, -1f, -1f);
    [Range(0f, 1.5f)] public float HeadScale = 0.85f;
    public float MaxHeadDegrees = 30f;

    [Header("Mouth")]
    [Range(0f, 1f)] public float MouthWhileListening = 0.2f;
    [Tooltip("Mouth opening from voice loudness, mixed under the tracked jaw so speech always shows.")]
    public float VoiceMouthGain = 3.5f;
    public string VoiceMouthShape = "jawOpen";
    public string BlinkLeft = "eyeBlinkLeft", BlinkRight = "eyeBlinkRight";

    private Transform head, neck;
    private readonly Dictionary<string, int> shapeIndex = new Dictionary<string, int>();
    private readonly Dictionary<int, float> weights = new Dictionary<int, float>();
    private readonly HashSet<int> touched = new HashSet<int>();
    private float blinkAt, blinkUntil;

    private static bool IsMouth(string arkit) =>
        arkit.StartsWith("jaw") || arkit.StartsWith("mouth") || arkit.StartsWith("tongue");

    private void Awake()
    {
        if (Avatar == null) Avatar = GetComponentInChildren<Animator>(true);
        if (Avatar != null && Avatar.isHuman)
        {
            head = Avatar.GetBoneTransform(HumanBodyBones.Head);
            neck = Avatar.GetBoneTransform(HumanBodyBones.Neck);
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
            if (!shapeIndex.ContainsKey(BlinkLeft) || !shapeIndex.ContainsKey(VoiceMouthShape))
                Debug.LogWarning($"HanselActor: {Face.name} has no ARKit shapes ({BlinkLeft}, {VoiceMouthShape}) - the face will not follow the visitor.");
        }
    }

    public void SetVisible(bool visible)
    {
        gameObject.SetActive(visible);
        if (visible && Avatar != null) Avatar.SetFloat("Witch", 0f);
    }

    // Poses the actor for one frame of the film. Called from LateUpdate, after the Animator.
    //   a, b, k    the recorded face frames around this moment (either may be null)
    //   speaking   whether this is the visitor's own line
    //   voice      loudness of the visitor's voice right now, 0..1
    public void Perform(SceneRecorder.Frame a, SceneRecorder.Frame b, float k, bool speaking, float voice)
    {
        if (Avatar == null || Face == null) return;

        weights.Clear();
        var tracked = a != null && a.VisitorFace != null;
        if (tracked)
        {
            var bFace = b != null ? b.VisitorFace : null;
            foreach (var kv in a.VisitorFace)
            {
                if (!shapeIndex.TryGetValue(kv.Key, out var idx)) continue;
                var v = Lerp(a.VisitorFace, bFace, kv.Key, k);
                if (IsMouth(kv.Key) && !speaking) v *= MouthWhileListening;
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

        // Shapes driven last frame but not this one go back to rest.
        foreach (var idx in touched) if (!weights.ContainsKey(idx)) Face.SetBlendShapeWeight(idx, 0f);
        touched.Clear();
        foreach (var kv in weights)
        {
            Face.SetBlendShapeWeight(kv.Key, Mathf.Clamp01(kv.Value) * 100f);
            touched.Add(kv.Key);
        }

        PoseHead(a, b, k, tracked);
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
            euler = new Vector3((Mathf.PerlinNoise(t, 1.3f) - 0.5f) * 6f, (Mathf.PerlinNoise(t, 7.1f) - 0.5f) * 9f, (Mathf.PerlinNoise(t, 3.7f) - 0.5f) * 4f);
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

    private void AutoBlink()
    {
        var now = Time.time;
        if (now > blinkAt)
        {
            blinkUntil = now + 0.12f;
            blinkAt = now + Random.Range(2f, 5f);
        }
        if (now < blinkUntil)
        {
            if (shapeIndex.TryGetValue(BlinkLeft, out var l)) Accumulate(l, 1f);
            if (shapeIndex.TryGetValue(BlinkRight, out var r)) Accumulate(r, 1f);
        }
    }

    private static float Lerp(Dictionary<string, float> a, Dictionary<string, float> b, string key, float k)
    {
        a.TryGetValue(key, out var va);
        var vb = va;
        if (b != null) b.TryGetValue(key, out vb);
        return Mathf.Lerp(va, vb, k);
    }
}

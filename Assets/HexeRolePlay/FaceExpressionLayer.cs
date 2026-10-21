using System.Collections.Generic;
using UnityEngine;

// Emotional expressions - the coach's warm smile, the witch's sly sneer - layered additively
// on top of whatever the face already does this frame.
//
// The face has several owners already: SAiD writes the mouth from speech, ArkitFaceAnimator
// writes eyes and brows from gaze and prosody. An expression has to sit on top of both, or
// the witch would lose her sneer the moment she speaks. So this runs last and adds offsets
// to the weights those systems wrote this frame.
//
// The one subtlety is shapes nobody else wrote this frame: they still hold our own value
// from last frame, and adding to that again would accumulate. We detect that case by
// checking whether the weight is still exactly what we set, and recover the underlying
// value by removing our previous offset first.
[DefaultExecutionOrder(30)]
public class FaceExpressionLayer : MonoBehaviour
{
    public SkinnedMeshRenderer FaceRenderer;
    [Tooltip("Seconds to blend between expressions.")]
    public float BlendTime = 0.35f;
    [Range(0f, 1.5f)] public float Intensity = 1f;
    [Tooltip("While speaking, mouth-region offsets are scaled by this so the expression does not fight articulation.")]
    [Range(0f, 1f)] public float MouthScaleWhileSpeaking = 0.45f;

    public string Current { get; private set; } = "Neutral";

    // Preset -> (ARKit/avatar shape name -> weight 0-100). Kept in code rather than as
    // FaceClip assets because they are authored in ARKit terms, which Lapwing exposes
    // directly; porting to another ARKit avatar needs no re-capture.
    private static readonly Dictionary<string, Dictionary<string, float>> Presets = new Dictionary<string, Dictionary<string, float>>
    {
        { "Neutral", new Dictionary<string, float>() },
        { "Smile", new Dictionary<string, float> {
            { "mouthSmileLeft", 38 }, { "mouthSmileRight", 34 }, { "cheekSquintLeft", 18 }, { "cheekSquintRight", 16 }, { "browInnerUp", 6 } } },
        { "BigSmile", new Dictionary<string, float> {
            { "mouthSmileLeft", 62 }, { "mouthSmileRight", 58 }, { "cheekSquintLeft", 35 }, { "cheekSquintRight", 32 },
            { "eyeSquintLeft", 18 }, { "eyeSquintRight", 16 }, { "browOuterUpLeft", 14 }, { "browOuterUpRight", 12 } } },
        { "Curious", new Dictionary<string, float> {
            { "browInnerUp", 30 }, { "browOuterUpLeft", 34 }, { "browOuterUpRight", 12 }, { "mouthSmileLeft", 16 }, { "mouthSmileRight", 12 }, { "eyeWideLeft", 10 } } },
        { "Concerned", new Dictionary<string, float> {
            { "browInnerUp", 55 }, { "mouthFrownLeft", 22 }, { "mouthFrownRight", 22 }, { "mouthPressLeft", 12 }, { "mouthPressRight", 12 } } },
        { "Surprised", new Dictionary<string, float> {
            { "browInnerUp", 48 }, { "browOuterUpLeft", 50 }, { "browOuterUpRight", 50 }, { "eyeWideLeft", 38 }, { "eyeWideRight", 38 }, { "jawOpen", 12 } } },
        // Attentive, open, slightly warm: soft smile, lifted inner brows, eyes a touch wider.
        // Nods and brow flashes on top come from SocialReactions while the visitor speaks.
        { "Listening", new Dictionary<string, float> {
            { "mouthSmileLeft", 20 }, { "mouthSmileRight", 17 }, { "browInnerUp", 14 }, { "browOuterUpLeft", 6 },
            { "eyeWideLeft", 8 }, { "eyeWideRight", 8 }, { "cheekSquintLeft", 8 }, { "cheekSquintRight", 7 } } },
        { "Thinking", new Dictionary<string, float> {
            { "browDownLeft", 22 }, { "browInnerUp", 20 }, { "mouthPressLeft", 20 }, { "mouthLeft", 18 }, { "eyeSquintLeft", 14 } } },

        // The witch. Asymmetry does most of the work: a one-sided smile and one lowered brow
        // read as scheming where the symmetric versions read as friendly.
        { "WitchSly", new Dictionary<string, float> {
            { "mouthSmileLeft", 55 }, { "mouthSmileRight", 8 }, { "browDownRight", 45 }, { "browOuterUpLeft", 30 },
            { "eyeSquintLeft", 30 }, { "eyeSquintRight", 45 }, { "noseSneerLeft", 25 }, { "cheekSquintLeft", 25 } } },
        { "WitchSweet", new Dictionary<string, float> {
            { "mouthSmileLeft", 48 }, { "mouthSmileRight", 44 }, { "browInnerUp", 40 }, { "eyeSquintLeft", 38 }, { "eyeSquintRight", 38 },
            { "cheekSquintLeft", 30 }, { "cheekSquintRight", 30 } } },
        { "WitchSuspicious", new Dictionary<string, float> {
            { "browDownLeft", 55 }, { "browDownRight", 35 }, { "eyeSquintLeft", 50 }, { "eyeSquintRight", 40 },
            { "mouthPressLeft", 30 }, { "mouthPressRight", 30 }, { "noseSneerLeft", 20 }, { "noseSneerRight", 20 } } },
        { "WitchGreedy", new Dictionary<string, float> {
            { "mouthSmileLeft", 70 }, { "mouthSmileRight", 60 }, { "eyeWideLeft", 30 }, { "eyeWideRight", 30 }, { "browOuterUpLeft", 40 },
            { "browDownRight", 20 }, { "tongueOut", 18 }, { "cheekSquintLeft", 30 } } },
        { "WitchListening", new Dictionary<string, float> {
            { "mouthSmileLeft", 30 }, { "browDownRight", 25 }, { "eyeSquintRight", 30 }, { "eyeSquintLeft", 15 } } },
    };

    private readonly Dictionary<int, float> target = new Dictionary<int, float>();
    private readonly Dictionary<int, float> current = new Dictionary<int, float>();
    private readonly Dictionary<int, float> applied = new Dictionary<int, float>();     // value we wrote last frame
    private readonly Dictionary<int, float> appliedOffset = new Dictionary<int, float>();
    private readonly List<int> scratch = new List<int>();
    private Dictionary<string, int> indexOf;
    private HashSet<int> mouthShapes;

    public static bool HasPreset(string name) => !string.IsNullOrEmpty(name) && Presets.ContainsKey(name);

    private void Start()
    {
        if (FaceRenderer == null || FaceRenderer.sharedMesh == null)
        {
            Debug.LogWarning("FaceExpressionLayer: no face renderer.");
            enabled = false;
            return;
        }
        var mesh = FaceRenderer.sharedMesh;
        indexOf = new Dictionary<string, int>();
        mouthShapes = new HashSet<int>();
        for (var i = 0; i < mesh.blendShapeCount; i++)
        {
            var name = mesh.GetBlendShapeName(i);
            indexOf[name] = i;
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("mouth") || lower.StartsWith("jaw") || lower.StartsWith("tongue")) mouthShapes.Add(i);
        }
        Set(Current);
    }

    public void Set(string preset)
    {
        if (!HasPreset(preset))
        {
            if (!string.IsNullOrEmpty(preset)) Debug.LogWarning($"FaceExpressionLayer: unknown expression '{preset}'.");
            return;
        }
        Current = preset;
        if (indexOf == null) return;

        target.Clear();
        foreach (var kv in Presets[preset])
        {
            if (indexOf.TryGetValue(kv.Key, out var idx)) target[idx] = kv.Value;
        }
    }

    private void LateUpdate()
    {
        if (indexOf == null) return;

        var speaking = SaidBlendshapePlayer.Instance != null && SaidBlendshapePlayer.Instance.IsSpeaking;
        var k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, BlendTime));

        // Every shape that is either targeted now or still easing out of a previous preset.
        scratch.Clear();
        scratch.AddRange(current.Keys);
        foreach (var idx in target.Keys) if (!current.ContainsKey(idx)) scratch.Add(idx);

        foreach (var idx in scratch)
        {
            target.TryGetValue(idx, out var goal);
            current.TryGetValue(idx, out var value);
            value = Mathf.Lerp(value, goal * Intensity, k);
            if (goal == 0f && value < 0.05f) value = 0f;
            current[idx] = value;

            var offset = value * (speaking && mouthShapes.Contains(idx) ? MouthScaleWhileSpeaking : 1f);

            var weight = FaceRenderer.GetBlendShapeWeight(idx);
            var underlying = weight;
            if (applied.TryGetValue(idx, out var last) && Mathf.Approximately(weight, last))
            {
                // Nobody else touched this shape since our write, so it still carries our offset.
                appliedOffset.TryGetValue(idx, out var lastOffset);
                underlying = weight - lastOffset;
            }

            var result = Mathf.Clamp(underlying + offset, 0f, 100f);
            FaceRenderer.SetBlendShapeWeight(idx, result);
            applied[idx] = result;
            appliedOffset[idx] = result - underlying;

            if (value == 0f && goal == 0f)
            {
                current.Remove(idx);
                applied.Remove(idx);
                appliedOffset.Remove(idx);
            }
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChatdollKit.Model
{
    public class VRCFaceExpressionProxy : MonoBehaviour, IFaceExpressionProxy
    {
        public FaceClipConfiguration FaceClipConfiguration;
        public SkinnedMeshRenderer SkinnedMeshRenderer;
        private Dictionary<string, FaceClip> faceClips = new Dictionary<string, FaceClip>();
        private Dictionary<string, float> faceValues = new Dictionary<string, float>();
        private IBlink blinker;

        [SerializeField]
        private float smoothTime = 0.2f;
        private float changeStartAt;
        private string currentFaceName;
        private float valueToApply;
        private float velocityAtStart;

        [Header("Apex Variance")]
        [SerializeField]
        [Tooltip("Held expressions rarely sit at exactly full intensity. Each time an expression is set, its target value is scaled by a random factor in this range.")]
        private float apexIntensityMin = 0.75f;
        [SerializeField]
        private float apexIntensityMax = 1.0f;

        [Header("Idle Micro-Motion")]
        [SerializeField]
        private bool useIdleMicroMotion = true;
        [SerializeField]
        [Tooltip("How much the held expression's intensity drifts up/down, as a fraction of its target value.")]
        private float microMotionAmplitude = 0.12f;
        [SerializeField]
        [Tooltip("How fast the drift cycles. Higher = more restless, lower = slower/calmer breathing.")]
        private float microMotionSpeed = 0.6f;
        private float noiseSeedA;
        private float noiseSeedB;

        [Header("Micro-Expression Flicker")]
        [SerializeField]
        [Tooltip("Briefly flash a different registered expression at low intensity, like an involuntary leak of feeling, then fade it back out.")]
        private bool useMicroExpressionFlicker = true;
        [SerializeField]
        private float flickerIntervalMin = 4f;
        [SerializeField]
        private float flickerIntervalMax = 9f;
        [SerializeField]
        private float flickerDurationMin = 0.15f;
        [SerializeField]
        private float flickerDurationMax = 0.35f;
        [SerializeField]
        [Range(0f, 1f)]
        private float flickerIntensity = 0.25f;
        private float nextFlickerAt;
        private bool isFlickering;
        private string flickerFaceName;
        private float flickerStartedAt;
        private float flickerDuration;

        [Header("Left/Right Asymmetry")]
        [SerializeField]
        [Tooltip("Real faces are never perfectly symmetric. Blend shapes ending in _L/_R are nudged apart by up to this fraction, re-rolled each time an expression changes.")]
        private float asymmetryAmount = 0.15f;
        private float asymmetryLeftFactor = 1f;
        private float asymmetryRightFactor = 1f;

        // Your real sculpted default face, captured live from the mesh once at startup -
        // never from a FaceClip asset, which may be stale or a broken all-zero capture.
        // Every expression is applied as a delta from this, not as an absolute pose, so
        // shapes an expression doesn't touch are never disturbed.
        private float[] neutralBaseline;

        private void Awake()
        {
            noiseSeedA = Random.Range(0f, 1000f);
            noiseSeedB = Random.Range(0f, 1000f);
            Setup(gameObject.GetComponent<ModelController>().AvatarModel);
        }

        public void Setup(GameObject avatarObject)
        {
            SkinnedMeshRenderer = AvatarUtility.GetFacialSkinnedMeshRenderer(avatarObject);
            blinker = gameObject.GetComponent<IBlink>();
            CaptureNeutralBaseline();
            LoadFaces();
            if (faceClips.Count == 0)
            {
                Debug.Log("Add Neutral face expression with values zero");
                faceClips.Add("Neutral", new FaceClip("Neutral", SkinnedMeshRenderer));
                // faceValues must gain the key too - SetExpressionSmoothly reads it as the
                // starting weight, and LoadFaces (which normally fills both) bails early
                // when there is no FaceClipConfiguration to load.
                faceValues["Neutral"] = 0.0f;
            }
            ScheduleNextFlicker();
        }

        private void CaptureNeutralBaseline()
        {
            if (SkinnedMeshRenderer == null || SkinnedMeshRenderer.sharedMesh == null)
            {
                neutralBaseline = null;
                return;
            }

            var mesh = SkinnedMeshRenderer.sharedMesh;
            neutralBaseline = new float[mesh.blendShapeCount];
            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                neutralBaseline[i] = SkinnedMeshRenderer.GetBlendShapeWeight(i);
            }
        }

        private void Update()
        {
            if (changeStartAt > 0)
            {
                if (currentFaceName == "Neutral")
                {
                    _ = blinker.StartBlinkAsync();
                }
                else
                {
                    blinker.StopBlink();
                }

                var elapsed = Time.realtimeSinceStartup - changeStartAt;
                var effectiveElapsed = elapsed + velocityAtStart * smoothTime;

                var linearVelocity = Mathf.Clamp01(effectiveElapsed / smoothTime);
                // Ease in/out instead of a constant-speed ramp, reads less mechanical.
                var velocity = linearVelocity * linearVelocity * (3f - 2f * linearVelocity);

                // Reset before apply
                foreach (var fc in faceClips)
                {
                    if (fc.Key != currentFaceName)
                    {
                        SetExpression(fc.Key, faceValues[fc.Key] * (1 - velocity));
                    }
                }
                // Apply new, staggered per facial region so eyes/brows lead and mouth follows
                ApplyStaggeredExpression(currentFaceName, valueToApply, effectiveElapsed);
                faceValues[currentFaceName] = velocity * valueToApply;

                if (effectiveElapsed >= smoothTime + MaxRegionDelay)
                {
                    changeStartAt = 0;
                }
            }
            else
            {
                if (useIdleMicroMotion && !string.IsNullOrEmpty(currentFaceName) && valueToApply > 0f)
                {
                    ApplyIdleMicroMotion();
                }
                if (useMicroExpressionFlicker)
                {
                    UpdateFlicker();
                }
            }
        }

        // Keep a held expression subtly alive instead of frozen: two Perlin noise
        // octaves at different speeds/phases drift the expression's intensity around
        // its target value so it reads as breathing rather than a static pose.
        private void ApplyIdleMicroMotion()
        {
            var t = Time.realtimeSinceStartup * microMotionSpeed;
            var slow = Mathf.PerlinNoise(noiseSeedA + t, 0f) - 0.5f;
            var fast = Mathf.PerlinNoise(noiseSeedB + t * 2.7f, 100f) - 0.5f;
            var noise = (slow * 0.7f + fast * 0.3f) * 2f; // roughly -1..1
            var multiplier = 1f + noise * microMotionAmplitude;
            SetExpression(currentFaceName, Mathf.Clamp01(valueToApply * multiplier));
        }

        // Longest per-region onset delay below, used to extend how long a transition is
        // considered "in progress" so slower regions get their own full ease window.
        private const float MaxRegionDelay = 0.12f;

        // Applies an incoming expression with each blend shape's onset offset by how
        // quickly that facial region reacts in real expressions: eyes/brows/pupils lead,
        // cheeks/nose follow a little behind, mouth/jaw/tongue arrive last. Everything
        // still finishes within smoothTime + MaxRegionDelay of the transition starting.
        private void ApplyStaggeredExpression(string name, float targetValue, float effectiveElapsed)
        {
            if (!faceClips.ContainsKey(name))
            {
                return;
            }

            if (name == "Neutral")
            {
                return;
            }

            var shapeCount = SkinnedMeshRenderer != null && SkinnedMeshRenderer.sharedMesh != null
                ? SkinnedMeshRenderer.sharedMesh.blendShapeCount
                : 0;

            foreach (var blendShapeValue in faceClips[name].Values)
            {
                // FaceClips store absolute blend shape indices, so a configuration captured
                // on a different avatar points past the end of this mesh. Skip rather than
                // throw, so a stale asset degrades to "no expression" instead of spamming
                // out-of-range errors every frame.
                if (blendShapeValue.Index < 0 || blendShapeValue.Index >= shapeCount)
                {
                    continue;
                }

                var baseline = neutralBaseline != null && blendShapeValue.Index < neutralBaseline.Length
                    ? neutralBaseline[blendShapeValue.Index]
                    : 0f;

                var delta = blendShapeValue.Weight - baseline;
                if (Mathf.Abs(delta) < 0.01f)
                {
                    continue;
                }

                var regionDelay = GetRegionDelay(blendShapeValue.Name);
                var localElapsed = Mathf.Max(0f, effectiveElapsed - regionDelay);
                var linear = Mathf.Clamp01(localElapsed / smoothTime);
                var eased = linear * linear * (3f - 2f * linear);

                var sideFactor = 1f;
                if (blendShapeValue.Name.EndsWith("_L"))
                {
                    sideFactor = asymmetryLeftFactor;
                }
                else if (blendShapeValue.Name.EndsWith("_R"))
                {
                    sideFactor = asymmetryRightFactor;
                }

                SkinnedMeshRenderer.SetBlendShapeWeight(blendShapeValue.Index, baseline + delta * eased * targetValue * sideFactor);
            }
        }

        private static float GetRegionDelay(string blendShapeName)
        {
            var n = blendShapeName.ToLowerInvariant();
            if (n.Contains("mouth") || n.Contains("jaw") || n.Contains("tongue") || n.Contains("tooth"))
            {
                return MaxRegionDelay;
            }
            if (n.Contains("cheek") || n.Contains("nose"))
            {
                return MaxRegionDelay * 0.5f;
            }
            return 0f; // eye, brow, eyelid, eyelash, pupil, highlight shapes react first
        }

        private void ScheduleNextFlicker()
        {
            nextFlickerAt = Time.realtimeSinceStartup + Random.Range(flickerIntervalMin, flickerIntervalMax);
        }

        // Involuntary-looking flash of a different registered expression bleeding through
        // for a few hundred ms, shaped as a sine pulse so it rises and fades on its own -
        // no separate fade-out bookkeeping needed.
        private void UpdateFlicker()
        {
            if (!isFlickering)
            {
                if (Time.realtimeSinceStartup < nextFlickerAt || faceClips.Count < 2)
                {
                    return;
                }

                var candidates = faceClips.Keys.Where(n => n != currentFaceName).ToList();
                if (candidates.Count == 0)
                {
                    return;
                }

                flickerFaceName = candidates[Random.Range(0, candidates.Count)];
                flickerStartedAt = Time.realtimeSinceStartup;
                flickerDuration = Random.Range(flickerDurationMin, flickerDurationMax);
                isFlickering = true;
            }

            var elapsed = Time.realtimeSinceStartup - flickerStartedAt;
            if (elapsed >= flickerDuration)
            {
                SetExpression(flickerFaceName, 0f);
                isFlickering = false;
                ScheduleNextFlicker();
                return;
            }

            var pulse = Mathf.Sin(Mathf.PI * (elapsed / flickerDuration)) * flickerIntensity;
            SetExpression(flickerFaceName, pulse);
        }

        public void SetExpression(string name = "Neutral", float value = 1.0f)
        {
            if (!faceClips.ContainsKey(name))
            {
                Debug.LogWarning($"Face '{name}' is not registered");
                return;
            }

            // Neutral is your permanent base sculpt, not a clip to actively apply - it's
            // captured once into neutralBaseline and left untouched forever. Track it for
            // bookkeeping but never write to the mesh for it.
            if (name == "Neutral")
            {
                faceValues[name] = value;
                return;
            }

            var shapeCount = SkinnedMeshRenderer != null && SkinnedMeshRenderer.sharedMesh != null
                ? SkinnedMeshRenderer.sharedMesh.blendShapeCount
                : 0;

            foreach (var blendShapeValue in faceClips[name].Values)
            {
                // FaceClips store absolute blend shape indices, so a configuration captured
                // on a different avatar points past the end of this mesh. Skip rather than
                // throw, so a stale asset degrades to "no expression" instead of spamming
                // out-of-range errors every frame.
                if (blendShapeValue.Index < 0 || blendShapeValue.Index >= shapeCount)
                {
                    continue;
                }

                var baseline = neutralBaseline != null && blendShapeValue.Index < neutralBaseline.Length
                    ? neutralBaseline[blendShapeValue.Index]
                    : 0f;

                // Only the delta from your real baseline belongs to this expression - a
                // captured clip snapshots all 700+ shapes including your identity sculpt,
                // and applying that as an absolute pose is what was fighting your baseline.
                var delta = blendShapeValue.Weight - baseline;
                if (Mathf.Abs(delta) < 0.01f)
                {
                    continue;
                }

                var sideFactor = 1f;
                if (blendShapeValue.Name.EndsWith("_L"))
                {
                    sideFactor = asymmetryLeftFactor;
                }
                else if (blendShapeValue.Name.EndsWith("_R"))
                {
                    sideFactor = asymmetryRightFactor;
                }

                SkinnedMeshRenderer.SetBlendShapeWeight(blendShapeValue.Index, baseline + delta * value * sideFactor);
            }
            faceValues[name] = value;
        }

        public void SetExpressionSmoothly(string name = "Neutral", float value = 1.0f)
        {
            if (!faceClips.ContainsKey(name))
            {
                Debug.LogWarning($"Face '{name}' is not registered");
                return;
            }

            changeStartAt = Time.realtimeSinceStartup;
            currentFaceName = name;
            valueToApply = value * Random.Range(apexIntensityMin, apexIntensityMax);
            velocityAtStart = faceValues[name];

            asymmetryLeftFactor = 1f + Random.Range(-asymmetryAmount, asymmetryAmount);
            asymmetryRightFactor = 1f + Random.Range(-asymmetryAmount, asymmetryAmount);
        }

        // Load faces from config
        private void LoadFaces()
        {
            if (FaceClipConfiguration == null)
            {
                Debug.LogWarning("Face configuration is not set");
                return;
            }

            faceClips.Clear();
            faceValues.Clear();

            foreach (var faceClip in FaceClipConfiguration.FaceClips)
            {
                faceClips[faceClip.Name] = faceClip;
                faceValues[faceClip.Name] = 0.0f;
            }
        }
    }
}

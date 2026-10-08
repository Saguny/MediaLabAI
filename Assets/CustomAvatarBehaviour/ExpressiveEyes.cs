using System.Collections.Generic;
using UnityEngine;

// Makes the eyes look alive rather than merely aimed.
//
// Real eyes are never still: they jump between fixation points in fast saccades (~30-80ms
// of travel, then a fixation held for a few hundred ms to a couple of seconds), the lids
// track vertical gaze, blinks vary in depth and cluster around gaze shifts, and the brows
// drift constantly. A gaze controller that smoothly lerps toward a target and a blink loop
// on a fixed timer both read as robotic precisely because they lack these.
//
// This drives the eye BONES (aim) and the eye/lid/brow BLEND SHAPES (lids, squint, brows,
// pupils) together, since those have to agree to look right - lids that don't follow the
// gaze down give the "doll staring through you" effect.
//
// Runs in LateUpdate after the Animator. Blend shape writes are additive over a baseline
// captured at Start, so they compose with the avatar's sculpted rest face instead of
// snapping it to zero.
public class ExpressiveEyes : MonoBehaviour
{
    [Header("Targets")]
    public Transform LookTarget;
    public SkinnedMeshRenderer FaceRenderer;
    public AudioSource VoiceAudioSource;

    [Header("Gaze Aim")]
    public float MaxEyeAngle = 22f;
    [Tooltip("Degrees per second during a saccade. Real saccades are near-instant; this is deliberately fast.")]
    public float SaccadeSpeed = 900f;

    [Header("Fixation")]
    [Tooltip("How long the eyes rest on a point before jumping, while listening/idle.")]
    public float FixationMin = 0.7f;
    public float FixationMax = 2.4f;
    [Tooltip("Fixations get shorter while speaking - gaze becomes more restless.")]
    public float SpeakingFixationScale = 0.55f;
    [Tooltip("Chance a saccade goes to the look target's face rather than drifting off it.")]
    [Range(0f, 1f)] public float ReturnToFaceChance = 0.55f;
    [Tooltip("How far off-target an away-glance lands, in metres at the target's distance.")]
    public float GlanceRadius = 0.35f;

    [Header("Blink")]
    public float BlinkIntervalMin = 2.2f;
    public float BlinkIntervalMax = 6.0f;
    [Tooltip("People blink noticeably more while talking.")]
    public float SpeakingBlinkScale = 0.6f;
    public float BlinkCloseTime = 0.06f;
    public float BlinkOpenTime = 0.12f;
    [Tooltip("Fraction of blinks that are shallow half-blinks rather than full closes.")]
    [Range(0f, 1f)] public float HalfBlinkChance = 0.3f;
    [Tooltip("Chance a blink is immediately followed by a second one - real blinks cluster.")]
    [Range(0f, 1f)] public float DoubleBlinkChance = 0.15f;

    [Header("Lids follow gaze")]
    [Tooltip("How strongly the lids track vertical gaze. Kept low because on this rig eyelid_down is a strong, near-closing shape rather than a subtle lid offset - raise it only if the lids look inert.")]
    [Range(0f, 100f)] public float LidGazeAmount = 14f;

    [Header("Speech coupling")]
    [Tooltip("Brow lift driven by vocal effort. Uncoupled brows drifting on their own timer while the mouth articulates is the main reason the upper face reads as disconnected from the voice.")]
    [Range(0f, 100f)] public float SpeechBrowAmount = 26f;
    [Tooltip("Eye widening on emphasis. Subtle - this is a supporting cue, not the main one.")]
    [Range(0f, 100f)] public float SpeechEyeWidenAmount = 10f;
    [Tooltip("How much the random idle drift is suppressed while speaking, so speech-driven motion leads instead of competing with noise.")]
    [Range(0f, 1f)] public float SpeechDriftSuppression = 0.65f;

    [Header("Brow life")]
    [Tooltip("Continuous low-level brow drift so the upper face is never frozen.")]
    [Range(0f, 100f)] public float BrowDriftAmount = 12f;
    public float BrowDriftSpeed = 0.25f;
    [Tooltip("Occasional brow flashes - a quick raise, the way people punctuate speech.")]
    public float BrowFlashIntervalMin = 3.5f;
    public float BrowFlashIntervalMax = 9f;
    [Range(0f, 100f)] public float BrowFlashAmount = 32f;

    [Header("Pupils")]
    [Tooltip("Slow pupil size drift. Subtle, but dead-still pupils read as glassy.")]
    [Range(0f, 100f)] public float PupilDriftAmount = 14f;

    // --- bones ---
    private Animator animator;
    private Transform leftEye, rightEye;

    // --- blend shape indices, -1 when the rig doesn't have that shape ---
    private int idxBlinkL = -1, idxBlinkR = -1, idxBlink = -1;
    private int idxLidUpL = -1, idxLidUpR = -1, idxLidDownL = -1, idxLidDownR = -1;
    private int idxBrowUpL = -1, idxBrowUpR = -1, idxBrowDownL = -1, idxBrowDownR = -1;
    private int idxPupilBig = -1, idxPupilSmall = -1;

    private float[] baseline;
    private readonly Dictionary<int, float> writes = new Dictionary<int, float>();

    // --- gaze state ---
    private Vector3 currentAimPoint;
    private Vector3 targetAimPoint;
    private float nextSaccadeAt;

    // --- blink state ---
    private float nextBlinkAt;
    private float blinkPhase = -1f;   // -1 = not blinking, else elapsed seconds
    private float blinkDepth = 1f;
    private bool queuedDoubleBlink;

    // --- brow state ---
    private float browSeed;
    private float nextBrowFlashAt;
    private float browFlashPhase = -1f;
    private float browFlashDuration;
    private float pupilSeed;

    private bool IsSpeaking => VoiceAudioSource != null && VoiceAudioSource.isPlaying;

    private void Start()
    {
        animator = GetComponent<Animator>();
        if (animator != null && animator.isHuman)
        {
            leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);

            var head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (leftEye == null && head != null) leftEye = FindChild(head, "lefteye");
            if (rightEye == null && head != null) rightEye = FindChild(head, "righteye");
        }

        if (LookTarget == null && Camera.main != null) LookTarget = Camera.main.transform;

        CacheShapeIndices();

        browSeed = Random.Range(0f, 1000f);
        pupilSeed = Random.Range(0f, 1000f);
        currentAimPoint = targetAimPoint = LookTarget != null ? LookTarget.position : transform.position + transform.forward;
        ScheduleSaccade();
        ScheduleBlink();
        nextBrowFlashAt = Time.time + Random.Range(BrowFlashIntervalMin, BrowFlashIntervalMax);
    }

    private void CacheShapeIndices()
    {
        if (FaceRenderer == null || FaceRenderer.sharedMesh == null) return;

        var mesh = FaceRenderer.sharedMesh;
        var lookup = new Dictionary<string, int>();
        for (var i = 0; i < mesh.blendShapeCount; i++) lookup[mesh.GetBlendShapeName(i)] = i;

        int Find(params string[] names)
        {
            foreach (var n in names) if (lookup.TryGetValue(n, out var i)) return i;
            return -1;
        }

        idxBlinkL = Find("eye_close_L");
        idxBlinkR = Find("eye_close_R");
        idxBlink = Find("vrc.blink", "eye_close", "まばたき");
        idxLidUpL = Find("eyelid_up_L");
        idxLidUpR = Find("eyelid_up_R");
        idxLidDownL = Find("eyelid_down_L");
        idxLidDownR = Find("eyelid_down_R");
        idxBrowUpL = Find("brow_up_L");
        idxBrowUpR = Find("brow_up_R");
        idxBrowDownL = Find("brow_down_L");
        idxBrowDownR = Find("brow_down_R");
        idxPupilBig = Find("pupil_big_1");
        idxPupilSmall = Find("pupil_smal_l1", "pupil_small_1");

        baseline = new float[mesh.blendShapeCount];
        for (var i = 0; i < mesh.blendShapeCount; i++) baseline[i] = FaceRenderer.GetBlendShapeWeight(i);
    }

    private static Transform FindChild(Transform root, string nameContainsLower)
    {
        foreach (Transform c in root)
        {
            if (c.name.ToLowerInvariant().Contains(nameContainsLower)) return c;
            var found = FindChild(c, nameContainsLower);
            if (found != null) return found;
        }
        return null;
    }

    private void ScheduleSaccade()
    {
        var scale = IsSpeaking ? SpeakingFixationScale : 1f;
        nextSaccadeAt = Time.time + Random.Range(FixationMin, FixationMax) * scale;

        if (LookTarget == null)
        {
            targetAimPoint = transform.position + transform.forward * 2f;
            return;
        }

        if (Random.value < ReturnToFaceChance)
        {
            // Back to the face - but not to the exact same spot each time. Real gaze
            // wanders around the eyes/mouth triangle rather than locking one point.
            targetAimPoint = LookTarget.position + Random.insideUnitSphere * 0.06f;
        }
        else
        {
            targetAimPoint = LookTarget.position + Random.insideUnitSphere * GlanceRadius;
        }
    }

    private void ScheduleBlink()
    {
        var scale = IsSpeaking ? SpeakingBlinkScale : 1f;
        nextBlinkAt = Time.time + Random.Range(BlinkIntervalMin, BlinkIntervalMax) * scale;
    }

    private void LateUpdate()
    {
        UpdateGaze();
        if (FaceRenderer == null || baseline == null) return;

        writes.Clear();
        UpdateBlink();
        UpdateLidsFollowGaze();
        UpdateBrows();
        UpdatePupils();
        Flush();
    }

    private void UpdateGaze()
    {
        if (Time.time >= nextSaccadeAt) ScheduleSaccade();

        // Saccades are ballistic: move at a fixed high angular rate, then stop dead.
        // Lerping with a smoothing factor would produce the drifting "camera pan" look.
        currentAimPoint = Vector3.MoveTowards(
            currentAimPoint, targetAimPoint,
            SaccadeSpeed * Mathf.Deg2Rad * Vector3.Distance(currentAimPoint, transform.position) * Time.deltaTime);

        AimEye(leftEye, currentAimPoint);
        AimEye(rightEye, currentAimPoint);
    }

    private void AimEye(Transform eye, Vector3 point)
    {
        if (eye == null || eye.parent == null) return;

        var toTarget = point - eye.position;
        if (toTarget.sqrMagnitude < 1e-6f) return;

        var desired = Quaternion.LookRotation(toTarget, eye.parent.up);
        var local = Quaternion.Inverse(eye.parent.rotation) * desired;
        eye.rotation = eye.parent.rotation * ClampRotation(local, MaxEyeAngle);
    }

    private static Quaternion ClampRotation(Quaternion q, float maxAngle)
    {
        // q and -q are the same rotation but ToAngleAxis reports opposite axes for them;
        // forcing w >= 0 stops the aim flipping to a mirrored pose at that boundary.
        if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);

        q.ToAngleAxis(out var angle, out var axis);
        if (axis.sqrMagnitude < 1e-8f || float.IsNaN(axis.x) || float.IsInfinity(axis.x)) return Quaternion.identity;

        if (angle > 180f) angle -= 360f;
        return Quaternion.AngleAxis(Mathf.Clamp(angle, -maxAngle, maxAngle), axis.normalized);
    }

    private void UpdateBlink()
    {
        // If something else already has the eyes shut - a closed-eye expression, or the
        // lid-follow at a steep downward gaze - blinking on top of that reads as a twitch
        // in an already-closed eye. Hold off until they're open again.
        if (blinkPhase < 0f && EyesAlreadyClosed())
        {
            nextBlinkAt = Mathf.Max(nextBlinkAt, Time.time + 0.35f);
            return;
        }

        if (blinkPhase < 0f && Time.time >= nextBlinkAt)
        {
            blinkPhase = 0f;
            blinkDepth = Random.value < HalfBlinkChance ? Random.Range(0.4f, 0.65f) : 1f;
            queuedDoubleBlink = Random.value < DoubleBlinkChance;
        }

        if (blinkPhase < 0f) return;

        blinkPhase += Time.deltaTime;
        var total = BlinkCloseTime + BlinkOpenTime;

        float amount;
        if (blinkPhase <= BlinkCloseTime)
        {
            amount = blinkPhase / BlinkCloseTime;
        }
        else if (blinkPhase <= total)
        {
            // Opening is slower than closing, which is what makes a blink read as organic
            // rather than as a shutter.
            amount = 1f - (blinkPhase - BlinkCloseTime) / BlinkOpenTime;
        }
        else
        {
            blinkPhase = -1f;
            if (queuedDoubleBlink)
            {
                queuedDoubleBlink = false;
                nextBlinkAt = Time.time + Random.Range(0.09f, 0.16f);
            }
            else
            {
                ScheduleBlink();
            }
            return;
        }

        var w = Mathf.Clamp01(amount) * blinkDepth * 100f;
        if (idxBlinkL >= 0 && idxBlinkR >= 0)
        {
            // Slight left/right offset so both lids don't move in perfect lockstep.
            Write(idxBlinkL, w);
            Write(idxBlinkR, w * 0.96f);
        }
        else if (idxBlink >= 0)
        {
            Write(idxBlink, w);
        }
    }

    // Reads the live weight on the closing shapes, so this notices eyes shut by any other
    // system (a face expression, the lid-follow) rather than only by our own blink.
    private bool EyesAlreadyClosed()
    {
        const float closedThreshold = 55f;

        if (idxBlinkL >= 0 && FaceRenderer.GetBlendShapeWeight(idxBlinkL) > closedThreshold) return true;
        if (idxBlinkR >= 0 && FaceRenderer.GetBlendShapeWeight(idxBlinkR) > closedThreshold) return true;
        if (idxBlink >= 0 && FaceRenderer.GetBlendShapeWeight(idxBlink) > closedThreshold) return true;
        if (idxLidDownL >= 0 && FaceRenderer.GetBlendShapeWeight(idxLidDownL) > closedThreshold) return true;

        return false;
    }

    private void UpdateLidsFollowGaze()
    {
        if (leftEye == null || LidGazeAmount <= 0f) return;

        // Pitch of the eye relative to the head drives lid position: looking down closes
        // the upper lid somewhat, looking up opens it. Without this the lids stay wide
        // open regardless of gaze, which is the main "doll eyes" tell.
        var localFwd = leftEye.parent != null
            ? leftEye.parent.InverseTransformDirection(leftEye.forward)
            : leftEye.forward;
        var pitch = Mathf.Clamp(localFwd.y, -1f, 1f);

        if (pitch < 0f)
        {
            var w = -pitch * LidGazeAmount;
            if (idxLidDownL >= 0) Write(idxLidDownL, w);
            if (idxLidDownR >= 0) Write(idxLidDownR, w);
        }
        else
        {
            var w = pitch * LidGazeAmount * 0.7f;
            if (idxLidUpL >= 0) Write(idxLidUpL, w);
            if (idxLidUpR >= 0) Write(idxLidUpR, w);
        }
    }

    private void UpdateBrows()
    {
        var energy = SaidBlendshapePlayer.Instance != null ? SaidBlendshapePlayer.Instance.SpeechEnergy : 0f;

        // While speaking, hand most of the brow motion over to vocal effort and pull the
        // random drift down, so the upper face tracks emphasis instead of wandering on an
        // unrelated schedule.
        var driftScale = Mathf.Lerp(1f, 1f - SpeechDriftSuppression, energy);

        var t = Time.time * BrowDriftSpeed;
        var driftL = (Mathf.PerlinNoise(browSeed + t, 0f) - 0.5f) * 2f * driftScale;
        var driftR = (Mathf.PerlinNoise(browSeed + t, 50f) - 0.5f) * 2f * driftScale;

        var speechLift = energy * SpeechBrowAmount;

        if (SpeechEyeWidenAmount > 0f && energy > 0.02f)
        {
            // A touch of lid opening on stressed syllables. Kept small - overdone, this
            // reads as permanent surprise.
            var widen = energy * SpeechEyeWidenAmount;
            if (idxLidUpL >= 0) Write(idxLidUpL, widen);
            if (idxLidUpR >= 0) Write(idxLidUpR, widen);
        }

        var flash = 0f;
        if (browFlashPhase < 0f && Time.time >= nextBrowFlashAt)
        {
            browFlashPhase = 0f;
            browFlashDuration = Random.Range(0.25f, 0.5f);
        }
        if (browFlashPhase >= 0f)
        {
            browFlashPhase += Time.deltaTime;
            if (browFlashPhase >= browFlashDuration)
            {
                browFlashPhase = -1f;
                nextBrowFlashAt = Time.time + Random.Range(BrowFlashIntervalMin, BrowFlashIntervalMax);
            }
            else
            {
                flash = Mathf.Sin(Mathf.PI * (browFlashPhase / browFlashDuration)) * BrowFlashAmount;
            }
        }

        ApplyBrow(driftL * BrowDriftAmount + flash + speechLift, idxBrowUpL, idxBrowDownL);
        ApplyBrow(driftR * BrowDriftAmount + flash * 0.9f + speechLift * 0.94f, idxBrowUpR, idxBrowDownR);
    }

    private void ApplyBrow(float signed, int upIdx, int downIdx)
    {
        if (signed >= 0f)
        {
            if (upIdx >= 0) Write(upIdx, signed);
        }
        else if (downIdx >= 0)
        {
            Write(downIdx, -signed);
        }
    }

    private void UpdatePupils()
    {
        if (PupilDriftAmount <= 0f) return;

        var n = (Mathf.PerlinNoise(pupilSeed + Time.time * 0.12f, 0f) - 0.5f) * 2f;
        if (n >= 0f)
        {
            if (idxPupilBig >= 0) Write(idxPupilBig, n * PupilDriftAmount);
        }
        else if (idxPupilSmall >= 0)
        {
            Write(idxPupilSmall, -n * PupilDriftAmount);
        }
    }

    // Accumulate rather than write immediately so two effects touching the same shape sum
    // instead of the later one clobbering the earlier.
    private void Write(int index, float weight)
    {
        if (index < 0) return;
        writes.TryGetValue(index, out var existing);
        writes[index] = existing + weight;
    }

    private readonly List<int> lastWritten = new List<int>();

    private void Flush()
    {
        // Release anything driven last frame but not this one, so a finished blink or brow
        // flash returns to the sculpted baseline instead of sticking.
        foreach (var idx in lastWritten)
        {
            if (!writes.ContainsKey(idx) && idx < baseline.Length)
            {
                FaceRenderer.SetBlendShapeWeight(idx, baseline[idx]);
            }
        }

        lastWritten.Clear();
        foreach (var kv in writes)
        {
            if (kv.Key >= baseline.Length) continue;
            FaceRenderer.SetBlendShapeWeight(kv.Key, Mathf.Clamp(baseline[kv.Key] + kv.Value, 0f, 100f));
            lastWritten.Add(kv.Key);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using ChatdollKit.Dialog;

// Drives the ARKit eye/brow channels as communicative behaviour rather than as idle noise.
//
// The distinction matters more than any amount of added variation. Human gaze, blinks and
// brow movement are signals tied to conversational role and to speech prosody; randomizing
// them produces a face that is busy but meaningless, which reads worse than a still one
// because the viewer keeps trying to interpret signals that carry nothing.
//
// What that means concretely, and where each rule comes from:
//
//  Gaze is state-dependent. Gaze aversion is rare while listening and common while
//  thinking, because looking away sheds visual processing load. Speakers begin a turn with
//  averted gaze, come back to the partner roughly 700ms in, and end the turn looking
//  directly at them - that final look is a turn-yielding signal, not decoration.
//
//  Fixation is not stillness. Between saccades the eye drifts slowly and is pulled back by
//  microsaccades one to two times per second. Those are discrete ~20ms flicks, not smooth
//  wander; smooth noise cannot produce them, and their absence is most of why a rendered
//  eye looks glassy. Ocular tremor is real but ~0.004 degrees, far below anything a display
//  resolves, so it is modelled for completeness rather than visible effect.
//
//  Brows track pitch, not loudness, and lead it by about 60ms. Only ~71% of brow raises
//  accompany an F0 rise, so the mapping is deliberately probabilistic rather than a direct
//  linear follow, which would look mechanical.
//
//  Blinks are punctuation. They cluster at the end of vocalizations and in pauses, so they
//  are triggered by phrase boundaries rather than purely by a timer.
//
// Owns eyeLook*, eyeBlink*, eyeSquint*, eyeWide* and brow*. Never touches mouth, jaw, cheek
// or nose - those come from the generated speech curves, so the two systems cannot collide.
// Runs after NaturalHeadMotion and BreathingMotion, which pose the bones the eyes hang
// from - see the note on NaturalHeadMotion.
[DefaultExecutionOrder(10)]
public class ArkitFaceAnimator : MonoBehaviour
{
    public enum Mode { Idle, Listening, Thinking, Speaking }

    [Header("Targets")]
    public SkinnedMeshRenderer FaceRenderer;
    public Transform LookTarget;
    [Tooltip("Head bone. Gaze is solved against this so the eyes hold their target while the head moves, the way the vestibulo-ocular reflex does. Auto-found from the Animator if left empty.")]
    public Transform HeadBone;

    [Header("Gaze")]
    // Gaze needs both halves. The eye bones rotate the eyeball, which is the actual look
    // direction; the eyeLook blend shapes carry the lid and eye-region deformation that
    // goes with it - looking down pulls the upper lid down with the eye. Bones alone give
    // eyeballs sliding under static lids; shapes alone leave the eyes pointing forward.
    [Tooltip("Rotate the eye bones. Auto-found under the head if left empty.")]
    public Transform EyeBoneL;
    public Transform EyeBoneR;
    [Tooltip("Flip if the eyes look up when they should look down.")]
    public bool InvertEyePitch = false;
    [Tooltip("Flip if the eyes look left when they should look right.")]
    public bool InvertEyeYaw = false;

    [Range(0f, 100f)] public float GazeRange = 100f;
    // A real eye comfortably reaches ~30 degrees, but a stylised avatar has a much larger
    // eyeball, so the pupil travels far further across the visible eye for the same angle -
    // anatomically correct values look wild. Vertical is tighter still: people move their
    // head rather than their eyes for most vertical looking, and a large upward deflection
    // reads as rolling the eyes back.
    [Tooltip("Maximum horizontal eye deflection in degrees.")]
    public float GazeMaxYawDegrees = 11f;
    [Tooltip("Maximum vertical eye deflection in degrees. Smaller than horizontal, but not so small that simply resting on a target below eye level already saturates it - a camera at chest-to-chin height sits several degrees down before any gaze behaviour is applied.")]
    public float GazeMaxPitchDegrees = 9f;
    [Tooltip("Deflection at which the eyeLook blend shapes reach full weight.")]
    public float GazeFullScaleDegrees = 14f;
    [Tooltip("Saccade duration. Real saccades run 30-80ms - fast enough to read as instantaneous.")]
    public float SaccadeDuration = 0.05f;

    [Header("Fixational motion")]
    [Tooltip("Microsaccades per second. Physiological range is 1-2; these are the discrete flicks that keep a fixating eye alive.")]
    public float MicrosaccadeRateHz = 1.5f;
    [Tooltip("Microsaccade amplitude in degrees. Physiological range is roughly 0.02-2; small values read as aliveness, large ones as twitchiness.")]
    public float MicrosaccadeDegrees = 0.32f;
    [Tooltip("Slow drift between microsaccades, degrees per second.")]
    public float DriftDegreesPerSec = 0.12f;
    [Tooltip("Ocular tremor amplitude in degrees. True value is ~0.004, which is below display resolution - included for completeness, not effect.")]
    public float TremorDegrees = 0.004f;

    [Header("Gaze policy per state")]
    [Tooltip("Probability a saccade lands on the partner's face, while listening. Aversion is rare here.")]
    [Range(0f, 1f)] public float ListeningFaceBias = 0.88f;
    [Tooltip("While thinking. Gaze aversion peaks during cognitive effort, so this is deliberately low.")]
    [Range(0f, 1f)] public float ThinkingFaceBias = 0.25f;
    [Tooltip("While speaking. In human dyads speakers alternate roughly evenly, but an agent addressing one person directly reads as evasive at that rate - and eye contact is what makes an agent read as engaged rather than absent. Biased above the human figure deliberately.")]
    [Range(0f, 1f)] public float SpeakingFaceBias = 0.72f;
    [Range(0f, 1f)] public float IdleFaceBias = 0.6f;

    [Header("Turn-taking")]
    [Tooltip("Speakers look away as a turn begins and come back to the partner around here.")]
    public float TurnStartAversionSeconds = 0.7f;
    [Tooltip("A turn ends with direct gaze at the partner - the cue that hands the floor over.")]
    public float TurnEndGazeHoldSeconds = 1.2f;

    [Header("Fixation dwell")]
    // Fixations on the partner and glances away are not the same length. Listeners hold
    // long gazes at the speaker broken by brief glances aside, and speakers alternate but
    // with short aversions too. Using one dwell range for both meant a 55% face bias still
    // left her looking away about half the *time*, which reads as evasive even though the
    // ratio of glances was right.
    [Tooltip("How long she rests on the partner's face.")]
    public float FaceFixationMin = 1.6f;
    public float FaceFixationMax = 4.2f;
    [Tooltip("How long a glance away lasts. Deliberately much shorter than a face fixation.")]
    public float AversionMin = 0.4f;
    public float AversionMax = 1.1f;
    [Tooltip("Fixations shorten while speaking.")]
    public float SpeakingFixationScale = 0.72f;
    [Tooltip("How far off-target an aversion lands, in metres at the partner's distance. Kept small enough that glances stay inside the eyes' range - anything larger clamps at the limit, which makes every aversion look identical.")]
    public float GlanceRadius = 0.17f;
    [Tooltip("Thinking aversions skew upward - the classic memory-retrieval look. Small, because vertical eye range is tight; most of a real upward glance is carried by the head.")]
    public float ThinkingUpwardBias = 0.08f;

    [Header("Blink")]
    public float BlinkIntervalMin = 2.2f;
    public float BlinkIntervalMax = 6.0f;
    [Tooltip("Chance a phrase boundary triggers a blink. Blinks cluster at ends of vocalizations.")]
    [Range(0f, 1f)] public float BlinkAtPhraseEndChance = 0.55f;
    public float BlinkCloseTime = 0.045f;
    public float BlinkOpenTime = 0.13f;
    [Range(0f, 1f)] public float HalfBlinkChance = 0.28f;
    [Range(0f, 1f)] public float DoubleBlinkChance = 0.15f;
    [Tooltip("Blinks often accompany a gaze shift; this fires one alongside a saccade.")]
    [Range(0f, 1f)] public float BlinkWithSaccadeChance = 0.18f;

    [Header("Lids")]
    [Tooltip("Upper lids follow vertical gaze - looking down lowers them.")]
    [Range(0f, 100f)] public float LidGazeAmount = 30f;

    [Header("Duchenne")]
    [Tooltip("Smiles crinkle the eyes. Mouth-only smiles are a documented uncanny trigger.")]
    [Range(0f, 100f)] public float SmileSquintAmount = 45f;
    [Tooltip("Seconds the eye crinkle takes to fade after the mouth stops smiling. Offset duration is what viewers read as genuineness - a smile that drops off instantly is the signature of a posed one, and the mouth curves alone would snap it off.")]
    public float SmileOffsetSeconds = 1.1f;

    [Header("Laughter")]
    [Tooltip("Eye closure at full laugh. Laughter drives strong orbicularis closure - AU43 - which is most of what separates a laugh from a wide smile.")]
    [Range(0f, 100f)] public float LaughEyeCloseAmount = 62f;
    [Tooltip("Cheek raise at full laugh (AU6).")]
    [Range(0f, 100f)] public float LaughCheekSquintAmount = 85f;
    [Tooltip("Brow raise at full laugh. Brows lift early in a bout then settle.")]
    [Range(0f, 100f)] public float LaughBrowAmount = 30f;

    [Header("Brows from prosody")]
    [Tooltip("Continuous brow response to pitch. Kept modest; the accent raises below carry most of the read.")]
    [Range(0f, 100f)] public float PitchBrowAmount = 18f;
    [Tooltip("Discrete brow accents on prominent pitch peaks.")]
    [Range(0f, 100f)] public float BrowAccentAmount = 30f;
    [Tooltip("Share of prominent pitch peaks that actually get a brow accent. Real speakers are around 0.7 - a 1:1 mapping looks mechanical.")]
    [Range(0f, 1f)] public float BrowAccentProbability = 0.7f;
    [Tooltip("Pitch above this counts as a peak worth accenting.")]
    [Range(0f, 1f)] public float BrowAccentPitchThreshold = 0.62f;
    [Range(0f, 100f)] public float BrowIdleDrift = 8f;
    public float BrowDriftSpeed = 0.2f;

    [Header("Asymmetry")]
    [Range(0f, 0.5f)] public float Asymmetry = 0.1f;

    public Mode CurrentMode { get; private set; } = Mode.Idle;

    // Conversational role supplied by something other than ChatdollKit's dialog loop, such
    // as a scripted scene that listens and thinks on its own schedule. Takes precedence over
    // the dialog status; actual speech still wins, since that is observed rather than told.
    public static Mode? ExternalMode;

    // --- shape indices, -1 when absent ---
    private int iLookInL = -1, iLookOutL = -1, iLookUpL = -1, iLookDownL = -1;
    private int iLookInR = -1, iLookOutR = -1, iLookUpR = -1, iLookDownR = -1;
    private int iBlinkL = -1, iBlinkR = -1, iSquintL = -1, iSquintR = -1, iWideL = -1, iWideR = -1;
    private int iBrowInnerUp = -1, iBrowOuterUpL = -1, iBrowOuterUpR = -1, iBrowDownL = -1, iBrowDownR = -1;
    private int iSmileL = -1, iSmileR = -1;

    private float[] baseline;
    private readonly Dictionary<int, float> writes = new Dictionary<int, float>();
    private readonly List<int> lastWritten = new List<int>();

    private DialogController dialog;
    private SaidBlendshapePlayer speech;
    private SocialReactions reactions;

    // Gaze is kept as a world-space anchor, then resolved into head-relative degrees each
    // frame. Storing degrees directly would make head motion drag the gaze off target.
    private Vector3 gazeAnchor;
    private Vector2 gazeFromDeg, gazeToDeg, gazeCurrentDeg;
    private float saccadeStartedAt = -1f;
    private float nextSaccadeAt;
    private Vector2 driftDeg;
    private float nextMicrosaccadeAt;
    private Vector2 microsaccadeOffset;

    private Mode previousMode = Mode.Idle;
    private float modeEnteredAt;
    private bool turnStartAversionDone;

    private float nextBlinkAt;
    private float blinkPhase = -1f;
    private float blinkDepth = 1f;
    private bool queuedDoubleBlink;

    private float browSeed, tremorSeed;
    private float browAccentPhase = -1f, browAccentDuration, browAccentStrength;
    private bool pitchWasBelowThreshold = true;

    private float asymL = 1f, asymR = 1f;
    private float smileHoldL, smileHoldR;
    private bool lookingAtFace = true;
    private Quaternion eyeRestL = Quaternion.identity, eyeRestR = Quaternion.identity;

    private static Transform FindChild(Transform root, params string[] nameFragmentsLower)
    {
        foreach (Transform c in root)
        {
            var n = c.name.ToLowerInvariant();
            foreach (var frag in nameFragmentsLower)
            {
                if (n.Contains(frag)) return c;
            }
            var found = FindChild(c, nameFragmentsLower);
            if (found != null) return found;
        }
        return null;
    }

    private void Start()
    {
        if (FaceRenderer == null || FaceRenderer.sharedMesh == null)
        {
            Debug.LogError("ArkitFaceAnimator: FaceRenderer not assigned.");
            enabled = false;
            return;
        }

        if (LookTarget == null && Camera.main != null) LookTarget = Camera.main.transform;

        var animator = GetComponent<Animator>();
        if (HeadBone == null && animator != null && animator.isHuman)
        {
            HeadBone = animator.GetBoneTransform(HumanBodyBones.Head);
        }

        // Eyes are optional Humanoid bones and frequently left unmapped even when present,
        // so fall back to a name search under the head.
        if (EyeBoneL == null && animator != null && animator.isHuman) EyeBoneL = animator.GetBoneTransform(HumanBodyBones.LeftEye);
        if (EyeBoneR == null && animator != null && animator.isHuman) EyeBoneR = animator.GetBoneTransform(HumanBodyBones.RightEye);
        if (EyeBoneL == null && HeadBone != null) EyeBoneL = FindChild(HeadBone, "eye_l", "lefteye", "eyeleft");
        if (EyeBoneR == null && HeadBone != null) EyeBoneR = FindChild(HeadBone, "eye_r", "righteye", "eyeright");

        if (EyeBoneL != null) eyeRestL = EyeBoneL.localRotation;
        if (EyeBoneR != null) eyeRestR = EyeBoneR.localRotation;

        if (EyeBoneL == null || EyeBoneR == null)
        {
            Debug.LogWarning($"ArkitFaceAnimator: eye bones not found (L={EyeBoneL}, R={EyeBoneR}) - gaze will only deform the lids, the eyes themselves will not turn.");
        }

        dialog = FindObjectOfType<DialogController>();
        speech = SaidBlendshapePlayer.Instance;
        reactions = GetComponent<SocialReactions>() ?? FindObjectOfType<SocialReactions>();

        CacheIndices();

        browSeed = Random.Range(0f, 1000f);
        tremorSeed = Random.Range(0f, 1000f);
        RollAsymmetry();

        gazeAnchor = LookTarget != null ? LookTarget.position : transform.position + transform.forward * 2f;
        modeEnteredAt = Time.time;
        ScheduleSaccade();
        ScheduleBlink();
        nextMicrosaccadeAt = Time.time + Random.Range(0.3f, 1.0f);
    }

    private void CacheIndices()
    {
        var mesh = FaceRenderer.sharedMesh;
        var lookup = new Dictionary<string, int>();
        for (var i = 0; i < mesh.blendShapeCount; i++) lookup[mesh.GetBlendShapeName(i)] = i;
        int F(string n) => lookup.TryGetValue(n, out var i) ? i : -1;

        iLookInL = F("eyeLookInLeft"); iLookOutL = F("eyeLookOutLeft");
        iLookUpL = F("eyeLookUpLeft"); iLookDownL = F("eyeLookDownLeft");
        iLookInR = F("eyeLookInRight"); iLookOutR = F("eyeLookOutRight");
        iLookUpR = F("eyeLookUpRight"); iLookDownR = F("eyeLookDownRight");
        iBlinkL = F("eyeBlinkLeft"); iBlinkR = F("eyeBlinkRight");
        iSquintL = F("eyeSquintLeft"); iSquintR = F("eyeSquintRight");
        iWideL = F("eyeWideLeft"); iWideR = F("eyeWideRight");
        iBrowInnerUp = F("browInnerUp");
        iBrowOuterUpL = F("browOuterUpLeft"); iBrowOuterUpR = F("browOuterUpRight");
        iBrowDownL = F("browDownLeft"); iBrowDownR = F("browDownRight");
        iSmileL = F("mouthSmileLeft"); iSmileR = F("mouthSmileRight");

        baseline = new float[mesh.blendShapeCount];
        for (var i = 0; i < mesh.blendShapeCount; i++) baseline[i] = FaceRenderer.GetBlendShapeWeight(i);
    }

    private void RollAsymmetry()
    {
        asymL = 1f + Random.Range(-Asymmetry, Asymmetry);
        asymR = 1f + Random.Range(-Asymmetry, Asymmetry);
    }

    // ChatdollKit already tracks the conversational role; deriving mode from it is far more
    // reliable than inferring one from audio activity alone.
    private Mode ResolveMode()
    {
        if (speech == null) speech = SaidBlendshapePlayer.Instance;
        if (speech != null && speech.IsSpeaking) return Mode.Speaking;

        if (ExternalMode.HasValue) return ExternalMode.Value;

        if (dialog != null)
        {
            switch (dialog.Status)
            {
                case DialogController.DialogStatus.Responding: return Mode.Speaking;
                case DialogController.DialogStatus.Processing:
                case DialogController.DialogStatus.PreparingFirstTurn:
                case DialogController.DialogStatus.Initializing: return Mode.Thinking;
                case DialogController.DialogStatus.Listening:
                case DialogController.DialogStatus.TurnStarted: return Mode.Listening;
            }
        }
        return Mode.Idle;
    }

    private void LateUpdate()
    {
        if (FaceRenderer == null || baseline == null) return;

        CurrentMode = ResolveMode();
        if (CurrentMode != previousMode)
        {
            OnModeChanged(previousMode, CurrentMode);
            previousMode = CurrentMode;
            modeEnteredAt = Time.time;
        }

        writes.Clear();
        UpdateGaze();
        UpdateBlink();
        UpdateDuchenne();
        UpdateBrows();
        UpdateLaughter();
        Flush();
    }

    private void OnModeChanged(Mode from, Mode to)
    {
        if (to == Mode.Speaking)
        {
            // Turn onset: look away first, then come back to the partner. Holding eye
            // contact from the first syllable is one of the more obvious artificial tells.
            turnStartAversionDone = false;
            SetAnchorAverted();
            ScheduleSaccade();
        }
        else if (from == Mode.Speaking)
        {
            // Turn hand-off: settle on the partner and hold, which is what actually yields
            // the floor.
            gazeAnchor = FacePoint(0.04f);
            nextSaccadeAt = Time.time + TurnEndGazeHoldSeconds;
            StartSaccadeTo(gazeAnchor);
        }
    }

    private Vector3 FacePoint(float spread)
    {
        lookingAtFace = true;
        var basePoint = LookTarget != null ? LookTarget.position : transform.position + transform.forward * 2f;
        // Gaze wanders the eye/mouth triangle rather than locking one exact point.
        return basePoint + Random.insideUnitSphere * spread;
    }

    private void SetAnchorAverted()
    {
        lookingAtFace = false;
        var basePoint = LookTarget != null ? LookTarget.position : transform.position + transform.forward * 2f;
        var offset = Random.insideUnitSphere * GlanceRadius;
        if (CurrentMode == Mode.Thinking) offset.y += ThinkingUpwardBias;
        gazeAnchor = basePoint + offset;
    }

    private float FaceBiasForMode()
    {
        // While the user is actually mid-sentence, hold gaze on them almost completely.
        // Glancing away while someone is talking to you reads as disinterest, and it is one
        // of the few gaze behaviours people consciously notice.
        if (reactions != null && reactions.UserIsSpeaking) return 0.95f;

        switch (CurrentMode)
        {
            case Mode.Listening: return ListeningFaceBias;
            case Mode.Thinking: return ThinkingFaceBias;
            case Mode.Speaking: return SpeakingFaceBias;
            default: return IdleFaceBias;
        }
    }

    // Dwell depends on what she is currently looking at, not just on mode.
    private void ScheduleSaccade()
    {
        var scale = CurrentMode == Mode.Speaking ? SpeakingFixationScale : 1f;
        var dwell = lookingAtFace
            ? Random.Range(FaceFixationMin, FaceFixationMax)
            : Random.Range(AversionMin, AversionMax);
        nextSaccadeAt = Time.time + dwell * scale;
    }

    private void PickNewAnchor()
    {
        // Within the first stretch of her own turn the gaze stays away regardless of the
        // usual bias, then returns - the 700ms pattern speakers actually produce.
        if (CurrentMode == Mode.Speaking && !turnStartAversionDone)
        {
            if (Time.time - modeEnteredAt < TurnStartAversionSeconds)
            {
                SetAnchorAverted();
                StartSaccadeTo(gazeAnchor);
                return;
            }
            turnStartAversionDone = true;
            gazeAnchor = FacePoint(0.05f);
            StartSaccadeTo(gazeAnchor);
            return;
        }

        if (Random.value < FaceBiasForMode()) gazeAnchor = FacePoint(0.07f);
        else SetAnchorAverted();

        StartSaccadeTo(gazeAnchor);
    }

    private void StartSaccadeTo(Vector3 worldPoint)
    {
        gazeFromDeg = gazeCurrentDeg;
        gazeToDeg = WorldToHeadRelativeDegrees(worldPoint);
        saccadeStartedAt = Time.time;
        RollAsymmetry();

        // A saccade resets fixational state: drift restarts from the new anchor and the
        // microsaccade schedule begins again.
        driftDeg = Vector2.zero;
        nextMicrosaccadeAt = Time.time + Random.Range(0.4f, 1.2f) / Mathf.Max(0.01f, MicrosaccadeRateHz);

        if (Random.value < BlinkWithSaccadeChance && blinkPhase < 0f) TriggerBlink();
    }

    // Solving against the head each frame is what gives vestibulo-ocular behaviour for
    // free: as the head turns, the required deflection changes, so the eyes counter-rotate
    // and the target stays fixed. Solving against the root instead lets head sway drag the
    // gaze off the target, which is the single most noticeable "not really looking at you"
    // artifact.
    private Vector2 WorldToHeadRelativeDegrees(Vector3 point)
    {
        // Origin must be the head, not the avatar root - the root sits at her feet, so
        // measuring from there put a camera at eye height roughly 1.5m "above" her and
        // pinned the gaze at full upward deflection.
        var origin = EyeBoneL != null ? EyeBoneL.position
                   : HeadBone != null ? HeadBone.position
                   : transform.position;

        var dir = point - origin;
        if (dir.sqrMagnitude < 1e-8f) return Vector2.zero;

        // Angles are taken in the avatar's own frame, which has a well-defined forward,
        // rather than the head bone's - bone axis conventions vary between rigs and the
        // head's local forward is frequently not +Z.
        var local = transform.InverseTransformDirection(dir);
        var horizontal = new Vector2(local.x, local.z).magnitude;

        var yaw = Mathf.Atan2(local.x, Mathf.Max(0.001f, local.z)) * Mathf.Rad2Deg;
        var pitch = Mathf.Atan2(local.y, Mathf.Max(0.001f, horizontal)) * Mathf.Rad2Deg;

        return new Vector2(
            Mathf.Clamp(yaw, -GazeMaxYawDegrees, GazeMaxYawDegrees),
            Mathf.Clamp(pitch, -GazeMaxPitchDegrees, GazeMaxPitchDegrees));
    }

    private void UpdateGaze()
    {
        // Being addressed interrupts whatever the gaze was doing. This is an orienting
        // reflex - it preempts the fixation schedule rather than waiting for it, which is
        // the difference between her noticing you and her happening to look over later.
        if (reactions != null && reactions.UserJustStartedSpeaking)
        {
            gazeAnchor = FacePoint(0.03f);
            StartSaccadeTo(gazeAnchor);
            ScheduleSaccade();
        }
        else if (Time.time >= nextSaccadeAt)
        {
            PickNewAnchor();
            ScheduleSaccade();
        }

        var anchorDeg = WorldToHeadRelativeDegrees(gazeAnchor);

        if (saccadeStartedAt >= 0f)
        {
            // Ballistic: cross the gap in a fixed short time and stop. Easing toward the
            // target the way a lerp does produces a slow pan no real eye makes.
            var k = Mathf.Clamp01((Time.time - saccadeStartedAt) / Mathf.Max(0.001f, SaccadeDuration));
            gazeCurrentDeg = Vector2.Lerp(gazeFromDeg, anchorDeg, k * k * (3f - 2f * k));
            if (k >= 1f) saccadeStartedAt = -1f;
        }
        else
        {
            // Between saccades the eye is not still: it drifts, and is periodically pulled
            // back by a microsaccade.
            gazeCurrentDeg = anchorDeg;

            driftDeg += new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, 1f)) *
                        DriftDegreesPerSec * Time.deltaTime;
            driftDeg = Vector2.ClampMagnitude(driftDeg, DriftDegreesPerSec * 2f);

            if (Time.time >= nextMicrosaccadeAt)
            {
                // Discrete corrective flick, largely opposing accumulated drift.
                microsaccadeOffset = (-driftDeg.normalized * MicrosaccadeDegrees) +
                                     new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, 1f)) * MicrosaccadeDegrees * 0.4f;
                driftDeg = Vector2.zero;
                nextMicrosaccadeAt = Time.time + Random.Range(0.6f, 1.4f) / Mathf.Max(0.01f, MicrosaccadeRateHz);
            }

            // Microsaccade offset decays over roughly a flick's duration.
            microsaccadeOffset = Vector2.Lerp(microsaccadeOffset, Vector2.zero, Time.deltaTime * 12f);
        }

        var tremor = new Vector2(
            (Mathf.PerlinNoise(tremorSeed + Time.time * 60f, 0f) - 0.5f) * 2f,
            (Mathf.PerlinNoise(tremorSeed + Time.time * 60f, 40f) - 0.5f) * 2f) * TremorDegrees;

        // Clamp again after the fixational terms: drift and microsaccades are added on top
        // of an already-clamped anchor and would otherwise push past the mechanical limit.
        var final = gazeCurrentDeg + driftDeg + microsaccadeOffset + tremor;
        final.x = Mathf.Clamp(final.x, -GazeMaxYawDegrees, GazeMaxYawDegrees);
        final.y = Mathf.Clamp(final.y, -GazeMaxPitchDegrees, GazeMaxPitchDegrees);
        ApplyGazeShapes(final);
    }

    private void ApplyGazeShapes(Vector2 deg)
    {
        var h = Mathf.Clamp(deg.x / GazeFullScaleDegrees, -1f, 1f);
        var v = Mathf.Clamp(deg.y / GazeFullScaleDegrees, -1f, 1f);

        ApplyEyeBones(deg);

        // Horizontal gaze is "in" for the eye on the far side and "out" for the near one;
        // that pairing is what the ARKit shapes encode.
        if (h > 0f)
        {
            Write(iLookOutR, h * GazeRange * asymR);
            Write(iLookInL, h * GazeRange * asymL);
        }
        else
        {
            Write(iLookOutL, -h * GazeRange * asymL);
            Write(iLookInR, -h * GazeRange * asymR);
        }

        var widen = reactions != null ? reactions.EyeWidenBoost : 0f;
        if (widen > 0.01f)
        {
            Write(iWideL, widen * asymL);
            Write(iWideR, widen * asymR);
        }

        if (v > 0f)
        {
            Write(iLookUpL, v * GazeRange * asymL);
            Write(iLookUpR, v * GazeRange * asymR);
            Write(iWideL, v * LidGazeAmount * 0.5f);
            Write(iWideR, v * LidGazeAmount * 0.5f);
        }
        else
        {
            Write(iLookDownL, -v * GazeRange * asymL);
            Write(iLookDownR, -v * GazeRange * asymR);
            // Lowered lids when looking down - the clearest cue that eyes belong to a face.
            Write(iBlinkL, -v * LidGazeAmount * 0.45f);
            Write(iBlinkR, -v * LidGazeAmount * 0.45f);
        }
    }

    // Rotates the eyeballs to the same gaze the shapes are deforming the lids toward.
    // Applied as a local offset from the rest pose captured at Start, and because the
    // deflection was solved against the head, head motion is already compensated - the
    // eyes hold their target as the head turns.
    // Each eye is solved against the target separately, which is what makes them converge.
    // The eyes sit ~6cm apart, so at conversational distance - and the camera here is only
    // ~0.4m out - they need noticeably different angles. Driving both from one eye's
    // solution leaves the other aimed past the viewer, which reads as her looking somewhere
    // just beside you rather than at you.
    private void ApplyEyeBones(Vector2 deg)
    {
        if (EyeBoneL == null && EyeBoneR == null) return;

        // Reconstruct the point being looked at from the (already clamped and jittered)
        // angles, so drift, microsaccades and aversions all carry through to both eyes.
        var distance = LookTarget != null
            ? Mathf.Max(0.15f, Vector3.Distance(EyeCentre(), LookTarget.position))
            : 2f;
        var dirLocal = Quaternion.Euler(-deg.y, deg.x, 0f) * Vector3.forward;
        var point = EyeCentre() + transform.TransformDirection(dirLocal) * distance;

        AimEye(EyeBoneL, point);
        AimEye(EyeBoneR, point);
    }

    private Vector3 EyeCentre()
    {
        if (EyeBoneL != null && EyeBoneR != null) return (EyeBoneL.position + EyeBoneR.position) * 0.5f;
        if (EyeBoneL != null) return EyeBoneL.position;
        if (EyeBoneR != null) return EyeBoneR.position;
        return HeadBone != null ? HeadBone.position : transform.position;
    }

    private void AimEye(Transform eye, Vector3 point)
    {
        if (eye == null) return;

        var dir = point - eye.position;
        if (dir.sqrMagnitude < 1e-8f) return;

        var local = transform.InverseTransformDirection(dir);
        var horizontal = new Vector2(local.x, local.z).magnitude;
        var yaw = Mathf.Atan2(local.x, Mathf.Max(0.001f, local.z)) * Mathf.Rad2Deg;
        var pitch = Mathf.Atan2(local.y, Mathf.Max(0.001f, horizontal)) * Mathf.Rad2Deg;

        yaw = Mathf.Clamp(yaw, -GazeMaxYawDegrees, GazeMaxYawDegrees);
        pitch = Mathf.Clamp(pitch, -GazeMaxPitchDegrees, GazeMaxPitchDegrees);

        if (InvertEyePitch) pitch = -pitch;
        if (InvertEyeYaw) yaw = -yaw;

        // World rotation rather than local: the angles are solved in the avatar's frame, so
        // composing onto the avatar's rotation lands the eye on target no matter how the
        // head is currently turned - vestibulo-ocular behaviour for free. Zero deflection
        // gives exactly the avatar's forward, matching the rig's convention that unrotated
        // eye bones look straight ahead.
        eye.rotation = transform.rotation * Quaternion.Euler(-pitch, yaw, 0f);
    }

    private void TriggerBlink()
    {
        blinkPhase = 0f;
        blinkDepth = Random.value < HalfBlinkChance ? Random.Range(0.45f, 0.7f) : 1f;
        queuedDoubleBlink = Random.value < DoubleBlinkChance;
    }

    private void ScheduleBlink()
    {
        nextBlinkAt = Time.time + Random.Range(BlinkIntervalMin, BlinkIntervalMax);
    }

    private void UpdateBlink()
    {
        // Phrase boundaries are the natural place for a blink - it lands in the gap rather
        // than mid-vowel, which is where the timer alone would eventually put it.
        if (blinkPhase < 0f && speech != null && speech.AtPhraseBoundary &&
            Random.value < BlinkAtPhraseEndChance)
        {
            TriggerBlink();
        }
        else if (blinkPhase < 0f && Time.time >= nextBlinkAt)
        {
            TriggerBlink();
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
            // Opening is slower than closing; that asymmetry is most of what sells a blink.
            amount = 1f - (blinkPhase - BlinkCloseTime) / BlinkOpenTime;
        }
        else
        {
            blinkPhase = -1f;
            if (queuedDoubleBlink)
            {
                queuedDoubleBlink = false;
                nextBlinkAt = Time.time + Random.Range(0.08f, 0.15f);
            }
            else ScheduleBlink();
            return;
        }

        var w = Mathf.Clamp01(amount) * blinkDepth * 100f;
        Write(iBlinkL, w * asymL);
        Write(iBlinkR, w * asymR * 0.97f);
    }

    // Reading the smile channels off the renderer keeps this in step with whatever the
    // speech curves are doing without the two systems knowing about each other.
    //
    // The eye component rises with the mouth but decays on its own slow timer rather than
    // tracking the mouth back down. Offset duration is the part observers actually read as
    // genuineness - a smile whose eyes release the instant the mouth does is the signature
    // of a posed one, and the speech curves drop the mouth abruptly at every phrase end.
    private void UpdateDuchenne()
    {
        if (SmileSquintAmount <= 0f || iSmileL < 0) return;

        var sL = FaceRenderer.GetBlendShapeWeight(iSmileL) / 100f;
        var sR = iSmileR >= 0 ? FaceRenderer.GetBlendShapeWeight(iSmileR) / 100f : sL;

        var decay = Time.deltaTime / Mathf.Max(0.01f, SmileOffsetSeconds);
        smileHoldL = sL > smileHoldL ? sL : Mathf.MoveTowards(smileHoldL, sL, decay);
        smileHoldR = sR > smileHoldR ? sR : Mathf.MoveTowards(smileHoldR, sR, decay);

        if (smileHoldL > 0.02f) Write(iSquintL, smileHoldL * SmileSquintAmount * asymL);
        if (smileHoldR > 0.02f) Write(iSquintR, smileHoldR * SmileSquintAmount * asymR);
    }

    // Laughter is layered over everything else because it co-occurs with speech rather than
    // replacing it - the mouth keeps articulating the "ha" calls from the generated curves
    // while the eyes, cheeks and brows carry the parts those curves cannot express.
    private void UpdateLaughter()
    {
        var laugh = speech != null ? speech.LaughterIntensity : 0f;
        if (laugh <= 0.01f) return;

        // Eye closure and cheek raise are what distinguish a laugh from a broad smile.
        Write(iBlinkL, laugh * LaughEyeCloseAmount * asymL);
        Write(iBlinkR, laugh * LaughEyeCloseAmount * asymR);
        Write(iSquintL, laugh * LaughCheekSquintAmount * asymL);
        Write(iSquintR, laugh * LaughCheekSquintAmount * asymR);

        Write(iBrowInnerUp, laugh * LaughBrowAmount * 0.6f);
        Write(iBrowOuterUpL, laugh * LaughBrowAmount * asymL);
        Write(iBrowOuterUpR, laugh * LaughBrowAmount * asymR);

        // Blinking on top of an already-closed eye would just look like a twitch.
        if (laugh > 0.4f && blinkPhase >= 0f) blinkPhase = -1f;
    }

    private void UpdateBrows()
    {
        var pitch = speech != null ? speech.Pitch : 0f;
        var speaking = CurrentMode == Mode.Speaking;

        // Discrete accents on prominent pitch peaks, fired on the rising edge and only
        // some of the time - a continuous 1:1 pitch follow reads as mechanical.
        if (speaking)
        {
            if (pitch > BrowAccentPitchThreshold && pitchWasBelowThreshold)
            {
                pitchWasBelowThreshold = false;
                if (browAccentPhase < 0f && Random.value < BrowAccentProbability)
                {
                    browAccentPhase = 0f;
                    browAccentDuration = Random.Range(0.28f, 0.5f);
                    browAccentStrength = BrowAccentAmount * Random.Range(0.7f, 1.15f);
                }
            }
            else if (pitch < BrowAccentPitchThreshold - 0.08f)
            {
                pitchWasBelowThreshold = true;
            }
        }

        var accent = 0f;
        if (browAccentPhase >= 0f)
        {
            browAccentPhase += Time.deltaTime;
            if (browAccentPhase >= browAccentDuration) browAccentPhase = -1f;
            else accent = Mathf.Sin(Mathf.PI * (browAccentPhase / browAccentDuration)) * browAccentStrength;
        }

        // Continuous component, centred so mid pitch is neutral and low pitch lowers slightly.
        var pitchComponent = speaking ? (pitch - 0.5f) * 2f * PitchBrowAmount : 0f;

        // Thinking pulls the inner brows together and down - the concentration look.
        var thinking = CurrentMode == Mode.Thinking ? -BrowIdleDrift * 1.2f : 0f;

        var driftScale = speaking ? 0.4f : 1f;
        var t = Time.time * BrowDriftSpeed;
        var driftL = (Mathf.PerlinNoise(browSeed + t, 0f) - 0.5f) * 2f * BrowIdleDrift * driftScale;
        var driftR = (Mathf.PerlinNoise(browSeed + t, 60f) - 0.5f) * 2f * BrowIdleDrift * driftScale;

        // Attention, approach and backchannel brow raises, added on top rather than
        // replacing the speech-driven component so a reaction during speech still reads.
        var reactionBrow = reactions != null ? reactions.BrowBoost : 0f;

        var signedL = driftL + pitchComponent + accent + thinking + reactionBrow;
        var signedR = driftR + pitchComponent + accent * 0.93f + thinking + reactionBrow * 0.95f;

        Write(iBrowInnerUp, Mathf.Max(0f, (signedL + signedR) * 0.5f) * 0.85f);

        if (signedL >= 0f) Write(iBrowOuterUpL, signedL * asymL);
        else Write(iBrowDownL, -signedL * asymL);

        if (signedR >= 0f) Write(iBrowOuterUpR, signedR * asymR);
        else Write(iBrowDownR, -signedR * asymR);
    }

    private void Write(int index, float weight)
    {
        if (index < 0) return;
        writes.TryGetValue(index, out var existing);
        writes[index] = existing + weight;
    }

    private void Flush()
    {
        foreach (var idx in lastWritten)
        {
            if (!writes.ContainsKey(idx) && idx < baseline.Length)
                FaceRenderer.SetBlendShapeWeight(idx, baseline[idx]);
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

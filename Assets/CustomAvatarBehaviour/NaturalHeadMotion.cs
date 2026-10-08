using UnityEngine;

// Turns the Humanoid head bone gently toward a target (defaults to the main camera),
// with a small continuous Perlin-noise drift layered on top so it doesn't look locked,
// plus a bigger sway amplitude while VoiceAudioSource is playing so the head moves a
// bit more while she's talking. Runs in LateUpdate, after the Animator, and is layered
// on top of the head's rest rotation: the pose the Animator produced this frame when a
// controller is playing, otherwise the bind pose captured at Start. Offsetting from the
// animated pose is what lets body animation and this layer coexist - offsetting from the
// static bind pose would silently erase whatever the clip did to the head.
// Runs before ArkitFaceAnimator. The eyes are children of the head and are aimed by
// setting their WORLD rotation; if the head were posed afterwards it would carry the eyes
// with it and silently undo that aim. Unity does not order LateUpdate between components,
// so the dependency is made explicit.
[DefaultExecutionOrder(-10)]
public class NaturalHeadMotion : MonoBehaviour
{
    [Header("Look Target")]
    public Transform LookTarget;
    public float HeadTurnSpeed = 3f;
    public float MaxHeadTurnAngle = 35f;

    [Header("Idle Sway")]
    public float SwayAmplitude = 3f;
    public float SwaySpeed = 0.3f;

    [Header("Talking Emphasis")]
    public AudioSource VoiceAudioSource;
    public float TalkingAmplitudeMultiplier = 2.5f;
    [Tooltip("Seconds to ease between idle and talking sway amplitude. Stepping it instantly jerks the head at every sentence boundary.")]
    public float AmplitudeBlendTime = 0.45f;

    [Header("Prosody coupling")]
    [Tooltip("Degrees of nod driven by vocal pitch. Rising pitch lifts the head, falling pitch drops it - the correlation is strong in real speakers, and it is what ties head motion to what is actually being said.")]
    public float PitchNodAmount = 4.5f;
    [Tooltip("Smaller roll component from pitch, so the motion is not a pure single-axis nod.")]
    public float PitchRollAmount = 1.8f;
    [Tooltip("Smoothing on the pitch signal. Too fast looks twitchy, too slow decouples it from the phrase.")]
    public float PitchFollowTime = 0.12f;

    [Header("Orienting")]
    [Tooltip("How much faster the head turns in the moment after being addressed.")]
    public float OrientTurnSpeedMultiplier = 3.5f;
    [Tooltip("Seconds the raised turn rate decays over.")]
    public float OrientBoostDecay = 0.8f;

    [Header("Laughter")]
    [Tooltip("Degrees the head tilts back at full laugh.")]
    public float LaughHeadBackAmount = 7f;
    [Tooltip("Degrees of side-to-side shake during a laugh.")]
    public float LaughShakeAmount = 2.5f;
    public float LaughShakeSpeed = 9f;

    private Animator animator;
    private Transform head;
    private Quaternion headRestLocalRotation;
    private Quaternion currentLookBias = Quaternion.identity;
    private float seedX, seedY, seedZ;
    private float currentAmplitude;
    private float amplitudeVelocity;
    private float smoothedPitchDrive;
    private float pitchDriveVelocity;
    private float smoothedLaugh;
    private float orientBoost;
    private SaidBlendshapePlayer speech;
    private SocialReactions reactions;

    private void Start()
    {
        animator = GetComponent<Animator>();
        if (animator == null || !animator.isHuman)
        {
            enabled = false;
            return;
        }

        head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head != null)
        {
            headRestLocalRotation = head.localRotation;
        }

        if (LookTarget == null && Camera.main != null)
        {
            LookTarget = Camera.main.transform;
        }

        seedX = Random.Range(0f, 1000f);
        seedY = Random.Range(0f, 1000f);
        seedZ = Random.Range(0f, 1000f);

        reactions = GetComponent<SocialReactions>() ?? FindObjectOfType<SocialReactions>();
    }

    private void LateUpdate()
    {
        if (head == null)
        {
            return;
        }

        // The Animator rewrites every humanoid bone each frame while a controller plays, so
        // the head's current rotation is this frame's animated pose. Without a controller
        // nothing resets it, and reading it back would accumulate our own offsets.
        var restLocalRotation = AnimatorDrivesPose() ? head.localRotation : headRestLocalRotation;

        // isPlaying flips false between every sentence while the next clip is fetched.
        // Switching the amplitude instantly there rescaled the noise offset in one frame
        // and read as the head jerking at each sentence boundary, so ease between the
        // talking and idle amplitudes instead of stepping.
        var targetAmplitude = SwayAmplitude;
        if (VoiceAudioSource != null && VoiceAudioSource.isPlaying)
        {
            targetAmplitude *= TalkingAmplitudeMultiplier;
        }
        currentAmplitude = Mathf.SmoothDamp(currentAmplitude, targetAmplitude, ref amplitudeVelocity, AmplitudeBlendTime);
        var amplitude = currentAmplitude;

        var t = Time.time * SwaySpeed;
        var pitchNoise = (Mathf.PerlinNoise(seedX + t, 0f) - 0.5f) * 2f * amplitude;
        var yawNoise = (Mathf.PerlinNoise(seedY + t, 100f) - 0.5f) * 2f * amplitude;
        var rollNoise = (Mathf.PerlinNoise(seedZ + t, 200f) - 0.5f) * 2f * amplitude * 0.5f;

        // Rigid head motion is strongly predicted by vocal F0 - most of its variance, in
        // measured speakers. Noise alone produces movement that is present but unrelated to
        // what is being said, which is exactly what reads as a disconnected talking head.
        // Pitch mostly drives the nod axis, with a smaller roll component.
        speech = SaidBlendshapePlayer.Instance;
        var pitchDrive = 0f;
        if (speech != null && speech.IsSpeaking)
        {
            pitchDrive = (speech.Pitch - 0.5f) * 2f;
        }
        smoothedPitchDrive = Mathf.SmoothDamp(smoothedPitchDrive, pitchDrive, ref pitchDriveVelocity, PitchFollowTime);

        var pitchAngle = pitchNoise - smoothedPitchDrive * PitchNodAmount;
        var rollAngle = rollNoise + smoothedPitchDrive * PitchRollAmount;

        // Laughing throws the head back and shakes it with the calls. Without this the
        // laugh stays trapped in the face, which reads as someone imitating a laugh rather
        // than producing one - it is a whole-body respiratory event.
        var laugh = speech != null ? speech.LaughterIntensity : 0f;
        if (laugh > 0.01f)
        {
            smoothedLaugh = Mathf.Lerp(smoothedLaugh, laugh, Time.deltaTime * 10f);
            pitchAngle -= smoothedLaugh * LaughHeadBackAmount;
            rollAngle += Mathf.Sin(Time.time * LaughShakeSpeed) * smoothedLaugh * LaughShakeAmount;
        }
        else
        {
            smoothedLaugh = Mathf.Lerp(smoothedLaugh, 0f, Time.deltaTime * 3f);
            pitchAngle -= smoothedLaugh * LaughHeadBackAmount;
        }

        // Listener feedback and orienting. Backchannel nods are the clearest signal that
        // she is receiving what is being said rather than merely waiting for it to end.
        if (reactions != null)
        {
            pitchAngle += reactions.NodOffsetDegrees;
            pitchAngle -= reactions.HeadBackDegrees;
        }

        var noiseOffset = Quaternion.Euler(pitchAngle, yawNoise, rollAngle);

        var desiredLookBias = Quaternion.identity;
        if (LookTarget != null)
        {
            var toTarget = LookTarget.position - head.position;
            if (toTarget.sqrMagnitude > 0.0001f)
            {
                var desiredWorldRotation = Quaternion.LookRotation(toTarget, head.parent.up);
                var localDesired = Quaternion.Inverse(head.parent.rotation) * desiredWorldRotation;
                var restRelative = Quaternion.Inverse(restLocalRotation) * localDesired;
                desiredLookBias = ClampRotation(restRelative, MaxHeadTurnAngle);
            }
        }
        // Orienting to a voice is quick - the head follows the eyes rather than easing over
        // at conversational pace, so the turn rate is briefly raised when addressed.
        if (reactions != null && reactions.UserJustStartedSpeaking) orientBoost = 1f;
        orientBoost = Mathf.Max(0f, orientBoost - Time.deltaTime / Mathf.Max(0.01f, OrientBoostDecay));

        var turnSpeed = HeadTurnSpeed * (1f + orientBoost * (OrientTurnSpeedMultiplier - 1f));
        currentLookBias = Quaternion.Slerp(currentLookBias, desiredLookBias, Time.deltaTime * turnSpeed);

        head.localRotation = restLocalRotation * currentLookBias * noiseOffset;
    }

    private bool AnimatorDrivesPose()
    {
        return animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null;
    }

    private Quaternion ClampRotation(Quaternion q, float maxAngle)
    {
        // q and -q describe the same rotation, but ToAngleAxis reports them with opposite
        // axes. Without forcing w >= 0 first, the axis flips sign as the head crosses that
        // boundary and the clamped result jumps to a mirrored pose - the head "snapping"
        // to a different position for no visible reason.
        if (q.w < 0f)
        {
            q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
        }

        q.ToAngleAxis(out var angle, out var axis);
        if (axis.sqrMagnitude < 1e-8f || float.IsNaN(axis.x) || float.IsInfinity(axis.x))
        {
            return Quaternion.identity;
        }

        if (angle > 180f)
        {
            angle -= 360f;
        }
        angle = Mathf.Clamp(angle, -maxAngle, maxAngle);
        return Quaternion.AngleAxis(angle, axis.normalized);
    }
}

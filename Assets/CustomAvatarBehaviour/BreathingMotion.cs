using UnityEngine;

// Subtle continuous breathing on the chest and shoulders.
//
// A character that goes completely still between utterances reads as artificial almost
// immediately, however good the face is - stillness is the thing viewers notice first when
// nothing is being said. Breathing is the cheapest continuous signal that fixes it.
//
// Speech is produced on the exhale, so the cycle is not a symmetric sine: while speaking,
// the chest holds higher and releases gradually, and a quick inhale is taken at phrase
// boundaries. That inhale is also a genuine conversational cue - listeners read it as
// "about to speak".
//
// Runs in LateUpdate after the Animator, layering rotation offsets over the animated pose
// when a controller is playing, or over the bind pose when none is.
// Runs before the head and eye layers: it rotates the chest, which the head and eyes hang
// from, so posing it afterwards would drag them off their solved aim.
[DefaultExecutionOrder(-20)]
public class BreathingMotion : MonoBehaviour
{
    [Header("Rate")]
    [Tooltip("Breaths per minute at rest. Adult resting rate is around 12-16.")]
    public float RestingRate = 14f;
    [Tooltip("Breathing speeds up slightly while speaking.")]
    public float SpeakingRateMultiplier = 1.25f;

    [Header("Amplitude")]
    [Tooltip("Degrees of chest rotation at full inhale. Should be barely perceptible - if you can clearly see it, it is too large.")]
    public float ChestAmount = 1.6f;
    [Tooltip("Shoulder lift, in degrees. Smaller than the chest movement.")]
    public float ShoulderAmount = 0.9f;

    [Header("Phrase inhale")]
    [Tooltip("Extra inhale taken when a phrase ends, as if drawing breath for the next one.")]
    public float PhraseInhaleAmount = 1.4f;
    public float PhraseInhaleDuration = 0.45f;

    private Animator animator;
    private Transform chest, shoulderL, shoulderR;
    private Quaternion chestRest, shoulderLRest, shoulderRRest;

    private float phase;
    private float inhalePhase = -1f;
    private SaidBlendshapePlayer speech;

    private void Start()
    {
        animator = GetComponent<Animator>();
        if (animator == null || !animator.isHuman)
        {
            enabled = false;
            return;
        }

        chest = animator.GetBoneTransform(HumanBodyBones.Chest)
                ?? animator.GetBoneTransform(HumanBodyBones.UpperChest)
                ?? animator.GetBoneTransform(HumanBodyBones.Spine);
        shoulderL = animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
        shoulderR = animator.GetBoneTransform(HumanBodyBones.RightShoulder);

        if (chest != null) chestRest = chest.localRotation;
        if (shoulderL != null) shoulderLRest = shoulderL.localRotation;
        if (shoulderR != null) shoulderRRest = shoulderR.localRotation;

        phase = Random.value;
        speech = SaidBlendshapePlayer.Instance;
    }

    private void LateUpdate()
    {
        if (speech == null) speech = SaidBlendshapePlayer.Instance;

        var speaking = speech != null && speech.IsSpeaking;
        var rate = RestingRate * (speaking ? SpeakingRateMultiplier : 1f) / 60f;
        phase = (phase + rate * Time.deltaTime) % 1f;

        if (speech != null && speech.AtPhraseBoundary && inhalePhase < 0f)
        {
            inhalePhase = 0f;
        }

        // Inhale is faster than exhale, so the cycle is skewed rather than sinusoidal.
        var t = phase < 0.4f
            ? Mathf.Sin(Mathf.PI * (phase / 0.4f) * 0.5f)
            : Mathf.Cos(Mathf.PI * ((phase - 0.4f) / 0.6f) * 0.5f);

        var extra = 0f;
        if (inhalePhase >= 0f)
        {
            inhalePhase += Time.deltaTime;
            if (inhalePhase >= PhraseInhaleDuration) inhalePhase = -1f;
            else extra = Mathf.Sin(Mathf.PI * (inhalePhase / PhraseInhaleDuration)) * PhraseInhaleAmount;
        }

        var breath = t * ChestAmount + extra;

        // With a controller playing, the Animator has just written this frame's pose, so that
        // is what breathing rides on. Without one, the bones still hold last frame's result and
        // the captured bind pose has to be the reference instead.
        var animated = animator.isActiveAndEnabled && animator.runtimeAnimatorController != null;

        if (chest != null)
        {
            chest.localRotation = (animated ? chest.localRotation : chestRest) * Quaternion.Euler(-breath, 0f, 0f);
        }
        if (shoulderL != null)
        {
            shoulderL.localRotation = (animated ? shoulderL.localRotation : shoulderLRest) * Quaternion.Euler(0f, 0f, -breath * (ShoulderAmount / Mathf.Max(ChestAmount, 0.01f)));
        }
        if (shoulderR != null)
        {
            shoulderR.localRotation = (animated ? shoulderR.localRotation : shoulderRRest) * Quaternion.Euler(0f, 0f, breath * (ShoulderAmount / Mathf.Max(ChestAmount, 0.01f)));
        }
    }
}

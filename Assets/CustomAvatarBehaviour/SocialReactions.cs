using UnityEngine;
using ChatdollKit.IO;

// Reactions to the other person: backchannel feedback while they speak, and orienting when
// they approach.
//
// This is the half of conversation an avatar usually lacks entirely. A character that only
// animates while it is itself talking, and goes inert the moment the user speaks, reads as
// a system waiting for input rather than someone listening. Listeners are not passive -
// they nod, raise their brows, and shift attention continuously, and that feedback is what
// makes a speaker feel heard.
//
// Two signals drive everything here:
//   - ChatdollMicrophone.CurrentVolume, a live dB level of the user's voice
//   - the distance and angle to the look target, for approach and orienting
//
// Outputs are offsets consumed by ArkitFaceAnimator and NaturalHeadMotion rather than
// direct blend shape writes, so reactions compose with speech animation instead of
// fighting it for the same channels.
public class SocialReactions : MonoBehaviour
{
    [Header("Sources")]
    public ChatdollMicrophone Microphone;
    public Transform LookTarget;

    [Header("Voice detection")]
    [Tooltip("Mic level in dB above which the user counts as speaking. CurrentVolume is roughly -99 when silent.")]
    public float SpeakingThresholdDb = -38f;
    [Tooltip("Seconds of quiet before the user is considered to have stopped.")]
    public float SilenceHangoverSeconds = 0.35f;

    // Voice activity from a better detector than the fixed threshold - one that adapts to the
    // room and ignores the avatar's own voice from the speakers. Without the echo gate she
    // nods along to herself; without the adaptive threshold a quiet microphone never
    // registers as speech and she sits still while being talked to.
    public System.Func<bool> VoiceActive;

    [Header("Listening posture")]
    [Tooltip("Head tilt while being spoken to - the attentive, 'I'm with you' angle.")]
    public float ListenTiltDegrees = 4f;
    [Tooltip("Slight forward lean of the head while listening.")]
    public float ListenLeanDegrees = 2f;
    public float ListenPostureBlendSeconds = 0.6f;
    [Tooltip("Speech at least this long earns an acknowledging nod when it ends - the listener's 'got it' before replying.")]
    public float TurnEndNodMinSpeech = 0.8f;

    [Header("Backchannel nods")]
    [Tooltip("Listeners nod during the speaker's turn, not only at the end. Nods land near prosodic boundaries, so they are triggered on dips in the speaker's level.")]
    public bool EnableBackchannel = true;
    public float NodDegrees = 3.2f;
    public float NodDuration = 0.42f;
    [Tooltip("Minimum gap between backchannel nods, so she does not bobble continuously.")]
    public float NodCooldownMin = 1.4f;
    public float NodCooldownMax = 3.2f;
    [Range(0f, 1f)] public float NodChance = 0.65f;
    [Tooltip("Chance a nod is accompanied by a brow raise, the other common backchannel signal.")]
    [Range(0f, 1f)] public float NodBrowChance = 0.45f;

    [Header("Attention on address")]
    [Tooltip("Brow raise and slight eye widen when the user starts speaking - the visible 'you have my attention' beat.")]
    [Range(0f, 100f)] public float AttentionBrowAmount = 22f;
    [Range(0f, 100f)] public float AttentionEyeWidenAmount = 16f;
    public float AttentionDecaySeconds = 1.1f;

    [Header("Approach")]
    [Tooltip("Approach speed in metres/second that counts as someone moving toward her.")]
    public float ApproachSpeedThreshold = 0.55f;
    [Tooltip("Only react to approach inside this distance.")]
    public float ApproachNoticeDistance = 3.0f;
    [Range(0f, 100f)] public float ApproachEyeWidenAmount = 34f;
    [Range(0f, 100f)] public float ApproachBrowAmount = 40f;
    public float ApproachHeadBackDegrees = 3.5f;
    public float ApproachDecaySeconds = 1.6f;
    [Tooltip("Minimum gap between approach reactions, so pacing back and forth does not retrigger constantly.")]
    public float ApproachCooldown = 4f;

    // --- outputs, read by the face and head layers ---
    public bool UserIsSpeaking { get; private set; }

    // True for the single frame the user starts talking. Orienting to a voice is a reflex,
    // not a preference - waiting for the next scheduled gaze shift to drift back means she
    // carries on looking at the wall for a second or two after being addressed.
    public bool UserJustStartedSpeaking { get; private set; }
    public float NodOffsetDegrees { get; private set; }
    public float BrowBoost { get; private set; }
    public float EyeWidenBoost { get; private set; }
    public float HeadBackDegrees { get; private set; }
    public float ListenTiltDegreesNow { get; private set; }
    public float ListenLeanDegreesNow { get; private set; }

    // Set by whoever owns the conversation while it is waiting for the user's turn, so the
    // posture is already attentive before the first word, not only once speech is detected.
    public bool ExpectingSpeech { get; set; }

    private float lastLoudAt = -99f;
    private float speechStartedAt;
    private bool wasUserSpeaking;
    private float listenPosture;
    private float tiltSign = 1f;

    private float nodPhase = -1f;
    private float nextNodAllowedAt;
    private bool nodBrow;

    private float attention;      // decaying 0-1
    private float approach;       // decaying 0-1
    private float lastApproachAt = -99f;

    private float previousDistance = -1f;
    private float smoothedLevel = -99f;
    private float levelBaseline = -99f;

    private void Start()
    {
        if (Microphone == null) Microphone = FindObjectOfType<ChatdollMicrophone>();
        if (LookTarget == null && Camera.main != null) LookTarget = Camera.main.transform;
    }

    private void Update()
    {
        UpdateVoiceState();
        UpdateBackchannel();
        UpdateApproach();

        var decayAttention = Time.deltaTime / Mathf.Max(0.01f, AttentionDecaySeconds);
        attention = Mathf.Max(0f, attention - decayAttention);

        var decayApproach = Time.deltaTime / Mathf.Max(0.01f, ApproachDecaySeconds);
        approach = Mathf.Max(0f, approach - decayApproach);

        BrowBoost = attention * AttentionBrowAmount + approach * ApproachBrowAmount + (nodBrow ? NodBrowValue() : 0f);
        EyeWidenBoost = attention * AttentionEyeWidenAmount + approach * ApproachEyeWidenAmount;
        HeadBackDegrees = approach * ApproachHeadBackDegrees;

        var posture = UserIsSpeaking || ExpectingSpeech ? 1f : 0f;
        listenPosture = Mathf.MoveTowards(listenPosture, posture, Time.deltaTime / Mathf.Max(0.05f, ListenPostureBlendSeconds));
        var eased = listenPosture * listenPosture * (3f - 2f * listenPosture);
        ListenTiltDegreesNow = eased * ListenTiltDegrees * tiltSign;
        ListenLeanDegreesNow = eased * ListenLeanDegrees;
    }

    private void UpdateVoiceState()
    {
        bool loud;
        if (VoiceActive != null)
        {
            loud = VoiceActive();
        }
        else
        {
            if (Microphone == null) return;
            var level = Microphone.CurrentVolume;
            if (float.IsNaN(level) || float.IsInfinity(level)) return;

            // Track a slow floor so the threshold adapts to the room rather than assuming a
            // fixed noise level.
            levelBaseline = levelBaseline < -90f ? level : Mathf.Lerp(levelBaseline, Mathf.Min(levelBaseline + 0.5f, level), Time.deltaTime * 0.3f);
            smoothedLevel = Mathf.Lerp(smoothedLevel < -90f ? level : smoothedLevel, level, Time.deltaTime * 12f);
            loud = smoothedLevel > SpeakingThresholdDb;
        }

        if (loud) lastLoudAt = Time.time;
        UserIsSpeaking = Time.time - lastLoudAt < SilenceHangoverSeconds;

        // The moment the user starts talking is when attention should visibly land.
        UserJustStartedSpeaking = UserIsSpeaking && !wasUserSpeaking;
        if (UserJustStartedSpeaking)
        {
            attention = 1f;
            speechStartedAt = Time.time;
            if (listenPosture < 0.05f) tiltSign = Random.value < 0.5f ? -1f : 1f;
        }

        // The end of their turn gets a nod of its own: it acknowledges what was said in the
        // gap before she answers, which is where silence otherwise reads as a system busy.
        if (wasUserSpeaking && !UserIsSpeaking && EnableBackchannel && nodPhase < 0f &&
            Time.time - speechStartedAt - SilenceHangoverSeconds >= TurnEndNodMinSpeech)
        {
            nodPhase = 0f;
            nodBrow = true;
        }
        wasUserSpeaking = UserIsSpeaking;
    }

    // A brief pause inside their turn - the audible comma that listeners nod into.
    private bool InPause()
    {
        if (VoiceActive != null) return Time.time - lastLoudAt > 0.12f;
        return smoothedLevel < SpeakingThresholdDb + 4f;
    }

    private void UpdateBackchannel()
    {
        if (nodPhase >= 0f)
        {
            nodPhase += Time.deltaTime;
            if (nodPhase >= NodDuration)
            {
                nodPhase = -1f;
                nodBrow = false;
                NodOffsetDegrees = 0f;
            }
            else
            {
                // One down-up cycle. A full sine gives the return for free, so the head
                // does not need a separate recovery.
                NodOffsetDegrees = Mathf.Sin(Mathf.PI * (nodPhase / NodDuration)) * NodDegrees;
            }
            return;
        }

        if (!EnableBackchannel || !UserIsSpeaking || Time.time < nextNodAllowedAt) return;

        // Nods cluster around prosodic boundaries rather than falling at random, so they
        // are triggered when the speaker's level dips - the audible equivalent of a comma.
        if (!InPause()) return;

        nextNodAllowedAt = Time.time + Random.Range(NodCooldownMin, NodCooldownMax);
        if (Random.value > NodChance) return;

        nodPhase = 0f;
        nodBrow = Random.value < NodBrowChance;
    }

    private float NodBrowValue()
    {
        if (nodPhase < 0f) return 0f;
        return Mathf.Sin(Mathf.PI * (nodPhase / NodDuration)) * 14f;
    }

    private void UpdateApproach()
    {
        if (LookTarget == null) return;

        var distance = Vector3.Distance(transform.position, LookTarget.position);
        if (previousDistance < 0f)
        {
            previousDistance = distance;
            return;
        }

        var closingSpeed = (previousDistance - distance) / Mathf.Max(Time.deltaTime, 1e-4f);
        previousDistance = distance;

        if (distance < ApproachNoticeDistance &&
            closingSpeed > ApproachSpeedThreshold &&
            Time.time - lastApproachAt > ApproachCooldown)
        {
            lastApproachAt = Time.time;
            // Scaled by proximity: someone arriving right in front warrants more than
            // someone crossing the far side of the room.
            approach = Mathf.Clamp01(1f - distance / ApproachNoticeDistance);
        }
    }
}

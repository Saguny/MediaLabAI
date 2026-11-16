using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using ChatdollKit.Extension.OpenAI;
using ChatdollKit.Model;
using UnityEngine;

// Everything the avatar does with voice, body and face, as one performer the director can
// cue: say this line in this voice, with this gesture and this expression.
//
// Body animation layers on the Animator (built by Tools > Hexe Role-Play > Build Animator):
//   Base         blend tree between the coach's upright stance and the witch's hunch,
//                driven by the float "Witch" - so the posture change is a smooth blend,
//                not a cut
//   RightHand /  one-shot gestures, masked to one or both arms, faded in and out by layer
//   BothHands    weight so they ride on whichever stance is active
// On top of that the existing procedural layers (breathing, head, gaze) now offset from the
// animated pose, speech drives the mouth through SAiD, and FaceExpressionLayer adds the
// emotional expression over both.
//
// Her body below the arms (feet, legs, hips, spine) is LayableBody's: planted feet, weight
// shifts and steps, and the postures she picks - in scripted lines ("posture", or a [p:Name]
// tag) or in the model's replies. Without Layable running it is the Animator's, as before.
//
// Speech goes through ChatdollKit's TTS loaders but is played here rather than through
// ModelController.Say, because the director needs the clips back (for the take recording)
// and needs a hard timeout: a failed download inside the loader can otherwise wait forever.
public class AvatarPerformer : MonoBehaviour
{
    public enum Voice { Coach, Witch }

    [Header("References")]
    public ModelController ModelController;
    public Animator Animator;
    public FaceExpressionLayer Face;
    public StageEffects Stage;
    public HexeStageUI UI;

    [Header("Voices (Kokoro voice names; the Chatterbox voices are cloned from them under the same names)")]
    public string CoachVoice = "af_bella";
    public float CoachSpeed = 1.0f;
    public string WitchVoice = "bf_isabella";
    public float WitchSpeed = 0.9f;
    [Tooltip("How far the witch sinks into her hunched stance: 0 stands upright like the coach, 1 is the full crooked pose. Her gestures play either way.")]
    [Range(0f, 1f)] public float WitchStance = 0f;
    [Tooltip("Reads the visitor's lines in the operator's test film, so it can be watched without anyone acting. Never used in a session.")]
    public string StandInVoice = "am_puck";
    public float StandInSpeed = 1.05f;

    // Chatterbox reads these per request (Kokoro ignores them). Exaggeration is how much
    // emotion goes into the voice (0.25 flat - 2 theatrical); a lower cfg_weight lets it
    // slow down and breathe, which strong emotion needs or it turns into a rush.
    [Header("Expressiveness (Chatterbox)")]
    [Range(0.25f, 2f)] public float CoachExaggeration = 0.6f;
    [Range(0.25f, 2f)] public float WitchExaggeration = 0.9f;
    [Tooltip("Added for lines tagged with a strong face, e.g. [f:WitchGreedy] or [f:Surprised].")]
    [Range(0f, 1f)] public float StrongFaceBoost = 0.25f;
    [Range(0f, 1f)] public float CoachCfgWeight = 0.45f;
    [Range(0f, 1f)] public float WitchCfgWeight = 0.3f;
    public string[] StrongFaces = { "WitchGreedy", "WitchSly", "WitchSuspicious", "Surprised", "BigSmile", "Concerned" };
    public float TtsTimeoutSec = 15f;
    [Tooltip("Pause after each spoken sentence.")]
    public float SentenceGap = 0.12f;

    [Header("Body")]
    public float StanceBlendSeconds = 0.9f;
    public float GestureFadeSeconds = 0.25f;
    public float TurnSeconds = 0.45f;

    public LayableBody Body { get; private set; }
    public string Posture => Body != null ? Body.GoalPosture.ToString() : "Stand";

    public bool IsWitch { get; private set; }
    public bool IsSpeaking { get; private set; }
    public bool IsVoicePlaying => voiceSource != null && voiceSource.isPlaying;
    // Raised as each clip starts playing, so the take recording knows when it was heard.
    public event Action<AudioClip> ClipStarted;

    private OpenAITTSLoader coachLoader, witchLoader, standInLoader;
    private AudioSource voiceSource;
    private float witchBlend;

    private static readonly Dictionary<string, string> GestureLayer = new Dictionary<string, string>
    {
        { "Wave", "RightHand" }, { "PointForward", "RightHand" }, { "PointSelf", "RightHand" }, { "PresentSide", "RightHand" },
        { "HandToEar", "RightHand" }, { "FingerWag", "RightHand" }, { "ThinkChin", "RightHand" }, { "Beckon", "RightHand" },
        { "OpenArms", "BothHands" }, { "Clap", "BothHands" }, { "Shrug", "BothHands" }, { "RubHands", "BothHands" },
    };

    private class LayerState
    {
        public int Index = -1;
        public float Weight;
        public float Target;
        public float ReleaseAt;
    }

    private readonly Dictionary<string, LayerState> layers = new Dictionary<string, LayerState>();
    private readonly Dictionary<string, float> clipLengths = new Dictionary<string, float>();

    public class Utterance
    {
        public string Text;
        public string Gesture;
        public string Face;
        public string Posture;
    }

    private void Start()
    {
        voiceSource = ModelController != null ? ModelController.AudioSource : null;
        CreateLoaders();

        if (Animator != null)
        {
            Body = Animator.GetComponent<LayableBody>();
            if (Body == null) Body = Animator.gameObject.AddComponent<LayableBody>();
            Animator.applyRootMotion = false;
            foreach (var name in new[] { "RightHand", "LeftHand", "BothHands" })
            {
                var idx = Animator.GetLayerIndex(name);
                if (idx >= 0) layers[name] = new LayerState { Index = idx };
            }
            if (Animator.runtimeAnimatorController != null)
            {
                foreach (var clip in Animator.runtimeAnimatorController.animationClips) clipLengths[clip.name] = clip.length;
            }
            else
            {
                Debug.LogWarning("AvatarPerformer: the avatar has no Animator Controller - run Tools > Hexe Role-Play > Build Animator. Gestures and stance changes will be skipped.");
            }
        }
    }

    // Two extra TTS loaders beside the scene's own, one per character, cloned from its
    // endpoint settings so the voices can differ without touching the ChatdollKit setup.
    private void CreateLoaders()
    {
        var template = ModelController != null ? ModelController.GetComponent<OpenAITTSLoader>() : null;
        if (template == null)
        {
            Debug.LogWarning("AvatarPerformer: no OpenAITTSLoader found - the avatar will be silent (subtitles only).");
            return;
        }
        coachLoader = CloneLoader(template, "HexeCoach", CoachVoice, CoachSpeed);
        witchLoader = CloneLoader(template, "HexeWitch", WitchVoice, WitchSpeed);
        standInLoader = CloneLoader(template, "HexeStandIn", StandInVoice, StandInSpeed);
    }

    // A line in the stand-in voice, synthesized but not played.
    public UniTask<AudioClip> SynthesizeStandInAsync(string text, CancellationToken token) =>
        standInLoader != null && !string.IsNullOrEmpty(text)
            ? FetchAsync(standInLoader, text, Voice.Coach, null, true, token)
            : UniTask.FromResult<AudioClip>(null);

    private static OpenAITTSLoader CloneLoader(OpenAITTSLoader template, string name, string voice, float speed)
    {
        var loader = template.gameObject.AddComponent<OpenAITTSLoader>();
        loader._Name = name;
        loader._IsDefault = false;
        loader.ApiKey = template.ApiKey;
        loader.SpeechUrl = template.SpeechUrl;
        loader.Voice = string.IsNullOrEmpty(voice) ? template.Voice : voice;
        loader.Speed = speed;
        loader.Timeout = template.Timeout;
        return loader;
    }

    private void Update()
    {
        if (Animator == null || Animator.runtimeAnimatorController == null) return;

        var step = Time.deltaTime / Mathf.Max(0.05f, StanceBlendSeconds);
        witchBlend = Mathf.MoveTowards(witchBlend, IsWitch ? 1f : 0f, step);
        Animator.SetFloat("Witch", witchBlend * witchBlend * (3f - 2f * witchBlend) * WitchStance);

        foreach (var layer in layers.Values)
        {
            if (layer.Target > 0f && Time.time >= layer.ReleaseAt) layer.Target = 0f;
            layer.Weight = Mathf.MoveTowards(layer.Weight, layer.Target, Time.deltaTime / Mathf.Max(0.05f, GestureFadeSeconds));
            Animator.SetLayerWeight(layer.Index, layer.Weight);
        }
    }

    // ------------------------------------------------------------------ body and face

    public void SetWitch(bool witch) => IsWitch = witch;

    public void SetFace(string preset)
    {
        if (Face != null && !string.IsNullOrEmpty(preset)) Face.Set(preset);
    }

    public void PlayGesture(string gesture)
    {
        if (string.IsNullOrEmpty(gesture) || Animator == null || Animator.runtimeAnimatorController == null) return;
        if (!GestureLayer.TryGetValue(gesture, out var layerName) || !layers.TryGetValue(layerName, out var layer))
        {
            Debug.LogWarning($"AvatarPerformer: unknown gesture '{gesture}'.");
            return;
        }

        var length = clipLengths.TryGetValue(gesture, out var l) ? l : 2f;
        Animator.CrossFadeInFixedTime(gesture, 0.1f, layer.Index, 0f);
        layer.Target = 1f;
        layer.ReleaseAt = Time.time + Mathf.Max(0.3f, length - GestureFadeSeconds);

        // A two-handed gesture takes both arms; drop a one-handed one still fading out.
        if (layerName == "BothHands" && layers.TryGetValue("RightHand", out var right)) right.Target = 0f;
    }

    // How she holds her body: Stand, Lean, Crouch, SitGround, LieBack or LieSide. She moves
    // there the way a person would (crouching, then sitting, then lying back).
    public void SetPosture(string posture)
    {
        if (string.IsNullOrEmpty(posture) || Body == null) return;
        if (!Body.TrySetPosture(posture)) Debug.LogWarning($"AvatarPerformer: unknown posture '{posture}'.");
    }

    // A new session: standing, feet planted where she is.
    public void ResetBody() => Body?.ResetBody();

    // Back on her feet (if she wasn't), waiting until she is - at most a few seconds.
    public async UniTask StandUpAsync(CancellationToken token)
    {
        if (Body == null) return;
        Body.SetPosture(LayableBody.Posture.Stand);
        for (var t = 0f; Body.IsBusy && t < 8f; t += Time.deltaTime) await UniTask.Yield(token);
    }

    // The costume change between coach and witch: turn away, change while the back is
    // turned (hat, posture, light), turn back as the other character.
    public async UniTask TransformAsync(bool toWitch, bool playCue, CancellationToken token)
    {
        var root = Animator != null ? Animator.transform : null;
        if (root == null)
        {
            IsWitch = toWitch;
            Stage?.SetSceneMode(toWitch);
            return;
        }
        await StandUpAsync(token);   // turning round is done on her feet

        var start = root.localRotation;
        await Turn(root, start, 180f, token);

        IsWitch = toWitch;
        Stage?.SetSceneMode(toWitch);
        if (Stage != null)
        {
            if (playCue) Stage.PlayCue(toWitch ? StageEffects.Cue.SceneStart : StageEffects.Cue.Cut);
            await Stage.SetHatAsync(toWitch, token);
        }
        SetFace(toWitch ? "WitchSly" : "BigSmile");
        await UniTask.Delay(TimeSpan.FromSeconds(0.25f), cancellationToken: token);

        await Turn(root, start * Quaternion.Euler(0f, 180f, 0f), 180f, token);
        root.localRotation = start;
    }

    private async UniTask Turn(Transform root, Quaternion from, float degrees, CancellationToken token)
    {
        for (var t = 0f; t < TurnSeconds; t += Time.deltaTime)
        {
            var k = t / TurnSeconds;
            k = k * k * (3f - 2f * k);
            root.localRotation = from * Quaternion.Euler(0f, degrees * k, 0f);
            await UniTask.Yield(token);
        }
        root.localRotation = from * Quaternion.Euler(0f, degrees, 0f);
    }

    // ------------------------------------------------------------------ speech

    public static List<Utterance> Parse(string text, bool splitSentences)
    {
        var result = new List<Utterance>();
        string gesture = null, face = null, posture = null;
        foreach (var part in Regex.Split(text ?? string.Empty, @"(\[[gfp]:[A-Za-z]+\])"))
        {
            var tag = Regex.Match(part, @"^\[([gfp]):([A-Za-z]+)\]$");
            if (tag.Success)
            {
                if (tag.Groups[1].Value == "g") gesture = tag.Groups[2].Value;
                else if (tag.Groups[1].Value == "p") posture = tag.Groups[2].Value;
                else face = tag.Groups[2].Value;
                continue;
            }
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var sentences = splitSentences
                ? Regex.Split(trimmed, "(?<=[.!?…])\\s+(?=[A-Z\"'“(])")
                : new[] { trimmed };
            foreach (var s in sentences)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                result.Add(new Utterance { Text = s.Trim(), Gesture = gesture, Face = face, Posture = posture });
                gesture = null;
                face = null;
                posture = null;
            }
        }
        if (gesture != null || face != null || posture != null)
            result.Add(new Utterance { Text = string.Empty, Gesture = gesture, Face = face, Posture = posture });
        return result;
    }

    // Synthesizes lines one sentence at a time, each with its lip-sync curves, so that by the
    // time a visitor arrives every scripted line plays the instant it is due. One at a time
    // because Kokoro and SAiD serve requests in order: a flood of warm-up work would queue in
    // front of the line actually being spoken. For the same reason it waits while hold says so:
    // a session's lines must not queue behind sentences nobody is waiting for.
    public async UniTask WarmUpAsync(IEnumerable<(string Text, Voice Voice)> lines, Func<bool> hold, CancellationToken token)
    {
        var count = 0;
        var started = Time.realtimeSinceStartup;
        foreach (var (text, voice) in lines)
        {
            var loader = LoaderFor(voice);
            if (loader == null) return;
            foreach (var u in Parse(text, voice == Voice.Coach))
            {
                if (u.Text.Length == 0) continue;
                await UniTask.WaitWhile(hold, cancellationToken: token);
                var clip = await FetchAsync(loader, u.Text, voice, u.Face, true, token);
                var said = SaidBlendshapePlayer.Instance;
                for (var t = 0f; clip != null && said != null && said.IsAvailable && !said.HasCurves(clip) && t < 10f; t += Time.unscaledDeltaTime)
                {
                    await UniTask.Yield(token);
                }
                count++;
            }
        }
        Debug.Log($"AvatarPerformer: {count} scripted sentences ready in {Time.realtimeSinceStartup - started:F0}s.");
    }

    // Speaks a line: sentence by sentence for the coach (so gestures land on the sentence
    // they belong to, and the first words start sooner), as one clip for scripted witch lines
    // (so each is one segment in the take recording). Returns the clips that were played.
    // Scripted lines are cached (they recur and were warmed up); improvised ones are not.
    public async UniTask<List<AudioClip>> SayAsync(string text, Voice voice, CancellationToken token, Action<string> onSentence = null, bool cache = true)
    {
        var played = new List<AudioClip>();
        var loader = LoaderFor(voice);
        var utterances = Parse(text, voice == Voice.Coach);

        // Synthesize ahead of the playback, one sentence after the other. Sent all at once they
        // come back in any order, and the first sentence, the one being waited for, can land
        // behind the others in SAiD's queue: seconds of dead air before she starts.
        var synthesized = 0;
        async UniTask<AudioClip> InTurnAsync(int turn, string line, string face)
        {
            await UniTask.WaitUntil(() => synthesized >= turn, cancellationToken: token);
            try { return await FetchAsync(loader, line, voice, face, cache, token); }
            finally { synthesized++; }
        }

        var fetches = new List<UniTask<AudioClip>>();
        var turns = 0;
        foreach (var u in utterances)
        {
            fetches.Add(loader != null && u.Text.Length > 0 ? InTurnAsync(turns++, u.Text, u.Face).Preserve() : UniTask.FromResult<AudioClip>(null));
        }

        IsSpeaking = true;
        try
        {
            for (var i = 0; i < utterances.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var u = utterances[i];
                var waitFrom = Time.realtimeSinceStartup;
                AudioClip clip = null;
                try { clip = await fetches[i]; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }

                if (!string.IsNullOrEmpty(u.Face)) SetFace(u.Face);
                if (!string.IsNullOrEmpty(u.Gesture)) PlayGesture(u.Gesture);
                if (!string.IsNullOrEmpty(u.Posture)) SetPosture(u.Posture);
                if (u.Text.Length == 0) continue;
                RecentlySaid = previousSentence + " " + u.Text;
                previousSentence = u.Text;
                onSentence?.Invoke(u.Text);

                if (clip != null && voiceSource != null)
                {
                    if (ModelController.PrepareVoicePlaybackAsync != null) await ModelController.PrepareVoicePlaybackAsync(clip);
                    // Dead air before this sentence, voice and lip-sync together.
                    Debug.Log($"AvatarPerformer: waited {Time.realtimeSinceStartup - waitFrom:F2}s for \"{u.Text}\".");
                    ModelController.OnVoicePlaybackStarted?.Invoke(clip);
                    voiceSource.PlayOneShot(clip);
                    ClipStarted?.Invoke(clip);
                    played.Add(clip);
                    await UniTask.Yield(token);
                    while (voiceSource.isPlaying) await UniTask.Yield(token);
                }
                else
                {
                    // No audio (TTS down or silent mode): hold the subtitle for a reading-
                    // speed duration so the flow and timing still work.
                    await UniTask.Delay(TimeSpan.FromSeconds(0.6f + u.Text.Length * 0.055f), cancellationToken: token);
                }
                await UniTask.Delay(TimeSpan.FromSeconds(SentenceGap), cancellationToken: token);
            }
        }
        catch (OperationCanceledException)
        {
            voiceSource?.Stop();
            throw;
        }
        finally
        {
            IsSpeaking = false;
        }
        return played;
    }

    // The sentences just spoken, to tell her own voice coming back through the microphone from the visitor's.
    public string RecentlySaid { get; private set; } = "";
    private string previousSentence = "";

    public void StopSpeaking()
    {
        voiceSource?.Stop();
    }

    private OpenAITTSLoader LoaderFor(Voice voice) => voice == Voice.Witch ? witchLoader : coachLoader;

    // The same line always gets the same settings, so a warmed-up clip is what plays.
    private TTSConfiguration Expressiveness(OpenAITTSLoader loader, Voice voice, string face)
    {
        var config = new TTSConfiguration(loader.Name);
        var exaggeration = voice == Voice.Witch ? WitchExaggeration : CoachExaggeration;
        if (!string.IsNullOrEmpty(face) && Array.IndexOf(StrongFaces, face) >= 0) exaggeration += StrongFaceBoost;
        config.Params["exaggeration"] = Mathf.Clamp(exaggeration, 0.25f, 2f);
        config.Params["cfg_weight"] = voice == Voice.Witch ? WitchCfgWeight : CoachCfgWeight;
        return config;
    }

    private async UniTask<AudioClip> FetchAsync(OpenAITTSLoader loader, string text, Voice voice, string face, bool useCache, CancellationToken token)
    {
        var v = new ChatdollKit.Model.Voice(string.Empty, 0f, 0f, text, string.Empty, Expressiveness(loader, voice, face), VoiceSource.TTS, useCache, string.Empty);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(TtsTimeoutSec));
            try
            {
                return await loader.GetAudioClipAsync(v, timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Debug.LogWarning($"AvatarPerformer: TTS timed out for \"{text}\".");
                return null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Debug.LogWarning($"AvatarPerformer: TTS failed for \"{text}\": {ex.Message}");
                return null;
            }
        }
    }
}

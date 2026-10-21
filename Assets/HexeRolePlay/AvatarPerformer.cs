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

    [Header("Voices (Kokoro voice names)")]
    public string CoachVoice = "af_bella";
    public float CoachSpeed = 1.0f;
    public string WitchVoice = "bf_isabella";
    public float WitchSpeed = 0.9f;
    public float TtsTimeoutSec = 15f;
    [Tooltip("Pause after each spoken sentence.")]
    public float SentenceGap = 0.12f;

    [Header("Body")]
    public float StanceBlendSeconds = 0.9f;
    public float GestureFadeSeconds = 0.25f;
    public float TurnSeconds = 0.45f;

    public bool IsWitch { get; private set; }
    public bool IsSpeaking { get; private set; }
    public bool IsVoicePlaying => voiceSource != null && voiceSource.isPlaying;

    private OpenAITTSLoader coachLoader, witchLoader;
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
    }

    private void Start()
    {
        voiceSource = ModelController != null ? ModelController.AudioSource : null;
        CreateLoaders();

        if (Animator != null)
        {
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
    }

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
        Animator.SetFloat("Witch", witchBlend * witchBlend * (3f - 2f * witchBlend));

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
        string gesture = null, face = null;
        foreach (var part in Regex.Split(text ?? string.Empty, @"(\[[gf]:[A-Za-z]+\])"))
        {
            var tag = Regex.Match(part, @"^\[([gf]):([A-Za-z]+)\]$");
            if (tag.Success)
            {
                if (tag.Groups[1].Value == "g") gesture = tag.Groups[2].Value;
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
                result.Add(new Utterance { Text = s.Trim(), Gesture = gesture, Face = face });
                gesture = null;
                face = null;
            }
        }
        if (gesture != null || face != null) result.Add(new Utterance { Text = string.Empty, Gesture = gesture, Face = face });
        return result;
    }

    // Synthesizes lines one sentence at a time, each with its lip-sync curves, so that by the
    // time a visitor arrives every scripted line plays the instant it is due. One at a time
    // because Kokoro and SAiD serve requests in order: a flood of warm-up work would queue in
    // front of the line actually being spoken.
    public async UniTask WarmUpAsync(IEnumerable<(string Text, Voice Voice)> lines, CancellationToken token)
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
                var clip = await FetchAsync(loader, u.Text, true, token);
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

        // Kick off every sentence's synthesis at once; await them in order.
        var fetches = new List<UniTask<AudioClip>>();
        foreach (var u in utterances)
        {
            fetches.Add(loader != null && u.Text.Length > 0 ? FetchAsync(loader, u.Text, cache, token).Preserve() : UniTask.FromResult<AudioClip>(null));
        }

        IsSpeaking = true;
        try
        {
            for (var i = 0; i < utterances.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var u = utterances[i];
                AudioClip clip = null;
                try { clip = await fetches[i]; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }

                if (!string.IsNullOrEmpty(u.Face)) SetFace(u.Face);
                if (!string.IsNullOrEmpty(u.Gesture)) PlayGesture(u.Gesture);
                if (u.Text.Length == 0) continue;
                onSentence?.Invoke(u.Text);

                if (clip != null && voiceSource != null)
                {
                    if (ModelController.PrepareVoicePlaybackAsync != null) await ModelController.PrepareVoicePlaybackAsync(clip);
                    ModelController.OnVoicePlaybackStarted?.Invoke(clip);
                    voiceSource.PlayOneShot(clip);
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

    public void StopSpeaking()
    {
        voiceSource?.Stop();
    }

    private OpenAITTSLoader LoaderFor(Voice voice) => voice == Voice.Witch ? witchLoader : coachLoader;

    private async UniTask<AudioClip> FetchAsync(OpenAITTSLoader loader, string text, bool useCache, CancellationToken token)
    {
        var v = new ChatdollKit.Model.Voice(string.Empty, 0f, 0f, text, string.Empty, new TTSConfiguration(loader.Name), VoiceSource.TTS, useCache, string.Empty);
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

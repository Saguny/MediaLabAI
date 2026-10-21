using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;

// Drives the lower face from SAiD's speech-derived ARKit blendshape curves instead of
// from named preset expressions. Given a WAV, the SAiD service returns 32 coefficients
// per frame at 60fps; we remap those onto this avatar's shape names via a JSON table in
// StreamingAssets and play them back against the AudioSource's own playback clock, so
// the mouth stays in sync even if the audio starts late or the framerate dips.
//
// Only speech articulation comes from here - SAiD emits no eye or brow shapes at all.
// Those stay owned by the procedural gaze/head/expression layer.
public class SaidBlendshapePlayer : MonoBehaviour
{
    [Header("Service")]
    public string ServiceUrl = "http://localhost:8900/generate";
    [Tooltip("Diffusion steps. 20 tracks the 50-step output at r=0.97 for ~2.5x the speed; raise only if the mouth reads as mushy.")]
    public int NumSteps = 20;
    public float GuidanceScale = 2.0f;
    public int RequestTimeoutSec = 60;

    [Header("Targets")]
    public SkinnedMeshRenderer FaceRenderer;
    public AudioSource VoiceAudioSource;

    [Header("Mapping")]
    [Tooltip("File in StreamingAssets mapping ARKit blendshape names onto this avatar's shape names.")]
    public string MappingFileName = "arkit_to_milltina.json";

    [Header("Playback")]
    [Tooltip("Scales every generated coefficient. Lower it if the mouth reads as over-articulated.")]
    [Range(0f, 2f)] public float Intensity = 1.0f;
    [Tooltip("Seconds of smoothing per shape, damping single-frame jitter from the diffusion output.")]
    public float SmoothTime = 0.03f;

    [Tooltip("How long audio playback may be held back waiting for this utterance's curves. Generation runs ~1.2s per 5s of speech and only the first sentence usually has to wait; later ones are generated during the previous sentence.")]
    public float MaxCurveWaitSec = 2.5f;

    [Tooltip("Logs clip ids as curves are cached and picked up, to diagnose curves never reaching playback.")]
    public bool VerboseLogging = true;

    [Tooltip("Utterances whose curves are kept. Scripted lines are prepared once and replayed on later takes, so this has to outlast a whole scene plus the talk around it, not just the prefetch depth.")]
    public int MaxCachedUtterances = 512;

    public static SaidBlendshapePlayer Instance { get; private set; }

    // Current speech intensity, 0-1, read off the generated mouth curves rather than the
    // audio signal. The upper face uses this to move *with* the speech - brows lifting on
    // emphasis and so on - which is what makes eyes read as connected to the voice instead
    // of drifting on their own schedule. Taken from the curves so it is frame-accurate
    // against the same timeline the mouth is using.
    public float SpeechEnergy { get; private set; }

    // Normalized pitch (0-1 over the utterance), sampled slightly AHEAD of the audio.
    // Eyebrow movement precedes the corresponding F0 rise by roughly 60ms in real
    // speakers, and because the whole curve is generated before playback we can actually
    // look ahead rather than approximate the lead with a filter.
    public float Pitch { get; private set; }
    public float Loudness { get; private set; }
    public bool IsVoiced { get; private set; }

    // True while an utterance is actually playing, used by the behaviour layer to tell
    // speaking from listening without inspecting the AudioSource.
    public bool IsSpeaking { get; private set; }

    // Rises at the end of a phrase - the falling edge of voiced speech. Blinks and gaze
    // returns cluster here in real conversation.
    public bool AtPhraseBoundary { get; private set; }

    // 0-1 while a laugh is being vocalised. Driven by the utterance's own loudness contour,
    // so the eye and head components land on the individual "ha" calls rather than running
    // on an independent timer that would drift against the audio.
    public float LaughterIntensity { get; private set; }

    [Header("Laughter")]
    [Tooltip("How sharply the loudness contour is shaped into per-call peaks. Higher isolates the calls more crisply.")]
    public float LaughterCallSharpness = 2.2f;
    [Tooltip("Seconds for the laugh envelope to rise and fall around the vocalised part.")]
    public float LaughterBlendTime = 0.18f;

    [Header("Prosody")]
    [Tooltip("Seconds to look ahead when sampling pitch for the upper face. Real eyebrow raises lead the pitch rise they accompany by about this much.")]
    public float BrowLeadSeconds = 0.06f;

    // Indices into the ARKit channel list for the shapes that best track vocal effort.
    private int chJawOpen = -1, chFunnel = -1, chPucker = -1, chStretchL = -1;

    // One ARKit channel can drive several of the avatar's shapes at different strengths.
    private struct ShapeTarget
    {
        public int Index;
        public float Weight;
    }

    // Curves for one utterance. ChatdollKit prefetches several TTS clips ahead of
    // playback, so curves are generated at download time and parked here keyed by the
    // clip they belong to, then picked up when that clip actually starts.
    private class CurveSet
    {
        public float[,] Frames;   // [frame, arkitChannel]
        public int FrameCount;
        public float Fps;
        public ShapeTarget[][] Targets;

        // Prosody on the same frame grid. Pitch drives the upper face; the blendshape
        // curves only encode mouth shape, which is a loudness proxy, not a pitch one.
        public float[] F0;
        public float[] Loudness;
        public float[] Voiced;

        public bool HasLaughter;
        public float LaughterStartFraction;
    }

    private Dictionary<string, ShapeTarget[]> rawMap;

    // Keyed by instance id rather than by AudioClip. ChatdollKit destroys clips once it is
    // done with them, and a destroyed UnityEngine.Object throws on member access even
    // though it compares equal to null - holding the references here meant the cache could
    // outlive its own keys.
    private readonly Dictionary<int, CurveSet> cache = new Dictionary<int, CurveSet>();
    private readonly List<int> cacheOrder = new List<int>();
    private CurveSet active;

    // ChatdollKit plays through AudioSource.PlayOneShot, which leaves AudioSource.clip null
    // and AudioSource.time meaningless. So playback position is tracked from the moment
    // ModelController tells us a clip started, rather than read off the AudioSource.
    private int pendingClipId;
    private int consecutiveFailures;
    private float playbackStartedAt = -1f;
    private float playbackLength;

    // Baseline captured before playback so releasing a shape returns it to the avatar's
    // sculpted default rather than to zero - same reason VRCFaceExpressionProxy does it.
    private float[] baseline;
    private float[] current;
    private float[] velocity;
    private float[] accum;
    private int[] touched;     // only the indices the mapping actually drives

    private void Awake()
    {
        Instance = this;
        LoadMapping();
    }

    private void OnEnable()
    {
        ChatdollKit.Model.ModelController.OnVoicePlaybackStarted += OnClipStarted;
        ChatdollKit.Model.ModelController.PrepareVoicePlaybackAsync = WaitForCurvesAsync;
    }

    private void OnDisable()
    {
        ChatdollKit.Model.ModelController.OnVoicePlaybackStarted -= OnClipStarted;
        if (ChatdollKit.Model.ModelController.PrepareVoicePlaybackAsync == (System.Func<AudioClip, UniTask>)WaitForCurvesAsync)
        {
            ChatdollKit.Model.ModelController.PrepareVoicePlaybackAsync = null;
        }
    }

    // Hold the audio back until this utterance's curves exist, so the mouth starts on the
    // first word rather than joining a second in. Bounded: if SAiD is slow or down the
    // speech still plays, just without generated mouth animation.
    private async UniTask WaitForCurvesAsync(AudioClip clip)
    {
        if (clip == null) return;

        var id = clip.GetInstanceID();
        if (cache.ContainsKey(id)) return;
        // Nothing will arrive, so don't hold the audio back: either there is no mapping, or
        // the service has failed repeatedly and waiting would only add dead air per sentence.
        if (rawMap == null || consecutiveFailures >= 2) return;

        var deadline = Time.realtimeSinceStartup + MaxCurveWaitSec;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (cache.ContainsKey(id)) return;
            await UniTask.Delay(16);
        }

        if (VerboseLogging)
        {
            Debug.LogWarning($"[SAiD] curves not ready within {MaxCurveWaitSec:F1}s for clip id={id} - playing without mouth animation.");
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // Whether generation can be expected at all, and whether this clip's curves are ready.
    // Lets a warm-up loop pace itself to the service instead of flooding its queue.
    public bool IsAvailable => rawMap != null && consecutiveFailures < 2;
    public bool HasCurves(AudioClip clip) => clip != null && cache.ContainsKey(clip.GetInstanceID());

    private void LoadMapping()
    {
        var path = Path.Combine(Application.streamingAssetsPath, MappingFileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"SaidBlendshapePlayer: mapping file not found at {path}");
            return;
        }

        if (FaceRenderer == null || FaceRenderer.sharedMesh == null)
        {
            Debug.LogError("SaidBlendshapePlayer: FaceRenderer is not assigned.");
            return;
        }

        var mesh = FaceRenderer.sharedMesh;
        var nameToIndex = new Dictionary<string, int>();
        for (var i = 0; i < mesh.blendShapeCount; i++)
        {
            nameToIndex[mesh.GetBlendShapeName(i)] = i;
        }

        var map = (JObject)JObject.Parse(File.ReadAllText(path))["map"];
        rawMap = new Dictionary<string, ShapeTarget[]>();
        var touchedSet = new HashSet<int>();
        var missing = new List<string>();

        foreach (var entry in map)
        {
            // Tolerate non-array values so documentation keys can live alongside the
            // mappings without breaking the load.
            if (!(entry.Value is JArray arr)) continue;

            var targets = new List<ShapeTarget>();
            foreach (var t in arr)
            {
                var shapeName = (string)t["shape"];
                if (!nameToIndex.TryGetValue(shapeName, out var idx))
                {
                    missing.Add(shapeName);
                    continue;
                }
                targets.Add(new ShapeTarget { Index = idx, Weight = (float)t["weight"] });
                touchedSet.Add(idx);
            }
            rawMap[entry.Key] = targets.ToArray();
        }

        if (missing.Count > 0)
        {
            Debug.LogWarning($"SaidBlendshapePlayer: {missing.Count} mapped shape(s) not on mesh: {string.Join(", ", missing)}");
        }

        touched = new int[touchedSet.Count];
        touchedSet.CopyTo(touched);

        baseline = new float[mesh.blendShapeCount];
        current = new float[mesh.blendShapeCount];
        velocity = new float[mesh.blendShapeCount];
        accum = new float[mesh.blendShapeCount];
        for (var i = 0; i < mesh.blendShapeCount; i++)
        {
            baseline[i] = FaceRenderer.GetBlendShapeWeight(i);
            current[i] = baseline[i];
        }

        Debug.Log($"SaidBlendshapePlayer: mapping loaded, driving {touched.Length} blend shapes.");
    }

    // Called by ModelController the instant it hands a clip to PlayOneShot. Curves may not
    // have finished generating yet, so record the clip and start the clock now; LateUpdate
    // picks the curves up whenever they land.
    public void OnClipStarted(AudioClip clip)
    {
        if (clip == null) return;

        pendingClipId = clip.GetInstanceID();
        playbackStartedAt = Time.time;
        playbackLength = clip.length;
        active = null;

        if (VerboseLogging)
        {
            Debug.Log($"[SAiD] clip started id={pendingClipId} len={playbackLength:F2}s - curves ready: {cache.ContainsKey(pendingClipId)}");
        }
    }

    // Re-reads the avatar's current weights as the rest pose. Call after anything else
    // has re-sculpted the face, otherwise playback returns to a stale baseline.
    public void RecaptureBaseline()
    {
        if (FaceRenderer == null || baseline == null) return;
        for (var i = 0; i < baseline.Length; i++)
        {
            baseline[i] = FaceRenderer.GetBlendShapeWeight(i);
        }
    }

    // Called by the TTS loader as soon as an utterance's audio is downloaded, which is
    // typically well before it plays. Generation costs ~1.4s per 5s of speech, so doing
    // it here rather than at playback time keeps the mouth from starting late.
    public async UniTask PrepareForClipAsync(AudioClip clip, byte[] wavBytes, string spokenText = null)
    {
        if (clip == null || wavBytes == null || wavBytes.Length == 0) return;
        if (rawMap == null)
        {
            Debug.LogWarning("SaidBlendshapePlayer: mapping not loaded, skipping generation.");
            return;
        }

        var curves = await RequestCurvesAsync(wavBytes);
        if (curves == null)
        {
            consecutiveFailures++;
            return;
        }
        consecutiveFailures = 0;

        if (!string.IsNullOrEmpty(spokenText) && LaughterDetector.Contains(spokenText))
        {
            curves.HasLaughter = true;
            curves.LaughterStartFraction = LaughterDetector.FirstOccurrenceFraction(spokenText);
        }

        var id = clip.GetInstanceID();
        if (!cache.ContainsKey(id)) cacheOrder.Add(id);
        cache[id] = curves;
        PruneCache();

        if (VerboseLogging)
        {
            Debug.Log($"[SAiD] cached {curves.FrameCount} frames for clip id={id} (cache size {cache.Count})");
        }
    }

    // Keep the cache bounded so a long conversation doesn't retain every utterance's
    // curves. Evicting oldest-first keeps whatever is playing or queued.
    private void PruneCache()
    {
        var maxEntries = Mathf.Max(8, MaxCachedUtterances);
        while (cacheOrder.Count > maxEntries)
        {
            var oldest = cacheOrder[0];
            cacheOrder.RemoveAt(0);
            if (oldest != pendingClipId) cache.Remove(oldest);
        }
    }

    private async UniTask<CurveSet> RequestCurvesAsync(byte[] wavBytes)
    {
        var form = new WWWForm();
        form.AddBinaryData("audio", wavBytes, "speech.wav", "audio/wav");
        form.AddField("num_steps", NumSteps);
        form.AddField("guidance_scale", GuidanceScale.ToString("F2"));

        using (var req = UnityWebRequest.Post(ServiceUrl, form))
        {
            req.timeout = RequestTimeoutSec;
            try
            {
                await req.SendWebRequest().ToUniTask();
            }
            catch (Exception ex)
            {
                Debug.LogError($"SaidBlendshapePlayer: request failed: {ex.Message}");
                return null;
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"SaidBlendshapePlayer: service error: {req.error}");
                return null;
            }

            try
            {
                return ParseResponse(req.downloadHandler.text);
            }
            catch (Exception ex)
            {
                Debug.LogError($"SaidBlendshapePlayer: could not parse response: {ex.Message}");
                return null;
            }
        }
    }

    private CurveSet ParseResponse(string body)
    {
        var root = JObject.Parse(body);
        var names = root["names"].ToObject<string[]>();

        chJawOpen = Array.IndexOf(names, "jawOpen");
        chFunnel = Array.IndexOf(names, "mouthFunnel");
        chPucker = Array.IndexOf(names, "mouthPucker");
        chStretchL = Array.IndexOf(names, "mouthStretchLeft");

        // Resolved once per response so playback is an array walk rather than a
        // dictionary lookup per shape per frame.
        var targets = new ShapeTarget[names.Length][];
        for (var i = 0; i < names.Length; i++)
        {
            targets[i] = rawMap.TryGetValue(names[i], out var t) ? t : Array.Empty<ShapeTarget>();
        }

        var frameArray = (JArray)root["frames"];
        var count = frameArray.Count;
        var frames = new float[count, names.Length];
        for (var f = 0; f < count; f++)
        {
            var row = (JArray)frameArray[f];
            var n = Math.Min(names.Length, row.Count);
            for (var c = 0; c < n; c++)
            {
                frames[f, c] = (float)row[c];
            }
        }

        var set = new CurveSet
        {
            Frames = frames,
            FrameCount = count,
            Fps = (float)root["fps"],
            Targets = targets,
        };

        var prosody = root["prosody"];
        if (prosody != null)
        {
            set.F0 = prosody["f0"]?.ToObject<float[]>();
            set.Loudness = prosody["loudness"]?.ToObject<float[]>();
            set.Voiced = prosody["voiced"]?.ToObject<float[]>();
        }

        return set;
    }

    // Vocal effort for the current frame: how open/active the mouth is, which tracks
    // stressed syllables closely enough to drive upper-face emphasis.
    private void UpdateSpeechEnergy(int i0, int i1, float frac)
    {
        float Sample(int ch)
        {
            if (ch < 0) return 0f;
            return Mathf.Lerp(active.Frames[i0, ch], active.Frames[i1, ch], frac);
        }

        var raw = Sample(chJawOpen) * 1.6f
                + Sample(chFunnel) * 0.5f
                + Sample(chPucker) * 0.4f
                + Sample(chStretchL) * 0.4f;

        // Attack fast so emphasis lands on the syllable, release slower so the upper face
        // settles rather than flickering between every phoneme.
        var target = Mathf.Clamp01(raw);
        var rate = target > SpeechEnergy ? 18f : 6f;
        SpeechEnergy = Mathf.Lerp(SpeechEnergy, target, Time.deltaTime * rate);
    }

    // Samples the pitch/loudness contours, reading pitch slightly ahead of the audio so
    // the brows lead it the way a real speaker's do.
    private void UpdateProsody(float elapsed)
    {
        if (active == null) return;

        float SampleAt(float[] arr, float seconds)
        {
            if (arr == null || arr.Length == 0) return 0f;
            var f = Mathf.Clamp(seconds * active.Fps, 0f, arr.Length - 1);
            var a = Mathf.FloorToInt(f);
            var b = Mathf.Min(a + 1, arr.Length - 1);
            return Mathf.Lerp(arr[a], arr[b], f - a);
        }

        Pitch = SampleAt(active.F0, elapsed + BrowLeadSeconds);
        Loudness = SampleAt(active.Loudness, elapsed);
        UpdateLaughter(elapsed);

        var voiced = SampleAt(active.Voiced, elapsed) > 0.5f;

        // A voiced->unvoiced transition with the voice trailing off is a phrase boundary;
        // a brief unvoiced consonant mid-word is not. Requiring low loudness separates them.
        AtPhraseBoundary = IsVoiced && !voiced && Loudness < 0.12f;
        IsVoiced = voiced;
    }

    // Shapes the laugh envelope from the audio's own loudness contour.
    //
    // Using the contour rather than a synthesised rhythm means the calls land exactly on
    // what is heard, and the natural decrescendo across a bout comes along for free instead
    // of having to be modelled - the TTS already produces it.
    private void UpdateLaughter(float elapsed)
    {
        if (active == null || !active.HasLaughter)
        {
            LaughterIntensity = Mathf.Lerp(LaughterIntensity, 0f, Time.deltaTime / Mathf.Max(0.01f, LaughterBlendTime));
            return;
        }

        var clipLength = active.FrameCount / Mathf.Max(1f, active.Fps);
        var startAt = active.LaughterStartFraction * clipLength;

        // Hold off until the audio actually reaches the laugh, so a sentence that merely
        // ends in a chuckle is not animated as laughing from its first word.
        var target = 0f;
        if (elapsed >= startAt - 0.1f)
        {
            // Sharpening the loudness contour separates the individual calls; the raw
            // envelope is too smooth to read as distinct "ha"s on the face.
            target = Mathf.Pow(Mathf.Clamp01(Loudness), LaughterCallSharpness);
        }

        LaughterIntensity = Mathf.Lerp(LaughterIntensity, target,
            Time.deltaTime / Mathf.Max(0.01f, LaughterBlendTime));
    }

    private void LateUpdate()
    {
        if (FaceRenderer == null || baseline == null || VoiceAudioSource == null) return;

        AtPhraseBoundary = false;

        // Generation is fire-and-forget and takes ~1.2s, so curves for a clip can land
        // after that clip has already started playing. Keep looking until they show up
        // rather than only checking once at clip start, which would drop them entirely.
        if (active == null && pendingClipId != 0 && cache.TryGetValue(pendingClipId, out active) && VerboseLogging)
        {
            Debug.Log($"[SAiD] picked up curves for clip id={pendingClipId}");
        }

        var elapsed = playbackStartedAt >= 0f ? Time.time - playbackStartedAt : -1f;
        var withinClip = elapsed >= 0f && elapsed <= playbackLength;

        if (active != null && withinClip)
        {
            // Position comes from our own clock: PlayOneShot leaves AudioSource.time stuck
            // at 0, so there is no audio-side playhead to read. Both this clock and the
            // audio start in the same frame, so they stay aligned for an utterance.
            var t = elapsed * active.Fps;
            var i0 = Mathf.Clamp(Mathf.FloorToInt(t), 0, active.FrameCount - 1);
            var i1 = Mathf.Min(i0 + 1, active.FrameCount - 1);
            var frac = Mathf.Clamp01(t - i0);

            for (var i = 0; i < touched.Length; i++)
            {
                accum[touched[i]] = 0f;
            }

            for (var c = 0; c < active.Targets.Length; c++)
            {
                var targets = active.Targets[c];
                if (targets.Length == 0) continue;

                var coeff = Mathf.Lerp(active.Frames[i0, c], active.Frames[i1, c], frac) * Intensity;
                if (coeff <= 0.0001f) continue;

                // Several ARKit channels can map onto the same avatar shape; sum their
                // contributions rather than letting whichever runs last win.
                for (var k = 0; k < targets.Length; k++)
                {
                    accum[targets[k].Index] += coeff * targets[k].Weight;
                }
            }

            for (var i = 0; i < touched.Length; i++)
            {
                var idx = touched[i];
                var desired = Mathf.Clamp(baseline[idx] + accum[idx], 0f, 100f);
                current[idx] = Mathf.SmoothDamp(current[idx], desired, ref velocity[idx], SmoothTime);
                FaceRenderer.SetBlendShapeWeight(idx, current[idx]);
            }

            UpdateSpeechEnergy(i0, i1, frac);
            UpdateProsody(elapsed);
            IsSpeaking = true;
        }
        else
        {
            SpeechEnergy = Mathf.Lerp(SpeechEnergy, 0f, Time.deltaTime * 6f);
            Pitch = Mathf.Lerp(Pitch, 0.5f, Time.deltaTime * 4f);
            Loudness = Mathf.Lerp(Loudness, 0f, Time.deltaTime * 6f);
            AtPhraseBoundary = IsSpeaking;   // the frame speech stops is a phrase boundary
            IsSpeaking = false;
            IsVoiced = false;

            // Between utterances: ease every driven shape back to baseline so the face
            // doesn't freeze mid-phoneme.
            for (var i = 0; i < touched.Length; i++)
            {
                var idx = touched[i];
                if (Mathf.Abs(current[idx] - baseline[idx]) < 0.05f) continue;
                current[idx] = Mathf.SmoothDamp(current[idx], baseline[idx], ref velocity[idx], SmoothTime * 3f);
                FaceRenderer.SetBlendShapeWeight(idx, current[idx]);
            }
        }
    }
}

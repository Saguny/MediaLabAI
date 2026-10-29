using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Judges which emotion a read line carried, from two independent sources:
//   voice  emotion2vec+ (Tools~/emotion), a model trained to classify the emotion of speech.
//          It hears what pitch and loudness alone cannot: a shaky, breathy "scared" voice
//          is not simply higher or quieter.
//   face   the visitor's webcam blendshapes recorded during the line (only when they agreed
//          to be filmed): raised inner brows and wide eyes, a frown, a smile.
// The voice counts most; the face nudges it, since children read with their eyes on the
// script and their faces often stay neutral even when the voice does not.
//
// The result is only used for feedback when it is clear (Confident): the coach would rather
// ask about the character than tell a child a wrong thing about their own voice.
public static class SpeechEmotion
{
    // The classes both sources are reduced to. emotion2vec's "disgusted" counts as angry,
    // "other" and "unknown" are dropped.
    public static readonly string[] Classes = { "angry", "fearful", "happy", "sad", "surprised", "neutral" };

    public class Reading
    {
        public Dictionary<string, float> Voice;   // null if the service did not answer
        public Dictionary<string, float> Face;    // null without tracked frames
        public Dictionary<string, float> Combined = new Dictionary<string, float>();
        public string Heard;
        public float Confidence;
        public bool Confident;
    }

    // The scene's emotions (as in hexe_scenario.json) and the classes that express them.
    // A line wants one of these to be heard; "suspicious" has no clear sound of its own, so
    // anything but cheerful will do.
    private static readonly Dictionary<string, string[]> Targets = new Dictionary<string, string[]>
    {
        { "scared", new[] { "fearful" } },
        { "nervous", new[] { "fearful", "surprised" } },
        { "afraid", new[] { "fearful" } },
        { "frustrated", new[] { "angry", "sad" } },
        { "angry", new[] { "angry" } },
        { "sad", new[] { "sad", "fearful" } },
        { "hungry", new[] { "sad" } },
        { "surprised", new[] { "surprised", "fearful" } },
        { "suspicious", new[] { "angry", "neutral", "sad" } },
        { "happy", new[] { "happy" } },
        { "brave", new[] { "angry", "happy" } },
    };

    // How a child would name what was heard.
    public static string Word(string heard)
    {
        switch (heard)
        {
            case "fearful": return "scared";
            case "neutral": return "calm";
            default: return heard;
        }
    }

    public static string[] TargetClasses(string lineEmotion) =>
        !string.IsNullOrEmpty(lineEmotion) && Targets.TryGetValue(lineEmotion.ToLowerInvariant(), out var t) ? t : null;

    public static float VoiceWeight = 0.75f;
    public static float ConfidentAbove = 0.45f;

    public static Reading Judge(Dictionary<string, float> voice, List<Dictionary<string, float>> faceFrames)
    {
        var r = new Reading { Voice = voice, Face = FaceScores(faceFrames) };
        if (r.Voice == null && r.Face == null) return r;

        var wv = r.Voice != null ? (r.Face != null ? VoiceWeight : 1f) : 0f;
        foreach (var c in Classes)
        {
            var v = r.Voice != null && r.Voice.TryGetValue(c, out var pv) ? pv : 0f;
            var f = r.Face != null && r.Face.TryGetValue(c, out var pf) ? pf : 0f;
            r.Combined[c] = wv * v + (1f - wv) * f;
        }
        var total = r.Combined.Values.Sum();
        if (total <= 1e-4f) return r;
        foreach (var c in Classes) r.Combined[c] /= total;

        var top = r.Combined.OrderByDescending(kv => kv.Value).First();
        r.Heard = top.Key;
        r.Confidence = top.Value;
        // A face alone is too weak a signal to tell a child how they sounded.
        r.Confident = r.Voice != null && top.Value >= ConfidentAbove;
        return r;
    }

    // How much of the line's intended emotion was there, 0..1, or NaN if not measurable.
    public static float Match(Reading r, string lineEmotion)
    {
        var targets = TargetClasses(lineEmotion);
        if (r == null || r.Heard == null || targets == null) return float.NaN;
        return Mathf.Clamp01(targets.Sum(c => r.Combined.TryGetValue(c, out var p) ? p : 0f));
    }

    // Face cues per class from ARKit blendshapes, averaged over the line. Deliberately simple,
    // after the Facial Action Coding System's prototypes (Ekman & Friesen): fear raises the
    // inner brows and widens the eyes, anger lowers the brows and presses the lips, sadness
    // pulls the mouth corners down, happiness smiles, surprise opens the jaw under raised brows.
    private static Dictionary<string, float> FaceScores(List<Dictionary<string, float>> frames)
    {
        if (frames == null || frames.Count < 5) return null;
        float Avg(params string[] names)
        {
            var sum = 0f;
            foreach (var frame in frames)
            {
                var v = 0f;
                foreach (var n in names) v += frame.TryGetValue(n, out var x) ? x : 0f;
                sum += v / names.Length;
            }
            return sum / frames.Count;
        }

        var browInner = Avg("browInnerUp");
        var browOuter = Avg("browOuterUpLeft", "browOuterUpRight");
        var browDown = Avg("browDownLeft", "browDownRight");
        var eyeWide = Avg("eyeWideLeft", "eyeWideRight");
        var smile = Avg("mouthSmileLeft", "mouthSmileRight");
        var frown = Avg("mouthFrownLeft", "mouthFrownRight");
        var press = Avg("mouthPressLeft", "mouthPressRight");
        var stretch = Avg("mouthStretchLeft", "mouthStretchRight");
        var jaw = Avg("jawOpen");

        var scores = new Dictionary<string, float>
        {
            { "fearful", browInner * 0.5f + eyeWide * 0.3f + stretch * 0.2f },
            { "angry", browDown * 0.6f + press * 0.4f },
            { "sad", browInner * 0.3f + frown * 0.7f },
            { "happy", smile },
            // Reading aloud opens the jaw anyway; only raised brows on top of it count.
            { "surprised", Mathf.Min(jaw, browOuter + eyeWide * 0.5f) },
        };
        var strongest = scores.Values.Max();
        scores["neutral"] = Mathf.Clamp01(0.35f - strongest);
        return scores;
    }
}

// Client for the emotion service. Fails quietly: without it the feedback falls back to the
// prosody measurements alone.
public class EmotionClient
{
    public string Url = "http://127.0.0.1:8920/emotion";
    public int TimeoutSec = 6;

    private bool down;
    private float retryAt;

    public async UniTask<Dictionary<string, float>> ClassifyAsync(float[] samples, int sampleRate, CancellationToken token)
    {
        if (string.IsNullOrEmpty(Url) || samples == null || samples.Length < sampleRate / 2) return null;
        // An absent service costs one timeout, not one per line.
        if (down && Time.realtimeSinceStartup < retryAt) return null;

        using (var req = new UnityWebRequest(Url, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(WhisperClient.ToWav(samples, sampleRate)) { contentType = "audio/wav" };
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = TimeoutSec;
            try
            {
                await req.SendWebRequest().ToUniTask(cancellationToken: token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (UnityWebRequestException ex) when (ex.ResponseCode >= 400 && ex.ResponseCode < 500)
            {
                return null;   // the service is up; this recording was unusable (too short)
            }
            catch (Exception ex)
            {
                if (!down) Debug.LogWarning($"EmotionClient: {Url} did not answer ({ex.Message}). Expression feedback uses prosody only. Is the emotion service running (start_services.ps1)?");
                down = true;
                retryAt = Time.realtimeSinceStartup + 60f;
                return null;
            }
            down = false;
            try
            {
                var scores = JObject.Parse(req.downloadHandler.text)["scores"] as JObject;
                if (scores == null) return null;
                var result = new Dictionary<string, float>();
                foreach (var p in scores.Properties())
                {
                    var key = p.Name == "disgusted" ? "angry" : p.Name;
                    if (Array.IndexOf(SpeechEmotion.Classes, key) < 0) continue;
                    result[key] = (result.TryGetValue(key, out var v) ? v : 0f) + (float)p.Value;
                }
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"EmotionClient: unreadable answer: {ex.Message}");
                return null;
            }
        }
    }
}

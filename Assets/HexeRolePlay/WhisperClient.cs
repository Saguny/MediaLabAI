using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Speech-to-text against an OpenAI-compatible transcription endpoint (the local
// faster-whisper server), asking for word timestamps. ChatdollKit's own request provider
// only asks for plain text; reading assessment needs to know *when* each word was said to
// find hesitations and measure pace.
public class WhisperClient
{
    public string Url;
    public string ApiKey;
    public string Model;
    public string Language;
    public int TimeoutSec = 20;

    public class Word
    {
        public string Text;
        public float Start;
        public float End;
        public float Probability;
    }

    public class Result
    {
        public string Text = string.Empty;
        public List<Word> Words = new List<Word>();
        public float NoSpeechProbability;
        public bool HasWordTimings => Words.Count > 0;
        public bool Failed;
    }

    // Phrases Whisper is known to produce from silence or noise. Only discarded when the
    // audio also looks like no speech, since a visitor can genuinely say "thank you".
    private static readonly HashSet<string> Hallucinations = new HashSet<string>
    {
        "thank you", "thank you.", "thanks for watching!", "thanks for watching.", "you", "bye.", "bye!",
        "thank you for watching.", "thank you very much.", ".", "...", "okay."
    };

    private bool wordTimestampsUnsupported;

    public async UniTask<Result> TranscribeAsync(float[] samples, int sampleRate, bool wordTimestamps, string prompt, CancellationToken token)
    {
        var result = new Result();
        if (samples == null || samples.Length < sampleRate / 10 || string.IsNullOrEmpty(Url))
        {
            result.Failed = string.IsNullOrEmpty(Url);
            return result;
        }

        var verbose = wordTimestamps && !wordTimestampsUnsupported;
        var form = new WWWForm();
        form.AddField("model", Model ?? "whisper-1");
        if (!string.IsNullOrEmpty(Language)) form.AddField("language", Language);
        form.AddField("response_format", verbose ? "verbose_json" : "json");
        if (verbose) form.AddField("timestamp_granularities[]", "word");
        form.AddField("temperature", "0");
        if (!string.IsNullOrEmpty(prompt)) form.AddField("prompt", prompt);
        form.AddBinaryData("file", ToWav(Normalized(samples), sampleRate), "speech.wav", "audio/wav");

        using (var req = UnityWebRequest.Post(Url, form))
        {
            req.timeout = TimeoutSec;
            if (!string.IsNullOrEmpty(ApiKey)) req.SetRequestHeader("Authorization", $"Bearer {ApiKey}");
            try
            {
                await req.SendWebRequest().ToUniTask(cancellationToken: token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A 4xx on verbose_json most likely means the server does not support word
                // timestamps; fall back to plain text once and remember it.
                if (verbose && req.responseCode >= 400 && req.responseCode < 500)
                {
                    wordTimestampsUnsupported = true;
                    Debug.LogWarning($"WhisperClient: word timestamps rejected ({req.responseCode}), using plain transcripts.");
                    return await TranscribeAsync(samples, sampleRate, false, prompt, token);
                }
                Debug.LogWarning($"WhisperClient: request failed: {ex.Message}");
                result.Failed = true;
                return result;
            }

            try
            {
                Parse(req.downloadHandler.text, result);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"WhisperClient: could not parse response: {ex.Message}");
                result.Failed = true;
            }
        }

        var normalized = result.Text.Trim().ToLowerInvariant();
        var seconds = samples.Length / (float)sampleRate;
        if (Hallucinations.Contains(normalized) && (result.NoSpeechProbability > 0.5f || seconds < 1.2f))
        {
            result.Text = string.Empty;
            result.Words.Clear();
        }
        return result;
    }

    public static void Parse(string body, Result result)
    {
        var trimmed = body.TrimStart();
        if (!trimmed.StartsWith("{"))
        {
            result.Text = body.Trim();
            return;
        }

        var root = JObject.Parse(body);
        result.Text = ((string)root["text"] ?? string.Empty).Trim();

        if (root["words"] is JArray words)
        {
            foreach (var w in words)
            {
                result.Words.Add(new Word
                {
                    Text = ((string)w["word"] ?? string.Empty).Trim(),
                    Start = (float?)w["start"] ?? 0f,
                    End = (float?)w["end"] ?? 0f,
                    Probability = (float?)w["probability"] ?? 1f
                });
            }
        }

        if (root["segments"] is JArray segments && segments.Count > 0)
        {
            var sum = 0f;
            foreach (var s in segments) sum += (float?)s["no_speech_prob"] ?? 0f;
            result.NoSpeechProbability = sum / segments.Count;

            // Some servers attach words to segments instead of the top level.
            if (result.Words.Count == 0)
            {
                foreach (var s in segments)
                {
                    if (!(s["words"] is JArray segWords)) continue;
                    foreach (var w in segWords)
                    {
                        result.Words.Add(new Word
                        {
                            Text = ((string)w["word"] ?? string.Empty).Trim(),
                            Start = (float?)w["start"] ?? 0f,
                            End = (float?)w["end"] ?? 0f,
                            Probability = (float?)w["probability"] ?? 1f
                        });
                    }
                }
            }
        }
    }

    // The server's voice detector (Silero, always on in speaches) drops quiet speech before
    // Whisper sees it, so a soft voice or a low-gain USB microphone is raised to a normal level.
    // Only the copy sent for recognition - loudness feedback still uses the real level.
    private static float[] Normalized(float[] samples)
    {
        var peak = 0f;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        if (peak < 1e-4f) return samples;
        var gain = Math.Min(0.7f / peak, 10f);   // at most +20 dB, so silence stays quiet
        if (gain <= 1.05f) return samples;
        var copy = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++) copy[i] = samples[i] * gain;
        return copy;
    }

    public static byte[] ToWav(float[] samples, int sampleRate)
    {
        using (var stream = new MemoryStream(44 + samples.Length * 2))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + samples.Length * 2);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(samples.Length * 2);
            foreach (var s in samples)
            {
                writer.Write((short)Mathf.Clamp(s * 32767f, -32768f, 32767f));
            }
            writer.Flush();
            return stream.ToArray();
        }
    }
}

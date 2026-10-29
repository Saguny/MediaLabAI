using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using ChatdollKit.IO;
using UnityEngine;

// Records one turn of the visitor's speech from ChatdollKit's microphone.
//
// ChatdollKit's recorders end a turn after a fixed silence, which is right for chat and
// wrong for reading aloud: readers pause mid-sentence to decode a word, and cutting them off
// there both truncates the line and tells them they were too slow. So a pause here is a
// question, not an ending - in reading mode the caller is asked whether the line is done
// (by checking how much of it has been heard), and listening simply continues if not.
//
// The microphone is buffered all the time, not just while a turn is open: people answer the
// instant the avatar stops talking, often before the director gets round to listening, and
// those first words used to be lost. When a turn opens, the audio since the avatar's voice
// last stopped is replayed into it first. The avatar's own output is never part of that - with
// speakers it would be transcribed as the visitor's words.
public class SpeechCapture : MonoBehaviour
{
    public ChatdollMicrophone Microphone;

    [Header("Device")]
    [Tooltip("If the microphone's configured device is missing (Windows renames USB devices, e.g. \"2- USB PnP Audio Device\", when they move ports), use the first device whose name contains this.")]
    public string PreferredDeviceKeyword = "USB PnP";

    [Header("Voice detection")]
    [Tooltip("Lowest level in dB (mean amplitude, ChatdollMicrophone's scale) the threshold may drop to. The live threshold adapts upward to the room's noise floor.")]
    public float MinThresholdDb = -50f;
    [Tooltip("How far above the measured noise floor speech has to be.")]
    public float AboveNoiseDb = 11f;
    [Tooltip("Voice must add up to this long before a turn starts, so a cough or a door does not open one.")]
    public float OnsetSeconds = 0.08f;
    [Tooltip("Audio kept from before the level first crosses the threshold - soft first syllables are underway by then.")]
    public float PreRollSeconds = 0.6f;
    [Tooltip("How much of the microphone is kept while no turn is open, so speech that starts just before listening begins is still caught.")]
    public float HistorySeconds = 2.5f;
    [Tooltip("After the avatar's own audio stops, ignore the microphone this much longer: through speakers its voice arrives late (room, audio latency).")]
    public float EchoTailSeconds = 0.15f;
    public bool LogCaptures = true;

    // True while the avatar's voice or a stage sound is playing.
    public Func<bool> IsOutputPlaying;

    public struct Options
    {
        public float NoSpeechTimeout;   // give up if nobody starts speaking within this
        public float EndSilence;        // a pause this long ends the turn (or asks IsComplete, if set)
        public float HardEndSilence;    // with IsComplete: a pause this long ends it regardless
        public float MaxDuration;
        public Func<float[], int, CancellationToken, UniTask<bool>> IsComplete;  // "is the turn over?", asked at each pause
    }

    public class Result
    {
        public float[] Samples = Array.Empty<float>();
        public int SampleRate = 16000;
        public bool HeardSpeech;
        public string TypedText;       // operator input instead of audio
        public bool ForcedEnd;
        public int LastVoiceSample;    // end of the last loud chunk in Samples; a transcript of at least this much covers every word
        public float StartTime;        // realtimeSinceStartup at Samples[0], to line the audio up with the face recorded meanwhile
        public float Seconds => Samples.Length / (float)Mathf.Max(1, SampleRate);
    }

    public bool IsCapturing { get; private set; }
    public bool IsVoiceActive { get; private set; }
    // The visitor's voice right now, turn open or not, with the avatar's own output gated out.
    // Listener feedback (nods, brows) runs off this rather than a fixed level.
    public bool UserVoiceActive => Time.realtimeSinceStartup - lastUserVoiceAt < 0.25f;
    public float NoiseFloorDb { get; private set; } = -60f;
    public float CurrentThresholdDb => Mathf.Max(MinThresholdDb, NoiseFloorDb + AboveNoiseDb);
    public float LevelDb { get; private set; } = -99f;
    public string DeviceInUse => Microphone == null ? "none" : string.IsNullOrEmpty(Microphone.DeviceName) ? "(Windows default)" : Microphone.DeviceName;

    private struct Chunk
    {
        public float[] Samples;
        public int Rate;
        public float Db;
        public float Time;     // realtimeSinceStartup when it arrived
        public bool Echo;      // the avatar was audible
        public float Seconds => Samples.Length / (float)Rate;
    }

    private readonly Queue<Chunk> history = new Queue<Chunk>();
    private readonly Queue<Chunk> incoming = new Queue<Chunk>();
    private float historySeconds;
    private float lastOutputAt = -10f;
    private float lastUserVoiceAt = -10f;
    private MicrophoneCapturedData lastSeen;
    private string typedText;
    private bool forceEnd;

    // Level over a sliding 30 ms window: a frame's worth of samples can be a few milliseconds,
    // shorter than one pitch period, and its average jumps around too much to threshold.
    private float[] levelWindow = new float[480];
    private int levelIndex;
    private float levelSum;

    private void Start()
    {
        if (Microphone == null) return;
        var devices = UnityEngine.Microphone.devices;
        var configured = Microphone.DeviceName;
        if (!string.IsNullOrEmpty(configured) && Array.IndexOf(devices, configured) < 0)
        {
            var match = string.IsNullOrEmpty(PreferredDeviceKeyword) ? null
                : devices.FirstOrDefault(d => d.IndexOf(PreferredDeviceKeyword, StringComparison.OrdinalIgnoreCase) >= 0);
            Debug.LogWarning($"SpeechCapture: microphone '{configured}' not found - using {(match != null ? $"'{match}'" : "the Windows default")}.");
            Microphone.DeviceName = match ?? string.Empty;
            if (Microphone.IsListening) Microphone.StartListening();
        }
        Debug.Log($"SpeechCapture: using {DeviceInUse}. Available: {string.Join(", ", devices)}");
    }

    // Always on: the noise floor is known before a turn starts, and the recent past is at hand
    // when one does.
    private void LateUpdate()
    {
        if (Microphone == null || !Microphone.IsListening) return;
        var now = Time.realtimeSinceStartup;
        if (IsOutputPlaying != null && IsOutputPlaying()) lastOutputAt = now;

        var data = Microphone.CapturedData;
        if (data == null || ReferenceEquals(data, lastSeen) || data.Data.Length == 0) return;
        lastSeen = data;

        var mono = Downmix(data.Data, data.ChannelCount);
        var chunk = new Chunk
        {
            Samples = mono,
            Rate = data.Frequency,
            Db = MeasureLevel(mono, data.Frequency),
            Time = now,
            Echo = now - lastOutputAt < EchoTailSeconds,
        };
        LevelDb = chunk.Db;
        if (!chunk.Echo && chunk.Db > CurrentThresholdDb) lastUserVoiceAt = now;

        if (IsCapturing)
        {
            incoming.Enqueue(chunk);
            return;
        }

        if (!chunk.Echo) TrackNoiseFloor(chunk.Db, chunk.Seconds);
        if (chunk.Echo)
        {
            history.Clear();
            historySeconds = 0f;
            return;
        }
        history.Enqueue(chunk);
        historySeconds += chunk.Seconds;
        while (history.Count > 0 && historySeconds - history.Peek().Seconds > HistorySeconds)
        {
            historySeconds -= history.Dequeue().Seconds;
        }
    }

    private float MeasureLevel(float[] mono, int rate)
    {
        var size = Mathf.Max(64, rate * 3 / 100);
        if (levelWindow.Length != size)
        {
            levelWindow = new float[size];
            levelIndex = 0;
            levelSum = 0f;
        }
        foreach (var s in mono)
        {
            var a = Math.Abs(s);
            levelSum += a - levelWindow[levelIndex];
            levelWindow[levelIndex] = a;
            levelIndex = (levelIndex + 1) % size;
        }
        return 20f * Mathf.Log10(Mathf.Max(levelSum / size, 0.00001f));
    }

    private void TrackNoiseFloor(float db, float seconds)
    {
        if (float.IsNaN(db) || float.IsInfinity(db) || db < -95f) return;
        // Falls quickly, rises slowly: the floor is the quiet between sounds, not their average.
        var tau = db < NoiseFloorDb ? 0.08f : 3f;
        NoiseFloorDb = Mathf.Lerp(NoiseFloorDb, db, 1f - Mathf.Exp(-seconds / tau));
    }

    public void SubmitText(string text)
    {
        typedText = text;
    }

    public void ForceEnd()
    {
        forceEnd = true;
    }

    public async UniTask<Result> CaptureAsync(Options options, CancellationToken token)
    {
        var result = new Result();
        if (Microphone == null)
        {
            Debug.LogWarning("SpeechCapture: no microphone.");
            return result;
        }

        Microphone.IsMuted = false;
        typedText = null;
        forceEnd = false;
        IsVoiceActive = false;

        // Replay what was said since the avatar stopped, then carry on live.
        incoming.Clear();
        foreach (var c in history) incoming.Enqueue(c);
        history.Clear();
        historySeconds = 0f;
        IsCapturing = true;

        var recorded = new List<float>(16000 * 10);
        var preRoll = new Queue<float>();
        var started = false;
        var startWait = Time.realtimeSinceStartup;
        var voiceRun = 0f;
        var lastVoiceAt = 0f;
        var recordStartAt = 0f;
        var onsetAt = 0f;
        var peakDb = -99f;
        var chunks = 0;
        UniTask<bool>? completionCheck = null;
        var checkIssuedAtCount = 0;
        var checkedPause = false;

        try
        {
            while (true)
            {
                var now = Time.realtimeSinceStartup;

                if (!string.IsNullOrEmpty(typedText))
                {
                    result.TypedText = typedText;
                    break;
                }

                while (incoming.Count > 0)
                {
                    var chunk = incoming.Dequeue();
                    chunks++;
                    result.SampleRate = chunk.Rate;
                    var loud = !chunk.Echo && chunk.Db > CurrentThresholdDb;
                    if (!chunk.Echo) peakDb = Mathf.Max(peakDb, chunk.Db);
                    if (!loud && !started && !chunk.Echo) TrackNoiseFloor(chunk.Db, chunk.Seconds);

                    if (!started)
                    {
                        foreach (var s in chunk.Samples) preRoll.Enqueue(s);
                        var maxPre = Mathf.CeilToInt(PreRollSeconds * chunk.Rate);
                        while (preRoll.Count > maxPre) preRoll.Dequeue();

                        // Brief dips between syllables slow the count down instead of resetting it.
                        voiceRun = loud ? voiceRun + chunk.Seconds : Mathf.Max(0f, voiceRun - chunk.Seconds * 0.5f);
                        if (voiceRun >= OnsetSeconds)
                        {
                            started = true;
                            recorded.AddRange(preRoll);
                            preRoll.Clear();
                            lastVoiceAt = chunk.Time;
                            result.LastVoiceSample = recorded.Count;
                            onsetAt = chunk.Time;
                            recordStartAt = chunk.Time - Mathf.Min(PreRollSeconds, recorded.Count / (float)chunk.Rate);
                        }
                    }
                    else
                    {
                        recorded.AddRange(chunk.Samples);
                        if (loud)
                        {
                            lastVoiceAt = chunk.Time;
                            result.LastVoiceSample = recorded.Count;
                            checkedPause = false;
                        }
                    }
                }
                IsVoiceActive = started && now - lastVoiceAt < 0.25f;

                if (forceEnd)
                {
                    result.ForcedEnd = true;
                    break;
                }

                if (!started)
                {
                    if (options.NoSpeechTimeout > 0f && now - startWait > options.NoSpeechTimeout) break;
                    await UniTask.Yield(PlayerLoopTiming.PostLateUpdate, token);
                    continue;
                }

                if (options.MaxDuration > 0f && now - recordStartAt > options.MaxDuration) break;

                var silence = now - lastVoiceAt;
                if (options.IsComplete == null)
                {
                    if (silence >= options.EndSilence) break;
                    await UniTask.Yield(PlayerLoopTiming.PostLateUpdate, token);
                    continue;
                }

                // A pause is a question, not an ending.
                if (options.HardEndSilence > 0f && silence >= options.HardEndSilence) break;

                if (completionCheck.HasValue)
                {
                    var awaiter = completionCheck.Value.GetAwaiter();
                    if (awaiter.IsCompleted)
                    {
                        var complete = false;
                        try { complete = awaiter.GetResult(); } catch (Exception ex) { Debug.LogWarning($"SpeechCapture: completion check failed: {ex.Message}"); }
                        completionCheck = null;
                        // Only honour "complete" if they have not started reading again meanwhile.
                        if (complete && now - lastVoiceAt >= options.EndSilence * 0.8f) break;
                    }
                }
                else if (!checkedPause && silence >= options.EndSilence && recorded.Count > checkIssuedAtCount)
                {
                    checkedPause = true;
                    checkIssuedAtCount = recorded.Count;
                    completionCheck = options.IsComplete(recorded.ToArray(), result.SampleRate, token).Preserve();
                }
                await UniTask.Yield(PlayerLoopTiming.PostLateUpdate, token);
            }
        }
        finally
        {
            IsCapturing = false;
            IsVoiceActive = false;
            incoming.Clear();
        }

        result.HeardSpeech = started;
        result.Samples = recorded.ToArray();
        result.StartTime = recordStartAt;

        if (LogCaptures && string.IsNullOrEmpty(result.TypedText))
        {
            var what = started
                ? $"speech {result.Seconds:F1}s, began {onsetAt - startWait:+0.00;-0.00}s from listen start"
                : chunks == 0 ? "NO AUDIO from the microphone" : "no speech";
            Debug.Log($"SpeechCapture: {what}; peak {peakDb:F0} dB, threshold {CurrentThresholdDb:F0} dB, floor {NoiseFloorDb:F0} dB, device {DeviceInUse}");
        }
        return result;
    }

    private static float[] Downmix(float[] data, int channels)
    {
        if (channels <= 1) return data;
        var mono = new float[data.Length / channels];
        for (var i = 0; i < mono.Length; i++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++) sum += data[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }
}

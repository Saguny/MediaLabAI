using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

// Keeps each take of the scene - the witch's synthesized lines and the visitor's recorded
// ones, in order - and plays a take back as one continuous recording.
//
// This is GameLet's Recording Studio idea in miniature: hearing yourself, and hearing the
// difference between your first and second take, is what makes the improvement tangible.
// Both halves of the dialogue already exist as audio (TTS output and microphone capture),
// so a take is just the segments stitched at a common rate.
//
// Privacy: takes live in memory only and are cleared when the session ends, unless
// SaveTakesToDisk is switched on deliberately (e.g. for a study with consent forms).
public class SceneRecorder : MonoBehaviour
{
    public int OutputRate = 24000;
    [Tooltip("Silence between lines in the stitched take, seconds.")]
    public float GapSeconds = 0.35f;
    [Tooltip("Visitor lines are normalised toward this peak so a quiet reader is still audible next to the TTS voice.")]
    [Range(0.1f, 1f)] public float VisitorPeak = 0.7f;
    public float MaxVisitorGainDb = 14f;
    public bool SaveTakesToDisk = false;

    private readonly List<List<float[]>> takes = new List<List<float[]>>();
    private List<float[]> current;
    private AudioSource output;

    public int TakeCount => takes.Count;
    public bool IsPlaying => output != null && output.isPlaying;

    private void Awake()
    {
        output = gameObject.AddComponent<AudioSource>();
        output.playOnAwake = false;
        output.spatialBlend = 0f;
    }

    public void BeginTake()
    {
        current = new List<float[]>();
        takes.Add(current);
    }

    public void AddClip(AudioClip clip)
    {
        if (current == null || clip == null) return;
        try
        {
            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            current.Add(Resample(Downmix(data, clip.channels), clip.frequency, OutputRate));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SceneRecorder: could not read clip: {ex.Message}");
        }
    }

    public void AddVoice(float[] samples, int rate)
    {
        if (current == null || samples == null || samples.Length == 0) return;
        current.Add(Normalize(Resample(samples, rate, OutputRate)));
    }

    public AudioClip BuildTake(int index)
    {
        if (index < 0 || index >= takes.Count || takes[index].Count == 0) return null;
        var gap = Mathf.RoundToInt(GapSeconds * OutputRate);
        var total = 0;
        foreach (var seg in takes[index]) total += seg.Length + gap;

        var data = new float[total];
        var pos = 0;
        foreach (var seg in takes[index])
        {
            Array.Copy(seg, 0, data, pos, seg.Length);
            pos += seg.Length + gap;
        }

        var clip = AudioClip.Create($"Take{index + 1}", data.Length, 1, OutputRate, false);
        clip.SetData(data, 0);

        if (SaveTakesToDisk)
        {
            var path = Path.Combine(Application.persistentDataPath, $"hexe_take{index + 1}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            File.WriteAllBytes(path, WhisperClient.ToWav(data, OutputRate));
            Debug.Log($"SceneRecorder: saved {path}");
        }
        return clip;
    }

    public async UniTask PlayAsync(AudioClip clip, Action<float> onProgress, CancellationToken token)
    {
        if (clip == null) return;
        output.clip = clip;
        output.Play();
        try
        {
            while (output.isPlaying)
            {
                onProgress?.Invoke(output.time / Mathf.Max(0.01f, clip.length));
                await UniTask.Yield(token);
            }
        }
        finally
        {
            output.Stop();
            output.clip = null;
            Destroy(clip);
        }
    }

    public void Clear()
    {
        takes.Clear();
        current = null;
    }

    private float[] Normalize(float[] data)
    {
        var peak = 0f;
        foreach (var s in data) peak = Mathf.Max(peak, Mathf.Abs(s));
        if (peak < 1e-4f) return data;
        var gain = Mathf.Min(VisitorPeak / peak, Mathf.Pow(10f, MaxVisitorGainDb / 20f));
        for (var i = 0; i < data.Length; i++) data[i] *= gain;
        return data;
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

    // Linear interpolation is plenty for speech played back once over a speaker.
    private static float[] Resample(float[] data, int from, int to)
    {
        if (from == to || data.Length == 0) return (float[])data.Clone();
        var length = Mathf.Max(1, (int)((long)data.Length * to / from));
        var result = new float[length];
        var step = from / (double)to;
        for (var i = 0; i < length; i++)
        {
            var pos = i * step;
            var a = (int)pos;
            var b = Math.Min(a + 1, data.Length - 1);
            result[i] = Mathf.Lerp(data[Math.Min(a, data.Length - 1)], data[b], (float)(pos - a));
        }
        return result;
    }
}

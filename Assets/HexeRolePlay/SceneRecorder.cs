using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Keeps each take of the scene so it can be replayed as a short film afterwards.
//
// A take is two things recorded side by side:
//   segments  the witch's synthesized lines and the visitor's recorded ones, each with the
//             moment it was heard, so the voices can be laid out on a tight timeline
//   frames    ~30 samples a second of both performers: the witch's body pose and face as
//             the avatar actually showed them, and the visitor's face from the webcam
//             tracker (ARKit channels and head rotation). Replayed, the witch performs her
//             lines again and a second avatar plays the visitor's part with their own face.
//
// The waiting between lines (listening, thinking, off-script banter) is cut out: the film
// jumps from one line to the next with a short gap, and the frames are looked up at the
// matching moment of the original take.
//
// Privacy: no camera image is ever stored, only blendshape weights. Takes live in memory and
// are cleared when the session ends, unless SaveTakesToDisk is switched on deliberately.
[DefaultExecutionOrder(1000)]   // after every layer that moves the avatar, so frames hold what was shown
public class SceneRecorder : MonoBehaviour
{
    public enum Speaker { Witch, Visitor }

    [Header("Performers")]
    public Animator Witch;
    public SkinnedMeshRenderer WitchFace;
    public LiveFaceTracking VisitorTracking;

    [Header("Recording")]
    public float FrameRate = 30f;
    public int OutputRate = 24000;
    [Tooltip("Silence between lines in the replay, seconds.")]
    public float GapSeconds = 0.35f;
    [Tooltip("Visitor lines are normalised toward this peak so a quiet reader is still audible next to the TTS voice.")]
    [Range(0.1f, 1f)] public float VisitorPeak = 0.7f;
    public float MaxVisitorGainDb = 14f;
    public bool SaveTakesToDisk = false;

    public class Segment
    {
        public Speaker Speaker;
        public float[] Samples;    // mono, OutputRate
        public float Start;        // realtimeSinceStartup when it was heard
        public float Seconds;
        public string Text;
        public string Mood;        // the witch's face preset or the line's intended emotion
        public float End => Start + Seconds;
    }

    public class Frame
    {
        public float Time;
        public Quaternion[] WitchBones;   // local rotations, in WitchBones order
        public Vector3 WitchHips;         // hips local position
        public float[] WitchFace;
        public Dictionary<string, float> VisitorFace;   // null while the tracker was silent
        public Vector3 VisitorHead;
        public bool HasVisitorHead;
    }

    public class Take
    {
        public readonly List<Segment> Segments = new List<Segment>();
        public readonly List<Frame> Frames = new List<Frame>();
        public bool HasVisitorFace;
    }

    // A take laid out for replay: where each segment sits on the film's clock.
    public class Movie
    {
        public Take Take;
        public float[] Audio;
        public int Rate;
        public float Duration;
        public float Lead;
        public readonly List<(Segment Segment, float At)> Placed = new List<(Segment, float)>();

        // The moment of the original take that film time t shows.
        public float SourceTime(float t)
        {
            if (Placed.Count == 0) return 0f;
            for (var i = 0; i < Placed.Count; i++)
            {
                var (seg, at) = Placed[i];
                if (t < at)
                {
                    // Before this line: hold close to its start, coming from the previous line's end.
                    var floor = i > 0 ? Placed[i - 1].Segment.End : seg.Start - Lead;
                    return Mathf.Max(floor, seg.Start - (at - t));
                }
                if (t <= at + seg.Seconds) return seg.Start + (t - at);
            }
            var (last, lastAt) = Placed[Placed.Count - 1];
            return last.End + (t - lastAt - last.Seconds);
        }

        // The segment playing at film time t, or the one just finished during a gap.
        public int SegmentAt(float t)
        {
            var index = -1;
            for (var i = 0; i < Placed.Count; i++) if (t >= Placed[i].At) index = i;
            return index;
        }
    }

    private readonly List<Take> takes = new List<Take>();
    private Take current;
    private bool capturingFrames;
    private float nextFrameAt;
    private readonly Dictionary<AudioClip, float> clipStarts = new Dictionary<AudioClip, float>();

    public int TakeCount => takes.Count;
    public bool IsCapturing => capturingFrames;
    public Take GetTake(int index) => index >= 0 && index < takes.Count ? takes[index] : null;
    // The witch's humanoid bones, hips first. Raw local rotations rather than muscle space:
    // they replay exactly, whatever the rig's axes and scale.
    public Transform[] WitchBones { get; private set; } = new Transform[0];

    private void Start()
    {
        if (Witch == null || !Witch.isHuman) return;
        var bones = new List<Transform>();
        for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
        {
            var t = Witch.GetBoneTransform(b);
            if (t != null) bones.Add(t);
        }
        WitchBones = bones.ToArray();
    }

    public void BeginTake()
    {
        current = new Take();
        takes.Add(current);
        clipStarts.Clear();
        capturingFrames = true;
        nextFrameAt = 0f;
    }

    public void EndTake() => capturingFrames = false;

    // Wired to AvatarPerformer.ClipStarted: the moment each clip was heard.
    public void NoteClipStarted(AudioClip clip)
    {
        if (capturingFrames && clip != null) clipStarts[clip] = Time.realtimeSinceStartup;
    }

    public void AddClip(AudioClip clip, string text = null, string mood = null)
    {
        if (current == null || clip == null) return;
        try
        {
            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            var samples = Resample(Downmix(data, clip.channels), clip.frequency, OutputRate);
            var start = clipStarts.TryGetValue(clip, out var t) ? t : Time.realtimeSinceStartup - clip.length;
            Add(Speaker.Witch, samples, start, text, mood);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"SceneRecorder: could not read clip: {ex.Message}");
        }
    }

    public void AddVoice(float[] samples, int rate, float startTime, string text = null, string mood = null)
    {
        if (current == null || samples == null || samples.Length == 0) return;
        Add(Speaker.Visitor, Normalize(Resample(samples, rate, OutputRate)), startTime, text, mood);
    }

    private void Add(Speaker speaker, float[] samples, float start, string text, string mood)
    {
        current.Segments.Add(new Segment
        {
            Speaker = speaker,
            Samples = samples,
            Start = start,
            Seconds = samples.Length / (float)OutputRate,
            Text = text,
            Mood = mood,
        });
    }

    private void LateUpdate()
    {
        if (!capturingFrames || current == null) return;
        var now = Time.realtimeSinceStartup;
        if (now < nextFrameAt) return;
        nextFrameAt = Mathf.Max(nextFrameAt + 1f / FrameRate, now - 0.5f / FrameRate);

        var frame = new Frame { Time = now };
        if (WitchBones.Length > 0)
        {
            frame.WitchBones = new Quaternion[WitchBones.Length];
            for (var i = 0; i < WitchBones.Length; i++) frame.WitchBones[i] = WitchBones[i].localRotation;
            frame.WitchHips = WitchBones[0].localPosition;
        }
        if (WitchFace != null && WitchFace.sharedMesh != null)
        {
            var count = WitchFace.sharedMesh.blendShapeCount;
            frame.WitchFace = new float[count];
            for (var i = 0; i < count; i++) frame.WitchFace[i] = WitchFace.GetBlendShapeWeight(i);
        }
        if (VisitorTracking != null && VisitorTracking.IsReceiving)
        {
            frame.VisitorFace = new Dictionary<string, float>();
            foreach (var kv in VisitorTracking.Latest) frame.VisitorFace[kv.Key] = kv.Value;
            frame.HasVisitorHead = VisitorTracking.HasHead;
            frame.VisitorHead = VisitorTracking.HeadEuler;
            current.HasVisitorFace = true;
        }
        current.Frames.Add(frame);
    }

    // The visitor's face during a span of the take being recorded, e.g. while they read a line.
    public List<Dictionary<string, float>> VisitorFaces(float from, float to)
    {
        var result = new List<Dictionary<string, float>>();
        if (current == null) return result;
        foreach (var f in current.Frames)
        {
            if (f.Time >= from && f.Time <= to && f.VisitorFace != null) result.Add(f.VisitorFace);
        }
        return result;
    }

    // Lays a take out as a film: lead-in silence, the lines in order with a short gap
    // between them, and a tail. The audio is the film's soundtrack and its clock.
    public Movie BuildMovie(int index, float leadSeconds, float tailSeconds)
    {
        var take = GetTake(index);
        if (take == null || take.Segments.Count == 0) return null;

        var movie = new Movie { Take = take, Rate = OutputRate, Lead = leadSeconds };
        var segments = new List<Segment>(take.Segments);
        segments.Sort((a, b) => a.Start.CompareTo(b.Start));

        var t = leadSeconds;
        foreach (var seg in segments)
        {
            movie.Placed.Add((seg, t));
            t += seg.Seconds + GapSeconds;
        }
        movie.Duration = t - GapSeconds + tailSeconds;

        movie.Audio = new float[Mathf.CeilToInt(movie.Duration * OutputRate)];
        foreach (var (seg, at) in movie.Placed)
        {
            var pos = Mathf.RoundToInt(at * OutputRate);
            Array.Copy(seg.Samples, 0, movie.Audio, pos, Mathf.Min(seg.Samples.Length, movie.Audio.Length - pos));
        }

        if (SaveTakesToDisk)
        {
            var path = Path.Combine(Application.persistentDataPath, $"hexe_take{index + 1}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            File.WriteAllBytes(path, WhisperClient.ToWav(movie.Audio, OutputRate));
            Debug.Log($"SceneRecorder: saved {path}");
        }
        return movie;
    }

    // The two frames around a moment of the take and how far between them it lies.
    public static bool Sample(Take take, float time, out Frame a, out Frame b, out float k)
    {
        a = b = null;
        k = 0f;
        var frames = take.Frames;
        if (frames.Count == 0) return false;
        if (time <= frames[0].Time) { a = b = frames[0]; return true; }
        if (time >= frames[frames.Count - 1].Time) { a = b = frames[frames.Count - 1]; return true; }

        int lo = 0, hi = frames.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (frames[mid].Time <= time) lo = mid; else hi = mid;
        }
        a = frames[lo];
        b = frames[hi];
        k = Mathf.InverseLerp(a.Time, b.Time, time);
        return true;
    }

    public void Clear()
    {
        takes.Clear();
        current = null;
        capturingFrames = false;
        clipStarts.Clear();
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

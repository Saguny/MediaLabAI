using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Turns the per-line measurements of one take into the few things the coach will say.
//
// The rules follow the order a reading teacher would: a reader who cannot decode the words
// is not helped by advice on intonation, so accuracy comes first, then pace and fluency,
// then volume, and only then expression. And the feedback is always one point at a time.
//
// Honesty rule: the coach only states an observation when the numbers clearly support it.
// When they do not, she asks the visitor about the character instead - the reflection
// question does the pedagogical work on its own, with or without a measurement behind it.
public static class ReadingFeedback
{
    [Serializable]
    public class Thresholds
    {
        [Header("Volume (RMS dBFS of voiced frames)")]
        public float QuietDb = -42f;
        public float ClearDb = -34f;

        [Header("Accuracy (share of script words recognised)")]
        public float AccuracyNeedsWork = 0.6f;
        public float AccuracySuper = 0.9f;

        [Header("Speed (words per minute over the spoken span; L2 readers aged 11-13 read ~80-130, fluent adults ~150-190)")]
        public float SlowWpm = 60f;
        public float FastWpm = 205f;
        public float SuperMinWpm = 85f;
        public float SuperMaxWpm = 180f;
        [Tooltip("Mid-phrase pauses per line at which fluency becomes the focus.")]
        public float HesitationsPerLine = 1.5f;

        [Header("Expression")]
        [Tooltip("Pitch range below this (semitones) reads as flat.")]
        public float FlatRangeSt = 4.5f;
        public float LivelyRangeSt = 9f;
        [Tooltip("Mean direction fit below this counts as not matching the character.")]
        public float FitNeedsWork = 0.15f;
    }

    public enum Level { NeedsWork, Good, Super, Unknown }

    public enum Focus { Microphone, Accuracy, Fast, Slow, Hesitant, Quiet, Expression, Deepen }

    // Scales that turn each dimension into roughly comparable units: the size of a change
    // an expressive reader would make between two lines.
    private const float LoudScale = 3f;     // dB
    private const float PitchScale = 1.5f;  // semitones
    private const float RateScale = 0.15f;  // log(wpm)
    private const float MelodyScale = 2f;   // semitones

    public class TakeSummary
    {
        public List<ReadingAnalyzer.LineMetrics> Lines = new List<ReadingAnalyzer.LineMetrics>();
        public List<HexeScenario.SceneLine> Targets = new List<HexeScenario.SceneLine>();

        public int LinesWithAudio;
        public float Accuracy;
        public float Wpm;
        public float HesitationsPerLine;
        public float LoudnessDb;
        public float PitchRangeSt;
        public float Variation;          // how much the voice changes from line to line
        public float Fit;                // how well those changes follow the character
        public float[] LineFit = Array.Empty<float>();

        public Level Volume = Level.Unknown;
        public Level AccuracyLevel = Level.Unknown;
        public Level Speed = Level.Unknown;
        public Level Expression = Level.Unknown;

        public bool Usable => LinesWithAudio >= Mathf.Max(1, Lines.Count / 2);
    }

    public class Plan
    {
        public Focus Focus;
        public string PositiveKey;
        public string ObservationKey;
        public string QuestionKey;
        public string TipKey;
        public int LineIndex = -1;       // the scene line the question is about, if any
    }

    public class Comparison
    {
        public bool Measurable;
        public bool Better;
        public float Score;
        public List<string> ChangeKeys = new List<string>();
    }

    public static TakeSummary Summarize(List<ReadingAnalyzer.LineMetrics> lines, List<HexeScenario.SceneLine> targets, Thresholds th)
    {
        var s = new TakeSummary { Lines = lines, Targets = targets };
        var audio = lines.Where(l => l.HasAudio).ToList();
        s.LinesWithAudio = audio.Count;

        var scored = lines.Where(l => l.HasAudio || l.Typed).ToList();
        s.Accuracy = scored.Count > 0 ? scored.Average(l => l.Accuracy) : 0f;
        s.Wpm = audio.Count > 0 ? audio.Average(l => l.Wpm) : 0f;
        s.HesitationsPerLine = audio.Count > 0 ? (float)audio.Average(l => l.Hesitations) : 0f;

        var prosodic = audio.Where(l => l.HasProsody).ToList();
        s.LoudnessDb = prosodic.Count > 0 ? prosodic.Average(l => l.LoudnessDb) : float.NaN;
        s.PitchRangeSt = prosodic.Count > 0 ? prosodic.Average(l => l.PitchRangeSt) : float.NaN;

        s.LineFit = new float[lines.Count];
        if (prosodic.Count >= 2)
        {
            var refLoud = prosodic.Average(l => l.LoudnessDb);
            var refPitch = prosodic.Average(l => l.PitchSt);
            var refRate = prosodic.Average(l => Mathf.Log(Mathf.Max(1f, l.Wpm)));
            var refMelody = prosodic.Average(l => l.PitchRangeSt);

            var fits = new List<float>();
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (!l.HasProsody || i >= targets.Count) continue;
                var d = targets[i].direction ?? new HexeScenario.VoiceDirection();
                var parts = new List<float>();
                if (d.loudness != 0) parts.Add(Mathf.Clamp(d.loudness * (l.LoudnessDb - refLoud) / LoudScale, -1f, 1f));
                if (d.pitch != 0) parts.Add(Mathf.Clamp(d.pitch * (l.PitchSt - refPitch) / PitchScale, -1f, 1f));
                if (d.rate != 0) parts.Add(Mathf.Clamp(d.rate * (Mathf.Log(Mathf.Max(1f, l.Wpm)) - refRate) / RateScale, -1f, 1f));
                if (d.melody != 0) parts.Add(Mathf.Clamp(d.melody * (l.PitchRangeSt - refMelody) / MelodyScale, -1f, 1f));
                if (parts.Count == 0) continue;
                s.LineFit[i] = parts.Average();
                fits.Add(s.LineFit[i]);
            }
            s.Fit = fits.Count > 0 ? fits.Average() : 0f;

            s.Variation = new[]
            {
                Std(prosodic.Select(l => l.LoudnessDb)) / LoudScale,
                Std(prosodic.Select(l => l.PitchSt)) / PitchScale,
                Std(prosodic.Select(l => Mathf.Log(Mathf.Max(1f, l.Wpm)))) / RateScale,
                Std(prosodic.Select(l => l.PitchRangeSt)) / MelodyScale
            }.Average();
        }

        // Criteria grid, as in GameLet's peer feedback sheet.
        if (audio.Count > 0 && !float.IsNaN(s.LoudnessDb))
        {
            s.Volume = s.LoudnessDb < th.QuietDb ? Level.NeedsWork : s.LoudnessDb < th.ClearDb ? Level.Good : Level.Super;
        }
        if (scored.Count > 0)
        {
            s.AccuracyLevel = s.Accuracy < th.AccuracyNeedsWork ? Level.NeedsWork : s.Accuracy < th.AccuracySuper ? Level.Good : Level.Super;
        }
        if (audio.Count > 0)
        {
            var speed = s.Wpm < th.SlowWpm || s.Wpm > th.FastWpm ? Level.NeedsWork
                      : s.Wpm >= th.SuperMinWpm && s.Wpm <= th.SuperMaxWpm ? Level.Super : Level.Good;
            if (s.HesitationsPerLine >= th.HesitationsPerLine && speed != Level.NeedsWork) speed--;
            s.Speed = speed;
        }
        if (prosodic.Count >= 2)
        {
            var range = Mathf.Clamp01((s.PitchRangeSt - th.FlatRangeSt) / Mathf.Max(0.1f, th.LivelyRangeSt - th.FlatRangeSt));
            var score = 0.45f * Mathf.Clamp01(s.Variation / 1.2f) + 0.3f * range + 0.25f * Mathf.Clamp01((s.Fit + 0.2f) / 0.8f);
            s.Expression = score < 0.35f ? Level.NeedsWork : score < 0.65f ? Level.Good : Level.Super;
        }
        return s;
    }

    public static Plan PlanFirstFeedback(TakeSummary s, Thresholds th)
    {
        var plan = new Plan { PositiveKey = PickPositive(s) };

        if (!s.Usable)
        {
            plan.Focus = Focus.Microphone;
            plan.PositiveKey = null;
            plan.ObservationKey = "obs_mic";
            plan.TipKey = "tip_mic";
            return plan;
        }
        if (s.AccuracyLevel == Level.NeedsWork)
        {
            plan.Focus = Focus.Accuracy;
            plan.ObservationKey = "obs_accuracy";
            plan.QuestionKey = "q_accuracy";
            plan.TipKey = "tip_accuracy";
            return plan;
        }
        if (s.Wpm > th.FastWpm)
        {
            plan.Focus = Focus.Fast;
            plan.ObservationKey = "obs_fast";
            plan.QuestionKey = "q_fast";
            plan.TipKey = "tip_fast";
            return plan;
        }
        if (s.Wpm > 0f && s.Wpm < th.SlowWpm)
        {
            plan.Focus = Focus.Slow;
            plan.ObservationKey = "obs_slow";
            plan.QuestionKey = "q_slow";
            plan.TipKey = "tip_slow";
            return plan;
        }
        if (s.HesitationsPerLine >= th.HesitationsPerLine)
        {
            plan.Focus = Focus.Hesitant;
            plan.ObservationKey = "obs_hesitant";
            plan.QuestionKey = "q_fluency";
            plan.TipKey = "tip_fluency";
            return plan;
        }
        if (s.Volume == Level.NeedsWork)
        {
            plan.Focus = Focus.Quiet;
            plan.ObservationKey = "obs_quiet";
            plan.QuestionKey = "q_quiet";
            plan.TipKey = "tip_quiet";
            return plan;
        }

        // Expression. With no usable prosody, fall through to the question alone.
        var flat = !float.IsNaN(s.PitchRangeSt) && s.PitchRangeSt < th.FlatRangeSt;
        var monotone = s.Variation < 0.45f;
        if (s.Expression == Level.Unknown || s.Expression == Level.NeedsWork || s.Fit < th.FitNeedsWork)
        {
            plan.Focus = Focus.Expression;
            if (s.Expression != Level.Unknown)
            {
                // A steady, loud read is the classic "confident narrator" voice - right for
                // a podcast host, wrong for a frightened boy.
                if (monotone && s.Volume != Level.NeedsWork) plan.ObservationKey = "obs_steady";
                else if (monotone || flat) plan.ObservationKey = "obs_same";
                else plan.ObservationKey = null;   // changes were there, just not matching: let the question do it
            }
            plan.QuestionKey = "q_emotion_yesno";
            plan.TipKey = "tip_emotion";
            return plan;
        }

        // Already expressive: praise, then ask about the line that fitted least.
        plan.Focus = Focus.Deepen;
        plan.PositiveKey = "deepen_praise";
        plan.QuestionKey = "q_deepen";
        plan.TipKey = "deepen_tip";
        plan.LineIndex = WeakestLine(s);
        return plan;
    }

    private static string PickPositive(TakeSummary s)
    {
        if (s.AccuracyLevel == Level.Super) return "pos_accuracy";
        if (s.Speed == Level.Super) return "pos_fluent";
        if (s.Expression == Level.Super) return "pos_expressive";
        if (s.Volume == Level.Super) return "pos_volume";
        return "pos_effort";
    }

    private static int WeakestLine(TakeSummary s)
    {
        var best = -1;
        var min = float.MaxValue;
        for (var i = 0; i < s.LineFit.Length && i < s.Targets.Count; i++)
        {
            if (!s.Targets[i].IsStudent || !s.Lines[i].HasProsody) continue;
            if (s.LineFit[i] < min) { min = s.LineFit[i]; best = i; }
        }
        return best;
    }

    // Did the second take move toward the character, line by line? Each line is measured
    // against its own take's average before the takes are compared, so a visitor who simply
    // stood closer to the microphone the second time does not count as "more expressive" -
    // only a change in how the lines differ from one another does.
    public static Comparison Compare(TakeSummary first, TakeSummary second)
    {
        var c = new Comparison();
        var contributions = new Dictionary<string, List<float>>();
        void Add(string key, float v)
        {
            if (!contributions.TryGetValue(key, out var list)) contributions[key] = list = new List<float>();
            list.Add(v);
        }

        var a = Deviations(first);
        var b = Deviations(second);
        var all = new List<float>();
        var n = Mathf.Min(first.Lines.Count, second.Lines.Count);
        for (var i = 0; i < n && i < first.Targets.Count; i++)
        {
            if (a[i] == null || b[i] == null) continue;
            var d = first.Targets[i].direction ?? new HexeScenario.VoiceDirection();
            void Dim(int dir, int index, float scale, string key)
            {
                if (dir == 0) return;
                var v = Mathf.Clamp(dir * (b[i][index] - a[i][index]) / scale, -1f, 1f);
                Add(key, v);
                all.Add(v);
            }
            Dim(d.loudness, 0, LoudScale, d.loudness < 0 ? "change_loudness_down" : "change_loudness_up");
            Dim(d.pitch, 1, PitchScale, "change_pitch");
            Dim(d.rate, 2, RateScale, "change_rate");
            Dim(d.melody, 3, MelodyScale, "change_melody");
        }

        c.Measurable = all.Count >= 2;
        if (!c.Measurable) return c;

        c.Score = all.Average();
        var variationGain = second.Variation - first.Variation;
        var rangeGain = float.IsNaN(first.PitchRangeSt) || float.IsNaN(second.PitchRangeSt) ? 0f : second.PitchRangeSt - first.PitchRangeSt;
        c.Better = c.Score > 0.2f || variationGain > 0.3f || rangeGain > 1.5f;

        if (c.Better)
        {
            var main = contributions
                .Select(kv => new { kv.Key, Mean = kv.Value.Average() })
                .Where(x => x.Mean > 0.15f)
                .OrderByDescending(x => x.Mean)
                .FirstOrDefault();
            if (main != null) c.ChangeKeys.Add(main.Key);
            else if (rangeGain > 1.5f) c.ChangeKeys.Add("change_melody");
        }

        if (second.HesitationsPerLine + 0.5f < first.HesitationsPerLine) c.ChangeKeys.Add("change_fluency");
        else if (second.Accuracy > first.Accuracy + 0.15f) c.ChangeKeys.Add("change_accuracy");
        return c;
    }

    // Per line: loudness, pitch, log rate and pitch range, each minus the take's mean.
    private static float[][] Deviations(TakeSummary take)
    {
        var result = new float[take.Lines.Count][];
        var prosodic = take.Lines.Where(l => l.HasProsody).ToList();
        if (prosodic.Count < 2) return result;
        var mean = new[]
        {
            prosodic.Average(l => l.LoudnessDb),
            prosodic.Average(l => l.PitchSt),
            prosodic.Average(l => Mathf.Log(Mathf.Max(1f, l.Wpm))),
            prosodic.Average(l => l.PitchRangeSt)
        };
        for (var i = 0; i < take.Lines.Count; i++)
        {
            var l = take.Lines[i];
            if (!l.HasProsody) continue;
            result[i] = new[]
            {
                l.LoudnessDb - mean[0],
                l.PitchSt - mean[1],
                Mathf.Log(Mathf.Max(1f, l.Wpm)) - mean[2],
                l.PitchRangeSt - mean[3]
            };
        }
        return result;
    }

    public static string Label(Level level)
    {
        switch (level)
        {
            case Level.NeedsWork: return "Needs work";
            case Level.Good: return "Good";
            case Level.Super: return "Super";
            default: return "-";
        }
    }

    private static float Std(IEnumerable<float> values)
    {
        var list = values.Where(v => !float.IsNaN(v)).ToList();
        if (list.Count < 2) return 0f;
        var mean = list.Average();
        return Mathf.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1));
    }
}

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
// "Clearly" is meant literally:
//   - a change between lines or takes counts only once it stands out from how much the
//     lines move anyway (Certainty standard errors clear of zero);
//   - "better" and "worse" are only said when she can also say what changed;
//   - every line she says about the reading names only what was measured. "Your voice got
//     softer there" can be measured, "you sounded scared" cannot;
//   - the emotion model stays out until it has been checked (EmotionModelInFeedback).
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
        [Tooltip("Share of the intended emotion the emotion model must hear, on average, to praise it.")]
        public float EmotionSuper = 0.5f;
        [Tooltip("Below this the emotion model clearly heard something else.")]
        public float EmotionNeedsWork = 0.25f;

        // Fields added later keep these defaults in scenes saved before them.
        [Header("Volume against the room (dB above the noise floor), used when measured")]
        [Tooltip("The level alone mostly measures how close the reader stands to the microphone.")]
        public float QuietSnrDb = 15f;
        public float ClearSnrDb = 25f;

        [Header("Evidence")]
        [Tooltip("Standard errors a change between lines or takes must stand clear of zero before the coach mentions it.")]
        public float Certainty = 2f;
        [Tooltip("Lowest accuracy at which 'you read very smoothly' may be said.")]
        public float SmoothMinAccuracy = 0.8f;
        [Tooltip("Let the emotion model's verdicts into what the coach says. Off, they are only logged: in the logged sessions " +
                 "emotion2vec+ heard 'neutral' in nearly every line children read, so it stays out until it is checked against labelled recordings.")]
        public bool EmotionModelInFeedback = false;
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
        public int SkippedLines;         // lines the visitor asked to go past
        public float Accuracy;
        public float Wpm;
        public float HesitationsPerLine;
        public float LoudnessDb;
        public float SnrDb = float.NaN;
        public float PitchRangeSt;
        public float Variation;          // how much the voice changes from line to line
        public float Fit;                // how well those changes follow the character
        public float FitLow = float.NegativeInfinity;   // Fit less Certainty standard errors
        public float[] LineFit = Array.Empty<float>();
        public float Certainty = 2f;

        public int EmotionLines;         // lines the emotion model judged
        public float EmotionMatch = float.NaN;
        public string HeardEmotion;      // what was confidently heard in most lines, if anything
        public bool EmotionMeasured => EmotionLines >= 2;
        public bool EmotionTrusted;      // measured, and allowed into what the coach says

        public Level Volume = Level.Unknown;
        public Level AccuracyLevel = Level.Unknown;
        public Level Speed = Level.Unknown;
        public Level Expression = Level.Unknown;

        public bool Usable => LinesWithAudio >= Mathf.Max(1, (Lines.Count - SkippedLines) / 2);
        // The voice clearly moved the way the character asks for, line by line.
        public bool FitsClearly(Thresholds th) => Fit >= th.FitNeedsWork && FitLow > 0f;
    }

    public class Plan
    {
        public Focus Focus;
        public string PositiveKey;
        public string ObservationKey;
        public string QuestionKey;
        public string TipKey;
        public int LineIndex = -1;       // the scene line the question is about, if any
        public Dictionary<string, string> Vars = new Dictionary<string, string>();
    }

    public class Comparison
    {
        public bool Measurable;
        public bool Better;
        public bool Worse;
        public bool Strong;              // better by enough to say so loudly
        public float Score;
        public List<string> ChangeKeys = new List<string>();
    }

    public static TakeSummary Summarize(List<ReadingAnalyzer.LineMetrics> lines, List<HexeScenario.SceneLine> targets, Thresholds th)
    {
        var s = new TakeSummary { Lines = lines, Targets = targets, Certainty = th.Certainty };
        var audio = lines.Where(l => l.HasAudio).ToList();
        s.LinesWithAudio = audio.Count;
        s.SkippedLines = lines.Count(l => l.Skipped);

        var scored = lines.Where(l => l.HasAudio || l.Typed).ToList();
        s.Accuracy = scored.Count > 0 ? scored.Average(l => l.Accuracy) : 0f;
        s.Wpm = audio.Count > 0 ? audio.Average(l => l.Wpm) : 0f;
        s.HesitationsPerLine = audio.Count > 0 ? (float)audio.Average(l => l.Hesitations) : 0f;

        var prosodic = audio.Where(l => l.HasProsody).ToList();
        s.LoudnessDb = prosodic.Count > 0 ? prosodic.Average(l => l.LoudnessDb) : float.NaN;
        var withSnr = prosodic.Where(l => !float.IsNaN(l.SnrDb)).ToList();
        s.SnrDb = withSnr.Count > 0 ? withSnr.Average(l => l.SnrDb) : float.NaN;
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
            // Five lines of a child reading move about anyway; the fit has to stand out from that.
            if (fits.Count >= 2) s.FitLow = s.Fit - th.Certainty * Std(fits) / Mathf.Sqrt(fits.Count);

            s.Variation = new[]
            {
                Std(prosodic.Select(l => l.LoudnessDb)) / LoudScale,
                Std(prosodic.Select(l => l.PitchSt)) / PitchScale,
                Std(prosodic.Select(l => Mathf.Log(Mathf.Max(1f, l.Wpm)))) / RateScale,
                Std(prosodic.Select(l => l.PitchRangeSt)) / MelodyScale
            }.Average();
        }

        // Criteria grid, as in GameLet's peer feedback sheet. Volume against the room where the
        // floor was measured, so standing a step further from the microphone isn't "quieter".
        if (!float.IsNaN(s.SnrDb))
        {
            s.Volume = s.SnrDb < th.QuietSnrDb ? Level.NeedsWork : s.SnrDb < th.ClearSnrDb ? Level.Good : Level.Super;
        }
        else if (audio.Count > 0 && !float.IsNaN(s.LoudnessDb))
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
        var judged = audio.Where(l => l.HasEmotion).ToList();
        s.EmotionLines = judged.Count;
        if (judged.Count > 0) s.EmotionMatch = judged.Average(l => l.EmotionMatch);
        s.HeardEmotion = judged.Where(l => l.EmotionConfident)
            .GroupBy(l => l.HeardEmotion)
            .Where(g => g.Count() * 2 >= judged.Count)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        s.EmotionTrusted = th.EmotionModelInFeedback && s.EmotionMeasured;

        // Expression: how much the voice moves and whether it moves with the character, and,
        // where the emotion model is trusted, whether the character's feeling was heard.
        var prosodyScore = float.NaN;
        if (prosodic.Count >= 2)
        {
            var range = Mathf.Clamp01((s.PitchRangeSt - th.FlatRangeSt) / Mathf.Max(0.1f, th.LivelyRangeSt - th.FlatRangeSt));
            prosodyScore = 0.45f * Mathf.Clamp01(s.Variation / 1.2f) + 0.3f * range + 0.25f * Mathf.Clamp01((s.Fit + 0.2f) / 0.8f);
        }
        var emotionScore = s.EmotionTrusted ? Mathf.Clamp01(s.EmotionMatch / Mathf.Max(0.05f, th.EmotionSuper)) : float.NaN;
        var expression = float.IsNaN(prosodyScore) ? emotionScore
                       : float.IsNaN(emotionScore) ? prosodyScore
                       : 0.55f * prosodyScore + 0.45f * emotionScore;
        if (!float.IsNaN(expression))
        {
            s.Expression = expression < 0.35f ? Level.NeedsWork : expression < 0.65f ? Level.Good : Level.Super;
        }
        return s;
    }

    public static Plan PlanFirstFeedback(TakeSummary s, Thresholds th)
    {
        var plan = new Plan { PositiveKey = PickPositive(s, th) };

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
        var otherEmotion = s.EmotionTrusted && s.EmotionMatch < th.EmotionNeedsWork && s.HeardEmotion != null;
        if (s.Expression == Level.Unknown || s.Expression == Level.NeedsWork || !s.FitsClearly(th) || otherEmotion)
        {
            plan.Focus = Focus.Expression;
            if (otherEmotion)
            {
                // The model clearly heard a different feeling: say which, then ask about the character.
                plan.ObservationKey = "obs_emotion";
                plan.Vars["heard"] = SpeechEmotion.Word(s.HeardEmotion);
            }
            else if (s.Expression != Level.Unknown)
            {
                // A steady, clear read with little change between lines: the classic narrator
                // voice - right for a podcast host, wrong for a frightened boy.
                if (monotone && s.Volume != Level.NeedsWork) plan.ObservationKey = "obs_steady";
                else if (monotone || flat) plan.ObservationKey = "obs_same";
                else plan.ObservationKey = null;   // changes were there, just not clearly matching: let the question do it
            }
            plan.QuestionKey = "q_emotion_yesno";
            plan.TipKey = "tip_emotion";
            return plan;
        }

        // The voice clearly followed the character: praise that, then ask about the line that fitted least.
        plan.Focus = Focus.Deepen;
        plan.PositiveKey = "deepen_praise";
        plan.QuestionKey = "q_deepen";
        plan.TipKey = "deepen_tip";
        plan.LineIndex = WeakestLine(s);
        return plan;
    }

    private static string PickPositive(TakeSummary s, Thresholds th)
    {
        // Hearing the character's feeling is what the whole play is about, so it comes first.
        if (s.EmotionTrusted && s.EmotionMatch >= th.EmotionSuper) return "pos_emotion";
        if (s.AccuracyLevel == Level.Super) return "pos_accuracy";
        // "Smoothly" is more than a pace: no stops mid-sentence, and the words mostly right.
        if (s.Speed == Level.Super && s.HesitationsPerLine < 0.5f && s.Accuracy >= th.SmoothMinAccuracy) return "pos_fluent";
        if (s.Expression == Level.Super && s.PitchRangeSt >= th.LivelyRangeSt) return "pos_expressive";
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
            var fit = s.LineFit[i] + (s.EmotionTrusted && s.Lines[i].HasEmotion ? s.Lines[i].EmotionMatch - 0.5f : 0f);
            if (fit < min) { min = fit; best = i; }
        }
        return best;
    }

    // Did the second take move toward the character, line by line? Each line is measured
    // against its own take's average before the takes are compared, so a visitor who simply
    // stood closer to the microphone the second time does not count as "more expressive" -
    // only a change in how the lines differ from one another does.
    //
    // Two takes of the same reading differ anyway (the child moves, the pitch tracker slips),
    // so a change only counts once it stands out from how much the lines moved between the
    // takes, and "better" or "worse" is only said together with what changed.
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

        var bothEmotion = first.EmotionTrusted && second.EmotionTrusted;
        var emotionGain = bothEmotion ? second.EmotionMatch - first.EmotionMatch : 0f;
        // A take cut short is not a worse take, just a different one.
        var comparable = first.LinesWithAudio >= 2 && second.LinesWithAudio >= Mathf.CeilToInt(first.LinesWithAudio * 0.6f);
        c.Measurable = comparable && (all.Count >= 2 || bothEmotion);
        if (!c.Measurable) return c;

        // How much the per-line changes scatter, pooled over every directed dimension: the yardstick
        // for whether a mean change is real. A mean is kept only by what it stands out beyond that.
        var spread = all.Count >= 3 ? Std(all) : 1f;
        float Clear(List<float> values)
        {
            if (values.Count == 0) return 0f;
            var mean = values.Average();
            return Mathf.Sign(mean) * Mathf.Max(0f, Mathf.Abs(mean) - second.Certainty * spread / Mathf.Sqrt(values.Count));
        }

        var direction = Clear(all);
        var variationGain = second.Variation - first.Variation;
        var rangeGain = float.IsNaN(first.PitchRangeSt) || float.IsNaN(second.PitchRangeSt) ? 0f : second.PitchRangeSt - first.PitchRangeSt;
        var accuracyGain = second.Accuracy - first.Accuracy;
        var hesitationGain = first.HesitationsPerLine - second.HesitationsPerLine;

        // Every way a take can differ gets a say, in both directions. Any single gain used to be
        // enough for "much better", and stumbling through a take makes the voice jump around,
        // which then counted in its favour. Following the character matters most.
        // How much the voice moves overall weighs least: over five lines it is mostly noise.
        var parts = new List<(float Value, float Weight)>
        {
            (Mathf.Clamp((variationGain / 0.4f + rangeGain / 2f) / 2f, -1f, 1f), 0.1f),
            (Mathf.Clamp(accuracyGain / 0.12f, -1f, 1f), 0.15f),
            (Mathf.Clamp(hesitationGain / 0.75f, -1f, 1f), 0.1f),
        };
        if (bothEmotion) parts.Add((Mathf.Clamp(emotionGain / 0.2f, -1f, 1f), 0.4f));
        if (all.Count > 0) parts.Add((Mathf.Clamp(direction / 0.3f, -1f, 1f), 0.3f));
        c.Score = parts.Sum(p => p.Value * p.Weight) / parts.Sum(p => p.Weight);

        // A clear step back in what matters is never called better, however the rest went.
        var lostFeeling = emotionGain < -0.15f;
        var lostWords = accuracyGain < -0.12f;
        var stumbled = hesitationGain < -0.75f;
        var lostDirection = direction < -0.15f;
        var flatter = variationGain < -0.3f || rangeGain < -1.5f;
        var slipped = lostFeeling || lostWords || stumbled;

        if (c.Score < -0.15f || (slipped && c.Score < 0.05f))
        {
            if (lostFeeling) c.ChangeKeys.Add("worse_emotion");
            if (lostWords) c.ChangeKeys.Add("worse_accuracy");
            if (stumbled) c.ChangeKeys.Add("worse_fluency");
            if (lostDirection) c.ChangeKeys.Add("worse_direction");
            if (flatter) c.ChangeKeys.Add("worse_flat");
            // "I liked your first take more" without a reason is an opinion, not feedback.
            c.Worse = c.ChangeKeys.Count > 0;
            if (!c.Worse) c.ChangeKeys.Clear();
            return c;
        }
        if (slipped) return c;

        // The feeling itself coming through is the biggest news, said before how the voice changed.
        if (emotionGain > 0.15f) c.ChangeKeys.Add("change_emotion");
        if (c.Score > 0.2f)
        {
            var main = contributions
                .Select(kv => new { kv.Key, Mean = Clear(kv.Value) })
                .Where(x => x.Mean > 0.15f)
                .OrderByDescending(x => x.Mean)
                .FirstOrDefault();
            if (main != null) c.ChangeKeys.Add(main.Key);
            // No single kind of change stands out on its few lines, but all of them together do.
            else if (direction > 0.15f) c.ChangeKeys.Add("change_direction");
        }
        if (hesitationGain > 0.5f) c.ChangeKeys.Add("change_fluency");
        else if (accuracyGain > 0.15f) c.ChangeKeys.Add("change_accuracy");

        // "That was better!" only with something to point at.
        c.Better = c.Score > 0.2f && c.ChangeKeys.Count > 0;
        c.Strong = c.Better && (bothEmotion ? emotionGain > 0.15f && c.Score > 0.35f : c.Score > 0.5f && direction > 0.3f);
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

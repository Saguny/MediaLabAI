using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

// Measures one read line along the four dimensions GameLet's feedback grid uses - volume,
// accuracy, expression and speed - from the recording and its transcript.
//
// What is measured, and how much it can be trusted:
//   accuracy    word alignment of the transcript against the script. Solid for clear
//               speech; ASR on children's L2 English is imperfect, so matching is lenient
//               and the result is only ever used for coarse "some words were tricky".
//   speed       words per minute over the spoken span, plus pauses found in the signal
//               itself (not in Whisper's word boundaries, which absorb short gaps).
//   volume      RMS level of the voiced frames. Microphone-dependent in absolute terms.
//   expression  pitch (YIN) level and range, loudness range. Meaningful mainly *relative*
//               to the same reader - across lines of a take, and between takes - which is
//               how the feedback layer uses it. There is no claim to recognise emotions.
//
// Pure computation on arrays, so it can run off the main thread.
public static class ReadingAnalyzer
{
    public class Token
    {
        public string Raw;
        public string Norm;
        public bool PunctuationAfter;   // , . ! ? ; : or an ellipsis follows this word
    }

    public class Pause
    {
        public float Start;
        public float Duration;
        public bool AtPunctuation;
    }

    public class LineMetrics
    {
        public string ExpectedText;
        public string RecognizedText;
        public bool Typed;
        public bool HasAudio;

        public int ExpectedWords;
        public int RecognizedWords;
        public int MatchedWords;
        public float Accuracy;
        public List<string> MissedWords = new List<string>();

        public float SpeakingSeconds;
        public float VoicedSeconds;
        public float Wpm;
        public List<Pause> Pauses = new List<Pause>();
        public int Hesitations;          // pauses mid-phrase, i.e. not at punctuation

        public float LoudnessDb = float.NaN;       // mean RMS dBFS of voiced frames
        public float LoudnessRangeDb = float.NaN;  // p90 - p10 of voiced frame levels
        public float PitchSt = float.NaN;          // median F0, semitones re 100 Hz
        public float PitchRangeSt = float.NaN;     // p90 - p10
        public float VoicedPitchRatio;             // share of speech frames with a pitch

        public bool HasProsody => HasAudio && !float.IsNaN(PitchSt) && !float.IsNaN(LoudnessDb);
    }

    // ---------------------------------------------------------------- text

    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(text)) return tokens;
        foreach (Match m in Regex.Matches(text, @"[\p{L}\p{N}']+([^\p{L}\p{N}'\s]*)"))
        {
            var word = Regex.Match(m.Value, @"[\p{L}\p{N}']+").Value;
            var norm = Normalize(word);
            if (norm.Length == 0) continue;
            var trailing = m.Groups[1].Value;
            tokens.Add(new Token
            {
                Raw = word,
                Norm = norm,
                PunctuationAfter = Regex.IsMatch(trailing, @"[,.!?;:…\-]")
            });
        }
        return tokens;
    }

    public static string Normalize(string word)
    {
        return Regex.Replace(word.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    }

    public static bool Similar(string a, string b)
    {
        if (a == b) return true;
        if (a.Length == 0 || b.Length == 0) return false;
        var longer = Math.Max(a.Length, b.Length);
        var shorter = Math.Min(a.Length, b.Length);
        if (shorter >= 4 && (a.StartsWith(b) || b.StartsWith(a))) return true;
        var d = Levenshtein(a, b);
        if (longer >= 7) return d <= 2;
        if (longer >= 4) return d <= 1;
        return false;
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            var t = prev; prev = cur; cur = t;
        }
        return prev[b.Length];
    }

    // For each expected token, the index of the recognized token it aligned to, or -1.
    // Plain edit-distance alignment with fuzzy word equality.
    public static int[] Align(List<Token> expected, List<string> recognizedNorm)
    {
        var n = expected.Count;
        var m = recognizedNorm.Count;
        var cost = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) cost[i, 0] = i;
        for (var j = 0; j <= m; j++) cost[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var sub = cost[i - 1, j - 1] + (Similar(expected[i - 1].Norm, recognizedNorm[j - 1]) ? 0 : 1);
                cost[i, j] = Math.Min(sub, Math.Min(cost[i - 1, j] + 1, cost[i, j - 1] + 1));
            }
        }

        var map = Enumerable.Repeat(-1, n).ToArray();
        int ii = n, jj = m;
        while (ii > 0 && jj > 0)
        {
            var same = Similar(expected[ii - 1].Norm, recognizedNorm[jj - 1]);
            if (cost[ii, jj] == cost[ii - 1, jj - 1] + (same ? 0 : 1))
            {
                if (same) map[ii - 1] = jj - 1;
                ii--; jj--;
            }
            else if (cost[ii, jj] == cost[ii - 1, jj] + 1) ii--;
            else jj--;
        }
        return map;
    }

    public static List<string> NormalizedWords(string text)
    {
        return Tokenize(text).Select(t => t.Norm).ToList();
    }

    // Share of the line heard so far. Used while the visitor is still reading, to decide
    // whether a pause is the end of the line or just a pause.
    public static float Coverage(string expected, string recognized)
    {
        var exp = Tokenize(expected);
        if (exp.Count == 0) return 1f;
        var map = Align(exp, NormalizedWords(recognized));
        return map.Count(x => x >= 0) / (float)exp.Count;
    }

    // True if the last words of the line were heard, near the end of what was said.
    public static bool ReachedEnd(string expected, string recognized)
    {
        var exp = Tokenize(expected);
        var rec = NormalizedWords(recognized);
        if (exp.Count == 0 || rec.Count == 0) return false;
        var tail = rec.Skip(Math.Max(0, rec.Count - 4)).ToList();
        var lastWords = exp.Skip(Math.Max(0, exp.Count - 2)).ToList();
        return lastWords.Any(w => tail.Any(r => Similar(w.Norm, r)));
    }

    // ---------------------------------------------------------------- full analysis

    public static LineMetrics Analyze(string expectedText, WhisperClient.Result stt, float[] samples, int sampleRate, float noiseFloorDb)
    {
        var metrics = new LineMetrics { ExpectedText = expectedText, RecognizedText = stt?.Text ?? string.Empty };
        var expected = Tokenize(expectedText);
        metrics.ExpectedWords = expected.Count;

        var recWords = stt != null && stt.HasWordTimings
            ? stt.Words.Select(w => Normalize(w.Text)).Where(w => w.Length > 0).ToList()
            : NormalizedWords(metrics.RecognizedText);
        metrics.RecognizedWords = recWords.Count;

        var map = Align(expected, recWords);
        metrics.MatchedWords = map.Count(x => x >= 0);
        metrics.Accuracy = expected.Count > 0 ? metrics.MatchedWords / (float)expected.Count : 0f;
        for (var i = 0; i < expected.Count; i++)
        {
            if (map[i] < 0) metrics.MissedWords.Add(expected[i].Raw);
        }

        if (samples == null || samples.Length < sampleRate / 4) return metrics;

        var frames = FrameLevels(samples, sampleRate, out var hop);
        var gate = Mathf.Max(-58f, noiseFloorDb + 8f);
        var voicedIdx = new List<int>();
        for (var i = 0; i < frames.Length; i++) if (frames[i] > gate) voicedIdx.Add(i);
        metrics.VoicedSeconds = voicedIdx.Count * hop;
        metrics.HasAudio = metrics.VoicedSeconds >= 0.35f;
        if (!metrics.HasAudio) return metrics;

        // Spoken span: from word timings if we have them, else from the energy envelope.
        float spanStart, spanEnd;
        var timed = stt != null && stt.HasWordTimings ? stt.Words.Where(w => Normalize(w.Text).Length > 0).ToList() : null;
        if (timed != null && timed.Count >= 2)
        {
            spanStart = timed.First().Start;
            spanEnd = timed.Last().End;
        }
        else
        {
            spanStart = voicedIdx.First() * hop;
            spanEnd = (voicedIdx.Last() + 1) * hop;
        }
        metrics.SpeakingSeconds = Mathf.Max(0.3f, spanEnd - spanStart);
        var wordsForRate = metrics.RecognizedWords > 0 ? metrics.RecognizedWords : metrics.ExpectedWords;
        metrics.Wpm = wordsForRate / metrics.SpeakingSeconds * 60f;

        // Pauses from the signal, inside the spoken span.
        var firstFrame = Mathf.Clamp(Mathf.FloorToInt(spanStart / hop), 0, frames.Length - 1);
        var lastFrame = Mathf.Clamp(Mathf.CeilToInt(spanEnd / hop), 0, frames.Length - 1);
        var run = 0;
        for (var i = firstFrame; i <= lastFrame; i++)
        {
            if (frames[i] <= gate) { run++; continue; }
            if (run * hop >= 0.35f)
            {
                var start = (i - run) * hop;
                metrics.Pauses.Add(new Pause { Start = start, Duration = run * hop, AtPunctuation = PauseAtPunctuation(start, timed, expected, map, recWords) });
            }
            run = 0;
        }
        metrics.Hesitations = metrics.Pauses.Count(p => !p.AtPunctuation && p.Duration >= 0.5f);

        // Loudness over voiced frames.
        var voicedLevels = voicedIdx.Select(i => frames[i]).OrderBy(x => x).ToList();
        metrics.LoudnessDb = 10f * Mathf.Log10(voicedLevels.Average(db => Mathf.Pow(10f, db / 10f)));
        metrics.LoudnessRangeDb = Percentile(voicedLevels, 0.9f) - Percentile(voicedLevels, 0.1f);

        // Pitch.
        var pitches = PitchTrack(samples, sampleRate, frames, hop, gate);
        metrics.VoicedPitchRatio = voicedIdx.Count > 0 ? pitches.Count / (float)Mathf.Max(1, voicedIdx.Count / 2) : 0f;
        if (pitches.Count >= 8)
        {
            var st = pitches.Select(f => 12f * Mathf.Log(f / 100f, 2f)).OrderBy(x => x).ToList();
            metrics.PitchSt = Percentile(st, 0.5f);
            metrics.PitchRangeSt = Percentile(st, 0.9f) - Percentile(st, 0.1f);
        }
        return metrics;
    }

    // A pause counts as phrasing when the word before it ends a phrase in the script.
    private static bool PauseAtPunctuation(float pauseStart, List<WhisperClient.Word> timed, List<Token> expected, int[] map, List<string> recWords)
    {
        if (timed == null || timed.Count == 0) return false;
        var before = -1;
        for (var k = 0; k < timed.Count; k++)
        {
            if (timed[k].End <= pauseStart + 0.2f) before = k;
            else break;
        }
        if (before < 0) return false;
        for (var i = 0; i < map.Length; i++)
        {
            if (map[i] == before) return expected[i].PunctuationAfter;
        }
        return false;
    }

    // RMS level per 20ms frame, 10ms hop.
    private static float[] FrameLevels(float[] samples, int rate, out float hopSeconds)
    {
        var frame = Mathf.Max(1, rate / 50);
        var hop = Mathf.Max(1, rate / 100);
        hopSeconds = hop / (float)rate;
        var count = Mathf.Max(0, (samples.Length - frame) / hop + 1);
        var levels = new float[count];
        for (var f = 0; f < count; f++)
        {
            double sum = 0;
            var offset = f * hop;
            for (var i = 0; i < frame; i++)
            {
                var s = samples[offset + i];
                sum += s * s;
            }
            levels[f] = 10f * Mathf.Log10((float)(sum / frame) + 1e-10f);
        }
        return levels;
    }

    // YIN (de Cheveigné & Kawahara 2002), on every second energy frame that is voiced.
    // 70-500 Hz covers adult voices and children's up to about age 12.
    private static List<float> PitchTrack(float[] samples, int rate, float[] levels, float hop, float gate)
    {
        var result = new List<float>();
        var window = Mathf.RoundToInt(0.025f * rate);
        var tauMin = Mathf.Max(2, rate / 500);
        var tauMax = rate / 70;
        var diff = new float[tauMax + 2];
        var hopSamples = Mathf.RoundToInt(hop * rate);

        for (var f = 0; f < levels.Length; f += 2)
        {
            if (levels[f] <= gate + 3f) continue;
            var start = f * hopSamples;
            if (start + window + tauMax + 1 >= samples.Length) break;

            for (var tau = 1; tau <= tauMax + 1; tau++)
            {
                double d = 0;
                for (var j = 0; j < window; j++)
                {
                    var delta = samples[start + j] - samples[start + j + tau];
                    d += delta * delta;
                }
                diff[tau] = (float)d;
            }

            // Cumulative mean normalised difference.
            diff[0] = 1f;
            var running = 0f;
            for (var tau = 1; tau <= tauMax + 1; tau++)
            {
                running += diff[tau];
                diff[tau] = running > 0f ? diff[tau] * tau / running : 1f;
            }

            var best = -1;
            for (var tau = tauMin; tau <= tauMax; tau++)
            {
                if (diff[tau] < 0.15f)
                {
                    while (tau + 1 <= tauMax && diff[tau + 1] < diff[tau]) tau++;
                    best = tau;
                    break;
                }
            }
            if (best < 0)
            {
                var min = float.MaxValue;
                for (var tau = tauMin; tau <= tauMax; tau++)
                {
                    if (diff[tau] < min) { min = diff[tau]; best = tau; }
                }
                if (min > 0.3f) continue;
            }

            // Parabolic interpolation around the dip.
            var t = (float)best;
            if (best > 1 && best < tauMax)
            {
                var a = diff[best - 1]; var b = diff[best]; var c = diff[best + 1];
                var denom = a - 2f * b + c;
                if (Mathf.Abs(denom) > 1e-6f) t = best + 0.5f * (a - c) / denom;
            }
            var f0 = rate / t;
            if (f0 >= 70f && f0 <= 500f) result.Add(f0);
        }
        return result;
    }

    public static float Percentile(List<float> sorted, float p)
    {
        if (sorted.Count == 0) return float.NaN;
        var pos = Mathf.Clamp01(p) * (sorted.Count - 1);
        var lo = Mathf.FloorToInt(pos);
        var hi = Mathf.Min(lo + 1, sorted.Count - 1);
        return Mathf.Lerp(sorted[lo], sorted[hi], pos - lo);
    }
}

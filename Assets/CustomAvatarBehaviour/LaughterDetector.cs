using System.Text.RegularExpressions;
using UnityEngine;

// Recognises laughter in a line of dialogue before it is spoken.
//
// Laughter is not a facial pose - it is a respiratory event. A bout is a run of "ha" calls
// on one exhalation, the calls crowd together at the start and space out toward the end,
// and loudness decays across the bout. So it cannot be rendered by holding an expression;
// it needs its own rhythm, driven in time with the audio.
//
// Detection runs on the text rather than the waveform because the text is available before
// synthesis, which leaves time to prepare, and because acoustic laughter detection on
// synthetic speech is unreliable - the TTS voices "haha" as ordinary syllables.
public static class LaughterDetector
{
    // Matches the ways a chat model actually writes laughter. Deliberately excludes "lol"
    // and similar written-only forms: the TTS either spells them out or skips them, so
    // animating a laugh over them would desync from what is heard.
    private static readonly Regex LaughPattern = new Regex(
        @"\b(?:(?:ha){2,}|(?:he){2,}|(?:hee){2,}|(?:heh){2,}|(?:hah){2,})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool Contains(string text)
    {
        return !string.IsNullOrEmpty(text) && LaughPattern.IsMatch(text);
    }

    // Where the laughter starts, as a fraction through the line. Used to hold the laugh
    // envelope back until the audio actually reaches it, instead of laughing from the
    // first word of a sentence that only ends in a chuckle.
    public static float FirstOccurrenceFraction(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var m = LaughPattern.Match(text);
        return m.Success ? Mathf.Clamp01((float)m.Index / Mathf.Max(1, text.Length)) : 0f;
    }
}

using System.Collections.Generic;
using System.Text.RegularExpressions;

// What a visitor can say to steer the session, wherever it has got to: skip what is happening,
// leave the scene, or answer something she has not asked yet. The script is her plan, not the
// visitor's obligation. Pure text rules, so they stay fast enough to check while she is talking.
public static class LooseDirection
{
    public enum Command { None, NextLine, EndScene }

    // Questions she may ask later, for the model to tick off when a visitor answers one early.
    public const string Harder = "harder", Enjoy = "enjoy", Tricky = "tricky", Different = "different";

    public const string TopicPrompt =
        "harder = whether the words or the feelings were harder; enjoy = whether they enjoyed acting; " +
        "tricky = whether the words were hard to read; different = whether another go felt different";

    public static readonly string[] AllTopics = { Harder, Enjoy, Tricky, Different };

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const int MaxCommandWords = 8;   // longer than this is a sentence that happens to hold the word

    private static readonly Regex KeepGoing = new Regex(@"\b(don't|do not|not|never)\s+(want\s+to\s+|wanna\s+)?(skip|move on)\b", Options);

    private static readonly Regex MoveOnWords = new Regex(
        @"\bskip\b|\b(move|moving)\s+on\b" +
        @"|^\W*((um|okay|ok|so)\W+)?next\b(\W+(one|line|part|question|please))?\W*$" +
        @"|\bnext\s+(question|part|thing|one|line)\b" +
        @"|\b(let's|lets|can we|could we|just)\s+(continue|keep going)\b" +
        @"|^\W*((okay|ok)\W+)?(that's|thats)?\s*enough\W*$" +
        @"|\bübersprin|^\W*weiter\W*$",
        Options);

    private static readonly Regex EndSceneWords = new Regex(
        @"^\W*cut\W*$" +
        @"|\b(cut|end|finish|stop|skip|leave)\b(\W+[\w']+){0,2}?\W+(scene|take|rest|role.?play)\b" +
        @"|\bskip\s+to\s+the\s+end\b",
        Options);

    private static readonly Regex HardWord = new Regex(@"\b(hard|harder|hardest|difficult|tricky|easy|easier)\b", Options);
    private static readonly Regex HarderSubject = new Regex(@"\b(word|words|reading|feeling|feelings|emotion|emotions|acting|everything|both)\b", Options);
    private static readonly Regex WordsSubject = new Regex(@"\b(word|words|reading|read|pronounc\w*)\b", Options);
    private static readonly Regex EnjoyWords = new Regex(@"\b(fun|enjoyed|enjoy it|loved it|liked it|boring)\b", Options);

    // "can I see my movie?", "show me the film", "let's watch the video" - in any sentence,
    // since it usually rides along with an answer ("I'd be scared, but can I see my movie?").
    private static readonly Regex MovieWords = new Regex(
        @"\b(see|watch|show|play|view)\b(\W+[\w']+){0,3}?\W+(movie|film|video|replay|recording)\b" +
        @"|\b(movie|film)\s+time\b",
        Options);
    private static readonly Regex NoMovie = new Regex(@"\b(don't|do not|not|never|no)\b(\W+[\w']+){0,3}?\W+(see|watch|show|play|view)\b", Options);

    public static bool WantsMovie(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Replace('’', '\'');
        return MovieWords.IsMatch(t) && !NoMovie.IsMatch(t);
    }

    public static bool MoveOn(string text)
    {
        if (!Short(text, out var t) || KeepGoing.IsMatch(t)) return false;
        return MoveOnWords.IsMatch(t) || EndSceneWords.IsMatch(t);
    }

    // During the scene: the end of the scene wins, because "skip the scene" also holds a "skip".
    public static Command SceneCommand(string text)
    {
        if (!Short(text, out var t) || KeepGoing.IsMatch(t)) return Command.None;
        if (EndSceneWords.IsMatch(t)) return Command.EndScene;
        return MoveOnWords.IsMatch(t) ? Command.NextLine : Command.None;
    }

    // Later questions this already answers, whatever it was said in reply to.
    public static List<string> Topics(string text)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return found;
        var t = Normal(text);
        if (HardWord.IsMatch(t) && HarderSubject.IsMatch(t)) found.Add(Harder);
        if (HardWord.IsMatch(t) && WordsSubject.IsMatch(t)) found.Add(Tricky);
        if (EnjoyWords.IsMatch(t)) found.Add(Enjoy);
        return found;
    }

    private static bool Short(string text, out string normal)
    {
        normal = string.IsNullOrWhiteSpace(text) ? string.Empty : Normal(text);
        return normal.Length > 0 && normal.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Length <= MaxCommandWords;
    }

    private static string Normal(string text) => text.Replace('’', '\'').Trim();
}

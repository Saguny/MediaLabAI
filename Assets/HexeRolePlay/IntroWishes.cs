using System.Text.RegularExpressions;

// What a visitor may ask for during the introduction without waiting to be asked: to get on with
// the scene, and to say up front whether they may be filmed.
public static class IntroWishes
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Scene = @"(role.?play|scene|acting)";
    private const string Target = @"(" + Scene + @"|the play)";
    private const string Film = @"(video|videos|film|filmed|filming|record|recorded|recording|camera|webcam)";
    private const string Gap = @"(\W+[\w']+){0,3}?\W+";

    private static readonly Regex KeepIntro = new Regex(@"\b(don't|do not|not|never)\s+([\w']+\s+)?(skip|jump|start|begin)\b", Options);

    private static readonly Regex Skip = new Regex(
        @"\bskip\b" +
        @"|\b(jump|dive|hop)\s+((right|straight|directly|immediately)\s+)?(in\b\W*$|in\W+(and|please|now)\b|ahead\b|(into|to)\W+((the|this|that)\W+)?(it|play|" + Scene + @")\b)" +
        @"|\b(straight|directly|immediately|right)\s+(in|into|to|onto)\b(\W+[\w']+){0,3}?\W+(" + Scene + @"|play|it)\b" +
        @"|\b(let's|lets|can we|could we|want to|wanna|just)\s+([\w']+\s+)?(start|begin)\b" +
        @"|\b(let's|lets)\s+go\b" +
        // "can we go to the roleplay", "move on to the scene", "get to the play"
        @"|\b(go|get|move|head)\s+(on\s+)?(to|into|onto)\W+((the|this|that|our)\W+)?(" + Scene + @"|play)\b" +
        @"|\b(start|begin)\b(\W+[\w']+){0,2}?\W+" + Target + @"\b" +
        @"|\b(let's|lets|can we|could we|want to|wanna|just|please)\W+([\w']+\W+)?(do|play)\b(\W+[\w']+){0,2}?\W+" + Target + @"\b" +
        @"|\b(no need|don't need|without|enough)\b(\W+[\w']+){0,3}?\W+(intro|introduction|explanation|explaining|small talk|questions)\b" +
        @"|\bübersprin|\b(direkt|gleich)\s+(los|anfangen|starten)\b|\blass\s+uns\s+((direkt|gleich)\s+)?(anfangen|loslegen|starten)\b",
        Options);

    private static readonly Regex Accepts = new Regex(
        @"\b(accept|accepted|agree to|consent to|allow|ok with|okay with|fine with|happy with|good with|cool with|don't mind|do not mind|no problem with)\b" + Gap + Film + @"\b" +
        @"|\b" + Film + @"\b(\W+[\w']+){0,3}?\W*(ok|okay|fine|good|cool|alright|accepted)\b" +
        @"|\b(you can|you may|go ahead and|feel free to|please)\W+([\w']+\W+){0,2}?(record|film)\b" +
        @"|\b(yes|yeah|sure|ok|okay)\W+(to\W+)?(the\W+)?" + Film + @"\b",
        Options);

    private static readonly Regex Refuses = new Regex(
        @"\b(no|not|don't|do not|never|without|skip|stop|turn off)\b" + Gap + Film + @"\b",
        Options);

    private static readonly Regex Idiom = new Regex(@"\b(don't mind|do not mind|no problem with)\b", Options);

    public static bool WantsToSkip(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = Normal(text);
        return Skip.IsMatch(t) && !KeepIntro.IsMatch(t);
    }

    // True or false only when they said so plainly. Anything unclear is null, which means she
    // asks, because being filmed is decided by the visitor and never assumed.
    public static bool? VideoConsent(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Normal(text);
        var accepts = Accepts.IsMatch(t);
        var refuses = Refuses.IsMatch(Idiom.Replace(t, " "));
        if (accepts && refuses) return null;
        if (refuses) return t.Contains("not sure") ? (bool?)null : false;
        if (accepts) return t.TrimEnd().EndsWith("?") ? (bool?)null : true;
        return null;
    }

    private static string Normal(string text) => text.Replace('’', '\'').Trim();
}

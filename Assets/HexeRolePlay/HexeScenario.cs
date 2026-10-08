using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

// The words of the role-play, loaded from StreamingAssets so the script can be changed -
// swapped for the GameLet scene text, shortened, translated - without touching the flow.
public class HexeScenario
{
    public string coachName = "Hilda";
    public string studentRole = "Hansel";
    public string sceneTitle = "";
    public string focusEmotion = "scared";
    public string focusReason = "";
    public bool offerModelReading = true;
    public int maxRounds = 2;
    public List<SceneLine> scene = new List<SceneLine>();
    public Dictionary<string, List<string>> emotionLexicon = new Dictionary<string, List<string>>();
    public List<string> focusEmotions = new List<string>();
    public Dictionary<string, List<string>> lines = new Dictionary<string, List<string>>();
    public string coachPersona = "";

    public class SceneLine
    {
        public string speaker;       // "witch" or "student"
        public string text;
        public string gesture;
        public string face;
        public string emotion;
        public string reason;
        public string modelText;
        public VoiceDirection direction = new VoiceDirection();

        [JsonIgnore] public bool IsStudent => string.Equals(speaker, "student", StringComparison.OrdinalIgnoreCase);
    }

    // Which way the voice should move on a line relative to the reader's own average, per
    // dimension: -1, 0 or +1. Relative rather than absolute because microphones, rooms and
    // voices differ far more than the changes an expressive reading makes.
    public class VoiceDirection
    {
        public int loudness;
        public int pitch;
        public int rate;
        public int melody;

        public bool IsNeutral => loudness == 0 && pitch == 0 && rate == 0 && melody == 0;
    }

    public static HexeScenario Load(string fileName)
    {
        var path = Path.Combine(Application.streamingAssetsPath, fileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"HexeScenario: {path} not found.");
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<HexeScenario>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Debug.LogError($"HexeScenario: could not parse {path}: {ex.Message}");
            return null;
        }
    }

    private readonly System.Random random = new System.Random();

    public bool Has(string key) => lines != null && lines.ContainsKey(key) && lines[key].Count > 0;

    // One variant of a line with its placeholders filled. Missing keys come back empty and
    // are logged, so a typo in the JSON skips a line rather than stopping the scene.
    public string Line(string key, Dictionary<string, string> vars = null)
    {
        if (!Has(key))
        {
            Debug.LogWarning($"HexeScenario: no line '{key}'.");
            return string.Empty;
        }
        var variants = lines[key];
        return Fill(variants[random.Next(variants.Count)], vars);
    }

    public string Fill(string text, Dictionary<string, string> vars = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = text
            .Replace("{coach}", coachName)
            .Replace("{role}", studentRole)
            .Replace("{focusEmotion}", focusEmotion)
            .Replace("{focusReason}", focusReason);
        if (vars != null)
        {
            foreach (var kv in vars) result = result.Replace("{" + kv.Key + "}", kv.Value);
        }
        return result;
    }

    // Emotion categories named in a free answer, e.g. "he's hungry and scared" -> hungry, scared.
    public List<string> FindEmotions(string answer)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(answer) || emotionLexicon == null) return found;
        var words = Tokenize(answer);
        foreach (var kv in emotionLexicon)
        {
            var hit = false;
            foreach (var entry in kv.Value)
            {
                var w = entry.ToLowerInvariant();
                foreach (var token in words)
                {
                    // Prefix match on longer stems catches inflections the list doesn't spell
                    // out ("frightening", "worrying") without letting "mad" match "made".
                    if (token == w || (w.Length >= 5 && token.StartsWith(w)))
                    {
                        hit = true;
                        break;
                    }
                }
                if (hit) break;
            }
            if (hit) found.Add(kv.Key);
        }
        return found;
    }

    public bool IsFocusEmotion(string category) => focusEmotions != null && focusEmotions.Contains(category);

    public static HashSet<string> Tokenize(string text)
    {
        var set = new HashSet<string>();
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[\p{L}']+"))
        {
            set.Add(m.Value.Trim('\''));
        }
        return set;
    }
}

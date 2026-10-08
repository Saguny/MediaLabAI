using UnityEngine;
using UnityEngine.UI;

// Screen overlay for the role-play, built entirely at runtime.
//
// The layout follows GameLet's Recording Studio: the visitor's own role is highlighted and
// big, the other role's line sits above it in a quieter style, and the feedback card uses
// the same four criteria as GameLet's peer feedback sheet (volume, accuracy, expression,
// speed). The script panel exists only during the scene - its appearing and disappearing is
// itself one of the cues that separate playing from talking about playing.
public class HexeStageUI : MonoBehaviour
{
    public StageEffects Stage;

    public enum ScriptState { WitchSpeaking, YourTurn, Listening, Done }

    private Font font;
    private RectTransform root;
    private CanvasGroup scriptGroup, bannerGroup, criteriaGroup, startGroup, playbackGroup;
    private RawImage vignette;
    private Text bannerText, witchLabel, witchLine, studentLabel, studentLine, stateText;
    private Text subtitle, visitorText, listeningText, startText, playbackText, recText;
    private Image recDot, playbackFill;
    private Text criteriaTitle;
    private readonly Text[] criteriaValues = new Text[4];
    private readonly Image[] criteriaChips = new Image[4];

    private bool scriptVisible, bannerVisible, criteriaVisible, startVisible, playbackVisible;
    private bool recording;
    private float visitorTextUntil;
    private ScriptState scriptState;

    private static readonly Color Panel = new Color(0.07f, 0.06f, 0.1f, 0.86f);
    private static readonly Color Accent = new Color(1f, 0.78f, 0.35f);
    private static readonly Color WitchTint = new Color(0.78f, 0.66f, 0.95f);

    private void Awake()
    {
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        Build();
    }

    private void Update()
    {
        var amount = Stage != null ? Stage.SceneAmount : (scriptVisible ? 1f : 0f);
        vignette.color = new Color(0f, 0f, 0f, 0.85f * amount);

        Fade(scriptGroup, scriptVisible, 5f);
        Fade(bannerGroup, bannerVisible, 5f);
        Fade(criteriaGroup, criteriaVisible, 4f);
        Fade(startGroup, startVisible, 3f);
        Fade(playbackGroup, playbackVisible, 5f);

        var pulse = 0.55f + 0.45f * Mathf.Sin(Time.time * 4f);
        if (scriptState == ScriptState.YourTurn) stateText.color = new Color(Accent.r, Accent.g, Accent.b, pulse);
        if (startVisible) startText.color = new Color(1f, 1f, 1f, 0.5f + 0.5f * pulse);
        recDot.enabled = recording && bannerVisible;
        recText.enabled = recDot.enabled;
        recDot.color = new Color(0.95f, 0.15f, 0.15f, 0.4f + 0.6f * pulse);

        if (visitorText.enabled && Time.time > visitorTextUntil) visitorText.enabled = false;
    }

    // ------------------------------------------------------------------ API

    public void ShowStartPrompt(string text)
    {
        startText.text = text;
        startVisible = !string.IsNullOrEmpty(text);
    }

    public void SetSceneFrame(bool visible, string title)
    {
        bannerVisible = visible;
        bannerText.text = "ROLE PLAY   ·   " + title;
        scriptVisible = visible;
        if (visible) subtitle.text = string.Empty;
    }

    public void SetRecording(bool on) => recording = on;

    public void SetScript(string witch, string student, string role, int lineNumber, int lineCount, ScriptState state)
    {
        witchLabel.text = "WITCH";
        witchLine.text = string.IsNullOrEmpty(witch) ? "" : "“" + witch + "”";
        studentLabel.text = role.ToUpperInvariant() + "  (you)   ·   line " + lineNumber + " of " + lineCount;
        studentLine.text = student ?? string.Empty;
        SetScriptState(state);
    }

    public void SetScriptState(ScriptState state)
    {
        scriptState = state;
        switch (state)
        {
            case ScriptState.WitchSpeaking:
                stateText.text = "The witch is speaking...";
                stateText.color = WitchTint;
                studentLine.color = new Color(1f, 1f, 1f, 0.55f);
                break;
            case ScriptState.YourTurn:
                stateText.text = "Your turn - read your line out loud!";
                studentLine.color = Color.white;
                break;
            case ScriptState.Listening:
                stateText.text = "Listening...";
                stateText.color = Accent;
                studentLine.color = Color.white;
                break;
            case ScriptState.Done:
                stateText.text = "";
                studentLine.color = new Color(1f, 1f, 1f, 0.55f);
                break;
        }
    }

    public void SetSubtitle(string text)
    {
        subtitle.text = text ?? string.Empty;
    }

    public void ShowVisitorText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        visitorText.text = "You: " + text;
        visitorText.enabled = true;
        visitorTextUntil = Time.time + 3.5f;
    }

    public void SetListening(bool on)
    {
        listeningText.enabled = on;
    }

    public void ShowCriteria(string title, ReadingFeedback.TakeSummary summary)
    {
        criteriaTitle.text = title;
        var levels = new[] { summary.Volume, summary.AccuracyLevel, summary.Expression, summary.Speed };
        for (var i = 0; i < 4; i++)
        {
            criteriaValues[i].text = ReadingFeedback.Label(levels[i]);
            criteriaChips[i].color = levels[i] == ReadingFeedback.Level.Super ? new Color(0.3f, 0.75f, 0.4f)
                                   : levels[i] == ReadingFeedback.Level.Good ? new Color(0.85f, 0.7f, 0.25f)
                                   : levels[i] == ReadingFeedback.Level.NeedsWork ? new Color(0.9f, 0.45f, 0.25f)
                                   : new Color(0.4f, 0.4f, 0.45f);
        }
        criteriaVisible = true;
    }

    public void HideCriteria() => criteriaVisible = false;

    public void SetPlayback(string label, float progress)
    {
        playbackVisible = !string.IsNullOrEmpty(label);
        if (!playbackVisible) return;
        playbackText.text = "▶  " + label;
        playbackFill.fillAmount = Mathf.Clamp01(progress);
    }

    // ------------------------------------------------------------------ construction

    private void Build()
    {
        var canvasGo = new GameObject("HexeStageCanvas");
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        root = canvasGo.GetComponent<RectTransform>();

        vignette = new GameObject("Vignette").AddComponent<RawImage>();
        Place(vignette.rectTransform, root, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        vignette.texture = BuildVignette(256);
        vignette.raycastTarget = false;

        // Banner + REC.
        bannerGroup = Group("Banner", root, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -60), new Vector2(760, 64));
        AddImage(bannerGroup.transform, Panel, Vector2.zero, Vector2.one);
        bannerText = AddText(bannerGroup.transform, "", 30, TextAnchor.MiddleCenter, Accent, Vector2.zero, Vector2.one, new Vector2(24, 0), new Vector2(-24, 0));
        bannerText.fontStyle = FontStyle.Bold;
        recDot = AddImage(root, Color.red, new Vector2(1f, 1f), new Vector2(1f, 1f));
        Size(recDot.rectTransform, new Vector2(-150, -60), new Vector2(26, 26));
        recDot.sprite = BuildCircle(64);
        recText = AddText(root, "REC", 30, TextAnchor.MiddleLeft, Color.white, new Vector2(1f, 1f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);
        Size(recText.rectTransform, new Vector2(-90, -60), new Vector2(90, 40));
        recText.fontStyle = FontStyle.Bold;

        // Script panel.
        scriptGroup = Group("Script", root, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 170), new Vector2(1240, 290));
        AddImage(scriptGroup.transform, Panel, Vector2.zero, Vector2.one);
        witchLabel = AddText(scriptGroup.transform, "WITCH", 22, TextAnchor.UpperLeft, WitchTint, new Vector2(0, 1), new Vector2(1, 1), new Vector2(36, -58), new Vector2(-36, -18));
        witchLabel.fontStyle = FontStyle.Bold;
        witchLine = AddText(scriptGroup.transform, "", 28, TextAnchor.UpperLeft, WitchTint, new Vector2(0, 1), new Vector2(1, 1), new Vector2(36, -106), new Vector2(-36, -52));
        witchLine.fontStyle = FontStyle.Italic;
        studentLabel = AddText(scriptGroup.transform, "", 22, TextAnchor.UpperLeft, Accent, new Vector2(0, 1), new Vector2(1, 1), new Vector2(36, -148), new Vector2(-36, -112));
        studentLabel.fontStyle = FontStyle.Bold;
        studentLine = AddText(scriptGroup.transform, "", 40, TextAnchor.UpperLeft, Color.white, new Vector2(0, 0), new Vector2(1, 1), new Vector2(36, 50), new Vector2(-36, -150));
        stateText = AddText(scriptGroup.transform, "", 24, TextAnchor.LowerRight, Accent, new Vector2(0, 0), new Vector2(1, 0), new Vector2(36, 12), new Vector2(-36, 50));

        // Coach subtitle and the visitor's recognised words.
        subtitle = AddText(root, "", 38, TextAnchor.LowerCenter, Color.white, new Vector2(0.1f, 0f), new Vector2(0.9f, 0f), new Vector2(0, 60), new Vector2(0, 220));
        subtitle.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.9f);
        subtitle.gameObject.GetComponent<Outline>().effectDistance = new Vector2(2, -2);
        visitorText = AddText(root, "", 28, TextAnchor.LowerCenter, new Color(0.75f, 0.9f, 1f), new Vector2(0.15f, 0f), new Vector2(0.85f, 0f), new Vector2(0, 20), new Vector2(0, 60));
        visitorText.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.9f);
        visitorText.enabled = false;
        listeningText = AddText(root, "●  Listening...", 26, TextAnchor.LowerLeft, Accent, new Vector2(0f, 0f), new Vector2(0f, 0f), Vector2.zero, Vector2.zero);
        Size(listeningText.rectTransform, new Vector2(170, 40), new Vector2(300, 40));
        listeningText.enabled = false;

        // Start prompt.
        startGroup = Group("Start", root, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0, 120), new Vector2(900, 70));
        startText = AddText(startGroup.transform, "", 34, TextAnchor.MiddleCenter, Color.white, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        startText.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.9f);

        // Criteria card.
        criteriaGroup = Group("Criteria", root, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-230, 80), new Vector2(400, 330));
        AddImage(criteriaGroup.transform, Panel, Vector2.zero, Vector2.one);
        criteriaTitle = AddText(criteriaGroup.transform, "", 26, TextAnchor.UpperLeft, Accent, new Vector2(0, 1), new Vector2(1, 1), new Vector2(28, -60), new Vector2(-28, -18));
        criteriaTitle.fontStyle = FontStyle.Bold;
        var names = new[] { "Volume", "Accuracy", "Expression", "Speed" };
        for (var i = 0; i < 4; i++)
        {
            var y = -90 - i * 56;
            AddText(criteriaGroup.transform, names[i], 26, TextAnchor.MiddleLeft, Color.white, new Vector2(0, 1), new Vector2(0, 1), new Vector2(28, y - 22), new Vector2(200, y + 22));
            criteriaChips[i] = AddImage(criteriaGroup.transform, Color.gray, new Vector2(1, 1), new Vector2(1, 1));
            Size(criteriaChips[i].rectTransform, new Vector2(-100, y), new Vector2(160, 40));
            criteriaValues[i] = AddText(criteriaChips[i].transform, "-", 22, TextAnchor.MiddleCenter, Color.white, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            criteriaValues[i].fontStyle = FontStyle.Bold;
        }

        // Playback bar.
        playbackGroup = Group("Playback", root, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0, -150), new Vector2(560, 90));
        AddImage(playbackGroup.transform, Panel, Vector2.zero, Vector2.one);
        playbackText = AddText(playbackGroup.transform, "", 30, TextAnchor.MiddleCenter, Color.white, new Vector2(0, 0.35f), new Vector2(1, 1), Vector2.zero, Vector2.zero);
        var track = AddImage(playbackGroup.transform, new Color(1f, 1f, 1f, 0.15f), new Vector2(0.06f, 0.18f), new Vector2(0.94f, 0.3f));
        playbackFill = AddImage(track.transform, Accent, Vector2.zero, Vector2.one);
        playbackFill.sprite = BuildCircle(4);
        playbackFill.type = Image.Type.Filled;
        playbackFill.fillMethod = Image.FillMethod.Horizontal;

        foreach (var g in new[] { scriptGroup, bannerGroup, criteriaGroup, startGroup, playbackGroup }) g.alpha = 0f;
    }

    private static void Fade(CanvasGroup group, bool visible, float speed)
    {
        group.alpha = Mathf.MoveTowards(group.alpha, visible ? 1f : 0f, Time.deltaTime * speed);
    }

    private CanvasGroup Group(string name, RectTransform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        Size(rt, pos, size);
        var g = go.AddComponent<CanvasGroup>();
        g.blocksRaycasts = false;
        g.interactable = false;
        return g;
    }

    private static void Size(RectTransform rt, Vector2 pos, Vector2 size)
    {
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    private static void Place(RectTransform rt, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
    }

    private Image AddImage(Transform parent, Color color, Vector2 anchorMin, Vector2 anchorMax)
    {
        var img = new GameObject("Image", typeof(RectTransform)).AddComponent<Image>();
        Place(img.rectTransform, parent, anchorMin, anchorMax, Vector2.zero, Vector2.zero);
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    private Text AddText(Transform parent, string text, int size, TextAnchor anchor, Color color, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        var t = new GameObject("Text", typeof(RectTransform)).AddComponent<Text>();
        Place(t.rectTransform, parent, anchorMin, anchorMax, offsetMin, offsetMax);
        t.font = font;
        t.fontSize = size;
        t.alignment = anchor;
        t.color = color;
        t.text = text;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    private static Texture2D BuildVignette(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size * 2f - 1f;
                var v = (y + 0.5f) / size * 2f - 1f;
                var d = Mathf.Sqrt(u * u * 0.8f + v * v);
                var a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1.35f, d));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    private static Sprite BuildCircle(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color32[size * size];
        var r = size / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(r - d) * 255));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }
}

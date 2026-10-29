using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

// Replays a take as a short film: the witch performs her lines again exactly as she did,
// and opposite her a second avatar plays Hansel with the visitor's voice and face. The
// visitor sees themselves in the scene - which is the moment they want to show someone.
//
// It is cut like a film rather than watched from the audience's seat:
//   - an establishing shot under the title, the scene, a pull-back under the credits
//   - a new shot on every line, cut a beat before the line starts, as editors do
//   - shot types rotate (over-the-shoulder, close-up, two-shot) and follow the mood: a
//     greedy witch is shot from below, a scared Hansel from above
//   - long witch lines cut to Hansel's reaction halfway - the visitor's real face, listening
//   - the camera stays on one side of the line between the two, so the cuts never confuse
//     who is looking at whom, and drifts slightly, as if handheld
//
// The film renders into its own 1080p texture (letterbox and titles included), shown
// full-screen while it plays and written to an MP4 by MovieExporter.
[DefaultExecutionOrder(1100)]   // after every layer that moves the avatars: the recording wins
public class CinematicReplay : MonoBehaviour
{
    [Header("References")]
    public SceneRecorder Recorder;
    public AvatarPerformer Performer;
    public StageEffects Stage;
    public HanselActor Hansel;
    [Tooltip("Clear flags, background and culling mask are copied from this camera.")]
    public Camera SourceCamera;

    [Header("Staging")]
    [Tooltip("Metres between the witch and Hansel.")]
    public float Distance = 1.25f;

    [Header("Film")]
    public int Width = 1920;
    public int Height = 1080;
    public int FrameRate = 30;
    public float LeadSeconds = 4.5f;
    public float TailSeconds = 5.5f;
    [Range(1.78f, 2.4f)] public float Aspect = 2.39f;
    public bool Subtitles = true;
    [Range(0f, 3f)] public float Handheld = 1f;

    [Header("Saving")]
    public bool SaveMovie = true;
    [Tooltip("Empty: the Windows Videos folder, in \"Hexe Role-Play\".")]
    public string OutputFolder = "";
    [Tooltip("Empty: found on PATH or where winget installs it.")]
    public string FfmpegPath = "";

    public bool IsPlaying { get; private set; }

    private enum ShotKind { Establishing, OverShoulder, CloseUp, TwoShot, LowAngle, HighAngle, Ending }

    private struct Shot
    {
        public float Start, End;
        public ShotKind Kind;
        public bool OnWitch;
        public float Shake;
    }

    private static readonly HashSet<string> MenacingMoods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WitchGreedy", "WitchSly", "WitchSuspicious" };
    private static readonly HashSet<string> FrightenedMoods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scared", "nervous" };

    private SceneRecorder.Movie movie;
    private readonly List<Shot> shots = new List<Shot>();
    private AudioSource source;
    private Camera cam;
    private RenderTexture filmTexture, captureTexture;
    private GameObject filmCanvas, displayCanvas;
    private CanvasGroup titleGroup, creditsGroup;
    private Image fade;
    private Text subtitle;
    private MovieExporter exporter;
    private float clock;
    private bool clockRunning;
    private int lastVisitorSegment = -1;

    private Animator witch;
    private SkinnedMeshRenderer witchFace;
    private Transform witchHead, hanselHead;
    private Font font;

    private void Awake()
    {
        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    private void OnDestroy()
    {
        exporter?.Abort();
        Camera.onPostRender -= OnCameraPostRender;
    }

    // Plays take `index` as a film and returns the saved MP4's path (null if not saved).
    public async UniTask<string> PlayAsync(int index, string title, string sceneTitle, string role, string witchName, CancellationToken token)
    {
        if (Recorder == null) return null;
        movie = Recorder.BuildMovie(index, LeadSeconds, TailSeconds);
        if (movie == null) return null;

        witch = Recorder.Witch;
        witchFace = Recorder.WitchFace;
        witchHead = witch != null ? witch.GetBoneTransform(HumanBodyBones.Head) : null;

        IsPlaying = true;
        clock = 0f;
        string saved = null;
        try
        {
            await StageAsync(token);
            BuildShots();
            BuildFilm($"Take {index + 1}", title, sceneTitle, role, witchName);
            StartExport(index);

            var clip = AudioClip.Create($"HexeFilm{index + 1}", movie.Audio.Length, 1, movie.Rate, false);
            clip.SetData(movie.Audio, 0);
            source.clip = clip;
            lastVisitorSegment = -1;
            clock = 0f;
            clockRunning = true;
            source.Play();
            Camera.onPostRender += OnCameraPostRender;

            while (clock < movie.Duration)
            {
                await UniTask.Yield(token);
            }

            Camera.onPostRender -= OnCameraPostRender;
            if (exporter != null) saved = await exporter.FinishAsync();
            if (saved != null) Debug.Log($"CinematicReplay: saved {saved}");
        }
        catch (OperationCanceledException)
        {
            exporter?.Abort();
            throw;
        }
        finally
        {
            Camera.onPostRender -= OnCameraPostRender;
            clockRunning = false;
            exporter = null;
            if (this != null) Teardown();
            IsPlaying = false;
        }
        return saved;
    }

    // ================================================================== staging

    private async UniTask StageAsync(CancellationToken token)
    {
        if (Performer != null) Performer.SetWitch(true);
        Stage?.SetSceneMode(true);
        if (Stage != null) await Stage.SetHatAsync(true, token);

        if (Hansel != null && witch != null)
        {
            var root = witch.transform;
            var facing = Vector3.ProjectOnPlane(root.forward, Vector3.up).normalized;
            Hansel.transform.SetPositionAndRotation(root.position + facing * Distance, Quaternion.LookRotation(-facing, Vector3.up));
            Hansel.SetVisible(true);
            hanselHead = Hansel.Avatar != null ? Hansel.Avatar.GetBoneTransform(HumanBodyBones.Head) : null;
        }
        // Let the actor's Animator settle into its stance before the first frame is filmed.
        await UniTask.DelayFrame(2, cancellationToken: token);
    }

    private void Teardown()
    {
        source.Stop();
        if (source.clip != null) Destroy(source.clip);
        source.clip = null;
        if (Hansel != null) Hansel.SetVisible(false);
        if (cam != null) Destroy(cam.gameObject);
        if (filmCanvas != null) Destroy(filmCanvas);
        if (displayCanvas != null) Destroy(displayCanvas);
        if (filmTexture != null) Destroy(filmTexture);
        if (captureTexture != null) Destroy(captureTexture);
        cam = null;
        movie = null;
    }

    private void StartExport(int index)
    {
        if (!SaveMovie) return;
        var ffmpeg = MovieExporter.FindFfmpeg(FfmpegPath);
        if (ffmpeg == null)
        {
            Debug.LogWarning("CinematicReplay: ffmpeg not found - the film plays but is not saved. Install it with: winget install Gyan.FFmpeg");
            return;
        }
        var folder = string.IsNullOrEmpty(OutputFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Hexe Role-Play")
            : OutputFolder;
        var path = Path.Combine(folder, $"HanselAndGretel_{DateTime.Now:yyyy-MM-dd_HHmmss}_take{index + 1}.mp4");
        exporter = new MovieExporter(Width, Height, FrameRate);
        if (!exporter.Start(ffmpeg, path, movie.Audio, movie.Rate)) exporter = null;
    }

    private void OnCameraPostRender(Camera rendered)
    {
        if (rendered != cam || exporter == null) return;
        Graphics.Blit(filmTexture, captureTexture);
        exporter.Capture(captureTexture, clock);
    }

    // ================================================================== per frame

    private void LateUpdate()
    {
        if (!IsPlaying || movie == null || !clockRunning) return;

        // The soundtrack is the clock; picture follows it. Without audio (no device, or past
        // its end) the clock runs on its own so the film still finishes.
        if (source.isPlaying) clock = source.timeSamples / (float)movie.Rate;
        else clock += Time.unscaledDeltaTime;

        var src = movie.SourceTime(clock);
        SceneRecorder.Sample(movie.Take, src, out var a, out var b, out var k);
        PoseWitch(a, b, k);
        PoseHansel(a, b, k);
        if (cam != null) PoseCamera();
        UpdateOverlays();
    }

    private void PoseWitch(SceneRecorder.Frame a, SceneRecorder.Frame b, float k)
    {
        if (a == null) return;
        var bones = Recorder.WitchBones;
        if (a.WitchBones != null && b.WitchBones != null && bones.Length == a.WitchBones.Length)
        {
            for (var i = 0; i < bones.Length; i++) bones[i].localRotation = Quaternion.Slerp(a.WitchBones[i], b.WitchBones[i], k);
            bones[0].localPosition = Vector3.Lerp(a.WitchHips, b.WitchHips, k);
        }
        if (witchFace != null && a.WitchFace != null && b.WitchFace != null)
        {
            var count = Mathf.Min(witchFace.sharedMesh.blendShapeCount, a.WitchFace.Length);
            for (var i = 0; i < count; i++) witchFace.SetBlendShapeWeight(i, Mathf.Lerp(a.WitchFace[i], b.WitchFace[i], k));
        }
    }

    private void PoseHansel(SceneRecorder.Frame a, SceneRecorder.Frame b, float k)
    {
        if (Hansel == null) return;
        var index = movie.SegmentAt(clock);
        var speaking = false;
        var voice = 0f;
        if (index >= 0)
        {
            var (seg, at) = movie.Placed[index];
            speaking = seg.Speaker == SceneRecorder.Speaker.Visitor && clock <= at + seg.Seconds;
            if (speaking)
            {
                voice = Level(clock);
                if (index != lastVisitorSegment)
                {
                    lastVisitorSegment = index;
                    Hansel.PlayGestureForMood(seg.Mood);
                }
            }
        }
        Hansel.Perform(a, b, k, speaking, voice);
    }

    // RMS of the soundtrack over the 50 ms around t.
    private float Level(float t)
    {
        var center = Mathf.RoundToInt(t * movie.Rate);
        var half = movie.Rate / 40;
        var from = Mathf.Max(0, center - half);
        var to = Mathf.Min(movie.Audio.Length, center + half);
        if (to <= from) return 0f;
        var sum = 0f;
        for (var i = from; i < to; i++) sum += movie.Audio[i] * movie.Audio[i];
        return Mathf.Sqrt(sum / (to - from));
    }

    // ================================================================== shots

    private void BuildShots()
    {
        shots.Clear();
        const float cutEarly = 0.15f;
        var placed = movie.Placed;
        var firstCut = Mathf.Max(0.5f, placed[0].At - cutEarly);
        shots.Add(new Shot { Start = 0f, End = firstCut, Kind = ShotKind.Establishing, Shake = 0.5f });

        var cycle = new[] { ShotKind.TwoShot, ShotKind.OverShoulder, ShotKind.CloseUp, ShotKind.OverShoulder, ShotKind.CloseUp };
        var turn = 0;
        var last = ShotKind.Establishing;
        for (var i = 0; i < placed.Count; i++)
        {
            var (seg, at) = placed[i];
            var start = i == 0 ? firstCut : at - cutEarly;
            var end = i + 1 < placed.Count ? placed[i + 1].At - cutEarly : at + seg.Seconds + 0.6f;
            var onWitch = seg.Speaker == SceneRecorder.Speaker.Witch;
            var shake = !onWitch && FrightenedMoods.Contains(seg.Mood ?? "") ? 1.8f : 1f;

            ShotKind kind;
            if (onWitch && MenacingMoods.Contains(seg.Mood ?? "") && last != ShotKind.LowAngle && turn % 2 == 0) kind = ShotKind.LowAngle;
            else if (!onWitch && shake > 1f && last != ShotKind.HighAngle && turn % 3 == 1) kind = ShotKind.HighAngle;
            else
            {
                kind = cycle[turn % cycle.Length];
                if (kind == last) kind = cycle[(turn + 1) % cycle.Length];
            }
            turn++;

            if (end - start > 3.6f)
            {
                // Long line: cut away halfway - to Hansel listening if the witch talks, to a
                // second angle on Hansel if he does.
                var mid = start + (end - start) * 0.55f;
                shots.Add(new Shot { Start = start, End = mid, Kind = kind, OnWitch = onWitch, Shake = shake });
                var second = kind == ShotKind.CloseUp ? ShotKind.OverShoulder : ShotKind.CloseUp;
                shots.Add(new Shot { Start = mid, End = end, Kind = second, OnWitch = false, Shake = shake });
                last = second;
            }
            else
            {
                shots.Add(new Shot { Start = start, End = end, Kind = kind, OnWitch = onWitch, Shake = shake });
                last = kind;
            }
        }
        shots.Add(new Shot { Start = shots[shots.Count - 1].End, End = movie.Duration + 1f, Kind = ShotKind.Ending, Shake = 0.5f });
    }

    private void PoseCamera()
    {
        var shot = shots[shots.Count - 1];
        foreach (var s in shots)
        {
            if (clock >= s.Start && clock < s.End) { shot = s; break; }
        }
        var u = Mathf.Clamp01((clock - shot.Start) / Mathf.Max(0.01f, shot.End - shot.Start));
        u = u * u * (3f - 2f * u);

        var w = witchHead != null ? witchHead.position : (witch != null ? witch.transform.position + Vector3.up * 1.4f : Vector3.up * 1.4f);
        var h = hanselHead != null ? hanselHead.position : w + Vector3.forward * Distance;
        var axis = Vector3.ProjectOnPlane(h - w, Vector3.up).normalized;
        if (axis.sqrMagnitude < 1e-4f) axis = Vector3.forward;
        var side = Vector3.Cross(Vector3.up, axis);   // the camera stays on this side of the line
        var up = Vector3.up;
        var mid = (w + h) * 0.5f;

        var speaker = shot.OnWitch ? w : h;
        var listener = shot.OnWitch ? h : w;
        var facing = Vector3.ProjectOnPlane(listener - speaker, up).normalized;
        var leadRoom = facing * 0.1f;   // space in front of a face, in the direction it looks

        Vector3 from, to, look;
        float fovFrom, fovTo, roll = 0f;
        switch (shot.Kind)
        {
            case ShotKind.Establishing:
                from = mid + side * 3.4f - axis * 0.9f + up * 1.3f;
                to = mid + side * 2.6f - axis * 0.4f + up * 0.5f;
                look = mid - up * 0.25f;
                fovFrom = 40f; fovTo = 34f;
                break;
            case ShotKind.OverShoulder:
                from = listener - facing * 0.55f + side * 0.32f + up * 0.07f;
                to = listener - facing * 0.47f + side * 0.3f + up * 0.06f;
                look = speaker + leadRoom * 0.5f - up * 0.03f;
                fovFrom = fovTo = 30f;
                break;
            case ShotKind.CloseUp:
                from = speaker + facing * 0.9f + side * 0.32f + up * 0.03f;
                to = speaker + facing * 0.76f + side * 0.27f + up * 0.03f;
                look = speaker + leadRoom - up * 0.02f;
                fovFrom = fovTo = 24f;
                break;
            case ShotKind.TwoShot:
                from = mid + side * 2.0f - facing * 0.12f + up * 0.02f;
                to = mid + side * 1.85f + facing * 0.12f + up * 0.02f;
                look = mid - up * 0.15f;
                fovFrom = 36f; fovTo = 34f;
                break;
            case ShotKind.LowAngle:
                from = speaker + facing * 1.0f + side * 0.35f - up * 0.55f;
                to = speaker + facing * 0.85f + side * 0.3f - up * 0.5f;
                look = speaker + leadRoom - up * 0.05f;
                fovFrom = fovTo = 32f;
                roll = 5f;
                break;
            case ShotKind.HighAngle:
                from = speaker + facing * 1.0f + side * 0.3f + up * 0.5f;
                to = speaker + facing * 0.88f + side * 0.27f + up * 0.42f;
                look = speaker + leadRoom - up * 0.08f;
                fovFrom = fovTo = 32f;
                break;
            default:   // Ending: pull back and up, away from the house
                from = mid + side * 1.9f + up * 0.05f;
                to = mid + side * 3.8f - axis * 0.6f + up * 1.7f;
                look = mid - up * 0.3f;
                fovFrom = 34f; fovTo = 42f;
                break;
        }

        var position = Vector3.Lerp(from, to, u);
        var t = Time.time * 0.55f;
        var shake = Handheld * shot.Shake;
        var drift = Quaternion.Euler((Mathf.PerlinNoise(t, 0.3f) - 0.5f) * 1.2f * shake, (Mathf.PerlinNoise(t, 5.1f) - 0.5f) * 1.6f * shake, roll + (Mathf.PerlinNoise(t, 9.7f) - 0.5f) * 0.6f * shake);
        cam.transform.SetPositionAndRotation(position, Quaternion.LookRotation(look - position, up) * drift);
        cam.fieldOfView = Mathf.Lerp(fovFrom, fovTo, u);
    }

    // ================================================================== picture and titles

    private void BuildFilm(string takeLabel, string title, string sceneTitle, string role, string witchName)
    {
        filmTexture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4, name = "HexeFilm" };
        captureTexture = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32) { name = "HexeFilmCapture" };

        var camGo = new GameObject("HexeFilmCamera");
        cam = camGo.AddComponent<Camera>();
        if (SourceCamera != null)
        {
            cam.clearFlags = SourceCamera.clearFlags;
            cam.backgroundColor = SourceCamera.backgroundColor;
            cam.cullingMask = SourceCamera.cullingMask;
        }
        cam.nearClipPlane = 0.03f;
        cam.farClipPlane = 200f;
        cam.targetTexture = filmTexture;
        cam.depth = -10;
        PoseCamera();

        // Overlays rendered by the film camera itself, so they are part of the picture.
        filmCanvas = new GameObject("HexeFilmCanvas");
        var canvas = filmCanvas.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = cam;
        canvas.planeDistance = 0.1f;
        var scaler = filmCanvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(Width, Height);
        var root = filmCanvas.GetComponent<RectTransform>();

        var vignette = new GameObject("Vignette").AddComponent<RawImage>();
        Stretch(vignette.rectTransform, root);
        vignette.texture = BuildVignette(256);
        vignette.raycastTarget = false;

        var bar = Mathf.Clamp01((1f - (Width / (float)Height) / Aspect) * 0.5f);
        var top = Panel(root, Color.black);
        Anchor(top.rectTransform, new Vector2(0f, 1f - bar), Vector2.one);
        var bottom = Panel(root, Color.black);
        Anchor(bottom.rectTransform, Vector2.zero, new Vector2(1f, bar));

        subtitle = Label(bottom.transform, "", 34, FontStyle.Italic, new Color(1f, 0.95f, 0.85f));
        Stretch(subtitle.rectTransform, bottom.rectTransform);
        subtitle.alignment = TextAnchor.MiddleCenter;

        titleGroup = Group(root, "Title");
        var t1 = Label(titleGroup.transform, title.ToUpperInvariant(), 120, FontStyle.Bold, new Color(1f, 0.86f, 0.55f));
        Anchor(t1.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.75f));
        t1.gameObject.AddComponent<Shadow>().effectDistance = new Vector2(4, -4);
        var t2 = Label(titleGroup.transform, sceneTitle, 44, FontStyle.Italic, Color.white);
        Anchor(t2.rectTransform, new Vector2(0f, 0.38f), new Vector2(1f, 0.5f));
        t2.gameObject.AddComponent<Shadow>().effectDistance = new Vector2(3, -3);
        var t3 = Label(titleGroup.transform, takeLabel.ToUpperInvariant(), 28, FontStyle.Normal, new Color(1f, 1f, 1f, 0.75f));
        Anchor(t3.rectTransform, new Vector2(0f, 0.3f), new Vector2(1f, 0.38f));

        creditsGroup = Group(root, "Credits");
        var c1 = Label(creditsGroup.transform, "starring", 34, FontStyle.Italic, new Color(1f, 1f, 1f, 0.8f));
        Anchor(c1.rectTransform, new Vector2(0f, 0.62f), new Vector2(1f, 0.7f));
        var c2 = Label(creditsGroup.transform, $"YOU  as  {role.ToUpperInvariant()}", 84, FontStyle.Bold, new Color(1f, 0.86f, 0.55f));
        Anchor(c2.rectTransform, new Vector2(0f, 0.48f), new Vector2(1f, 0.62f));
        var c3 = Label(creditsGroup.transform, $"and {witchName} as the Witch", 40, FontStyle.Normal, Color.white);
        Anchor(c3.rectTransform, new Vector2(0f, 0.38f), new Vector2(1f, 0.47f));
        var c4 = Label(creditsGroup.transform, DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB")), 26, FontStyle.Normal, new Color(1f, 1f, 1f, 0.6f));
        Anchor(c4.rectTransform, new Vector2(0f, 0.3f), new Vector2(1f, 0.36f));

        fade = Panel(root, Color.black);
        Stretch(fade.rectTransform, root);

        // The film shown full-screen to the visitor, over the stage UI.
        displayCanvas = new GameObject("HexeFilmDisplay");
        var display = displayCanvas.AddComponent<Canvas>();
        display.renderMode = RenderMode.ScreenSpaceOverlay;
        display.sortingOrder = 200;
        var displayRoot = displayCanvas.GetComponent<RectTransform>();
        Stretch(Panel(displayRoot, Color.black).rectTransform, displayRoot);
        var screen = new GameObject("Film").AddComponent<RawImage>();
        screen.texture = filmTexture;
        screen.raycastTarget = false;
        Stretch(screen.rectTransform, displayRoot);
        var fitter = screen.gameObject.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fitter.aspectRatio = Width / (float)Height;
    }

    private void UpdateOverlays()
    {
        var d = movie.Duration;
        var lead = LeadSeconds;
        var tailStart = d - TailSeconds;

        var fadeIn = 1f - Smooth(clock / 1.0f);
        var fadeOut = Smooth((clock - tailStart - 0.4f) / 1.2f) * 0.8f + Smooth((clock - (d - 0.7f)) / 0.7f) * 0.2f;
        fade.color = new Color(0f, 0f, 0f, Mathf.Max(fadeIn, fadeOut));

        titleGroup.alpha = Smooth((clock - 0.6f) / 0.8f) * (1f - Smooth((clock - (lead - 1.2f)) / 0.7f));
        creditsGroup.alpha = Smooth((clock - (tailStart + 1.0f)) / 0.8f) * (1f - Smooth((clock - (d - 0.5f)) / 0.5f));

        var text = "";
        if (Subtitles)
        {
            var index = movie.SegmentAt(clock);
            if (index >= 0)
            {
                var (seg, at) = movie.Placed[index];
                if (clock <= at + seg.Seconds + 0.3f && !string.IsNullOrEmpty(seg.Text)) text = seg.Text;
            }
        }
        subtitle.text = text;
    }

    private static float Smooth(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    private CanvasGroup Group(RectTransform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        Stretch(go.GetComponent<RectTransform>(), parent);
        var group = go.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        return group;
    }

    private Text Label(Transform parent, string text, int size, FontStyle style, Color color)
    {
        var t = new GameObject("Text").AddComponent<Text>();
        t.transform.SetParent(parent, false);
        t.font = font;
        t.text = text;
        t.fontSize = size;
        t.fontStyle = style;
        t.color = color;
        t.alignment = TextAnchor.MiddleCenter;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    private static Image Panel(RectTransform parent, Color color)
    {
        var image = new GameObject("Panel").AddComponent<Image>();
        image.transform.SetParent(parent, false);
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static void Stretch(RectTransform rect, RectTransform parent)
    {
        rect.SetParent(parent, false);
        Anchor(rect, Vector2.zero, Vector2.one);
    }

    private static void Anchor(RectTransform rect, Vector2 min, Vector2 max)
    {
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static Texture2D BuildVignette(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size * 2f - 1f;
                var dy = (y + 0.5f) / size * 2f - 1f;
                var a = Mathf.Clamp01((Mathf.Sqrt(dx * dx + dy * dy) - 0.55f) / 0.85f);
                pixels[y * size + x] = new Color32(0, 0, 0, (byte)(a * a * 200));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        return tex;
    }
}

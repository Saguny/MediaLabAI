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
//
// Scripted film: when the scenario has a "film" block (and its lines "film" beats), the take is
// staged on a set instead of the two actors simply facing each other - Hansel in a cage, an
// oven beside it, the bone Gretel slips him (FilmSet). The beats pick the shot for each line,
// cut away to inserts, move the bone and light the oven, and put the witch nearer the cage
// for a line; between shots she moves only on a cut, as films hide travel. What the actors say
// and do is still the take: her recorded body, face and voice, the visitor's own face and voice.
// Only where they stand, the props and two reaches (his hand out with the bone, hers feeling
// for it) are scripted. Prop state comes from the latest line reached, so a skipped line or a
// take cut short still leaves everything where the story has it.
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
    [Tooltip("Scripted film: where the oven stands, from the middle between the two - metres to the film camera's side (negative: the far side), up, and along the line from the witch to Hansel (negative: behind her).")]
    public Vector3 OvenPlace = new Vector3(-2.0f, 0f, -0.9f);
    [Tooltip("Scripted film: metres between the witch and Hansel unless a line says otherwise. Further than Distance, so the close shots on her stay outside the cage.")]
    public float ScriptedDistance = 1.75f;

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

    private enum ShotKind { Establishing, OverShoulder, CloseUp, TwoShot, LowAngle, HighAngle, Ending, Insert, Oven }

    private struct Shot
    {
        public float Start, End;
        public ShotKind Kind;
        public bool OnWitch;
        public float Shake;
        public float WitchDistance;   // scripted film: metres between her and Hansel in this shot; 0: Distance
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

    private Animator witch;
    private SkinnedMeshRenderer witchFace;
    private Transform witchHead, hanselHead;
    private Font font;

    // Scripted film.
    private HexeScenario scenario;
    private bool scripted;
    private FilmSet set;
    private Vector3 witchHome, hanselSpot, filmAxis;
    private Quaternion witchHomeRotation;
    private bool witchMoved;
    private readonly SortedDictionary<int, (float Start, float End)> lineSpans = new SortedDictionary<int, (float Start, float End)>();
    private Transform hanselLeftUpper, hanselLeftLower, hanselLeftHand, hanselLeftMiddle;
    private Transform hanselRightUpper, hanselRightLower, hanselRightHand;
    private Transform witchLeftUpper, witchLeftLower, witchLeftHand;

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
    // scenario: optional; with a "film" block, the take is played as a scripted film.
    public async UniTask<string> PlayAsync(int index, string title, string sceneTitle, string role, string witchName, CancellationToken token, HexeScenario scenario = null)
    {
        if (Recorder == null) return null;
        movie = Recorder.BuildMovie(index, LeadSeconds, TailSeconds);
        if (movie == null) return null;

        this.scenario = scenario;
        witch = Recorder.Witch;
        witchFace = Recorder.WitchFace;
        witchHead = witch != null ? witch.GetBoneTransform(HumanBodyBones.Head) : null;

        IsPlaying = true;
        clock = 0f;
        string saved = null;
        try
        {
            await StageAsync(token);
            if (scripted) BuildScriptedShots();
            else BuildShots();
            BuildFilm($"Take {index + 1}", title, sceneTitle, role, witchName);
            StartExport(index);

            var clip = AudioClip.Create($"HexeFilm{index + 1}", movie.Audio.Length, 1, movie.Rate, false);
            clip.SetData(movie.Audio, 0);
            source.clip = clip;
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
        // The film poses her from the take and moves her between cuts; Layable must not.
        if (Performer != null && Performer.Body != null) Performer.Body.Suspended = true;
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

        scripted = CanScript();
        if (scripted) BuildSet();
    }

    // The film is scripted only when every line of the take knows which scene line it is, the
    // scenario asks for a set this film knows, and both actors are there.
    private bool CanScript()
    {
        if (scenario?.film == null || !string.Equals(scenario.film.set, "cage", StringComparison.OrdinalIgnoreCase)) return false;
        if (Hansel == null || Hansel.Avatar == null || witch == null || movie.Placed.Count == 0) return false;
        foreach (var (seg, _) in movie.Placed)
        {
            if (seg.Line < 0 || seg.Line >= scenario.scene.Count) return false;
        }
        return true;
    }

    private void BuildSet()
    {
        witchHome = witch.transform.position;
        witchHomeRotation = witch.transform.rotation;
        witchMoved = true;
        hanselSpot = Hansel.transform.position;
        filmAxis = Vector3.ProjectOnPlane(hanselSpot - witchHome, Vector3.up).normalized;
        if (filmAxis.sqrMagnitude < 1e-4f) filmAxis = Vector3.ProjectOnPlane(witch.transform.forward, Vector3.up).normalized;

        var avatar = Hansel.Avatar;
        hanselLeftUpper = avatar.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        hanselLeftLower = avatar.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        hanselLeftHand = avatar.GetBoneTransform(HumanBodyBones.LeftHand);
        hanselLeftMiddle = avatar.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
        hanselRightUpper = avatar.GetBoneTransform(HumanBodyBones.RightUpperArm);
        hanselRightLower = avatar.GetBoneTransform(HumanBodyBones.RightLowerArm);
        hanselRightHand = avatar.GetBoneTransform(HumanBodyBones.RightHand);
        witchLeftUpper = witch.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        witchLeftLower = witch.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        witchLeftHand = witch.GetBoneTransform(HumanBodyBones.LeftHand);

        var headHeight = hanselHead != null ? hanselHead.position.y - hanselSpot.y : 1.45f;
        set = FilmSet.Build(hanselSpot, filmAxis, ScriptedDistance, OvenPlace, witch.gameObject.layer, headHeight);

        // Where each scene line sits on the film's clock (a witch line may be several clips).
        lineSpans.Clear();
        foreach (var (seg, at) in movie.Placed)
        {
            var end = at + seg.Seconds;
            lineSpans[seg.Line] = lineSpans.TryGetValue(seg.Line, out var span) ? (Mathf.Min(span.Start, at), Mathf.Max(span.End, end)) : (at, end);
        }
    }

    private void Teardown()
    {
        source.Stop();
        if (source.clip != null) Destroy(source.clip);
        source.clip = null;
        if (Hansel != null) Hansel.SetVisible(false);
        set?.Destroy();
        set = null;
        if (witchMoved && witch != null) witch.transform.SetPositionAndRotation(witchHome, witchHomeRotation);
        witchMoved = false;
        // Back home, Layable takes her again with a fresh body where she stands.
        if (Performer != null && Performer.Body != null) Performer.Body.Suspended = false;
        scripted = false;
        lineSpans.Clear();
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
        if (scripted) PlaceWitch();
        PoseWitch(a, b, k);
        PoseHansel(a, b, k);
        if (scripted) StageProps();
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
            if (speaking) voice = Level(clock);
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

    // ================================================================== scripted film

    // One shot per scene line, as the line's film beat asks (or as the plain film would pick),
    // with an optional cutaway part-way through the line.
    private void BuildScriptedShots()
    {
        shots.Clear();
        const float cutEarly = 0.15f;
        var lines = new List<(int Line, float Start, float End)>();
        foreach (var kv in lineSpans) lines.Add((kv.Key, kv.Value.Start, kv.Value.End));
        lines.Sort((x, y) => x.Start.CompareTo(y.Start));

        var firstCut = Mathf.Max(0.5f, lines[0].Start - cutEarly);
        shots.Add(new Shot { Start = 0f, End = firstCut, Kind = ShotKind.Establishing, Shake = 0.5f, WitchDistance = WitchDistanceOn(lines[0].Line) });

        var cycle = new[] { ShotKind.TwoShot, ShotKind.OverShoulder, ShotKind.CloseUp, ShotKind.OverShoulder, ShotKind.CloseUp };
        var turn = 0;
        var last = ShotKind.Establishing;
        for (var i = 0; i < lines.Count; i++)
        {
            var (index, lineStart, lineEnd) = lines[i];
            var line = scenario.scene[index];
            var beat = line.film;
            var start = i == 0 ? firstCut : lineStart - cutEarly;
            var end = i + 1 < lines.Count ? lines[i + 1].Start - cutEarly : lineEnd + 0.6f;
            var distance = WitchDistanceOn(index);
            var onWitch = OnWitch(beat?.on, !line.IsStudent);
            var shake = line.IsStudent && FrightenedMoods.Contains(line.emotion ?? "") ? 1.8f : 1f;

            if (!TryShot(beat?.shot, out var kind))
            {
                if (onWitch && MenacingMoods.Contains(line.face ?? "") && last != ShotKind.LowAngle && turn % 2 == 0) kind = ShotKind.LowAngle;
                else if (!onWitch && shake > 1f && last != ShotKind.HighAngle && turn % 3 == 1) kind = ShotKind.HighAngle;
                else
                {
                    kind = cycle[turn % cycle.Length];
                    if (kind == last) kind = cycle[(turn + 1) % cycle.Length];
                }
            }
            turn++;

            var cut = -1f;
            var second = ShotKind.CloseUp;
            var secondOnWitch = false;
            if (beat != null && TryShot(beat.cutaway, out second))
            {
                cut = lineStart + Mathf.Clamp01(beat.cutawayAt) * (lineEnd - lineStart);
                secondOnWitch = OnWitch(beat.cutawayOn, onWitch);
            }
            else if (string.IsNullOrEmpty(beat?.shot) && end - start > 3.6f)
            {
                // As in the plain film: a long line cuts away halfway, to Hansel.
                cut = start + (end - start) * 0.55f;
                second = kind == ShotKind.CloseUp ? ShotKind.OverShoulder : ShotKind.CloseUp;
            }

            if (cut > start + 0.5f && cut < end - 0.5f)
            {
                shots.Add(new Shot { Start = start, End = cut, Kind = kind, OnWitch = onWitch, Shake = shake, WitchDistance = distance });
                shots.Add(new Shot { Start = cut, End = end, Kind = second, OnWitch = secondOnWitch, Shake = shake, WitchDistance = distance });
                last = second;
            }
            else
            {
                shots.Add(new Shot { Start = start, End = end, Kind = kind, OnWitch = onWitch, Shake = shake, WitchDistance = distance });
                last = kind;
            }
        }
        shots.Add(new Shot { Start = shots[shots.Count - 1].End, End = movie.Duration + 1f, Kind = ShotKind.Ending, Shake = 0.5f, WitchDistance = WitchDistanceOn(lines[lines.Count - 1].Line) });
    }

    private static bool TryShot(string name, out ShotKind kind)
    {
        kind = ShotKind.CloseUp;
        if (string.IsNullOrEmpty(name)) return false;
        if (!Enum.TryParse(name, true, out kind)) return false;
        return kind != ShotKind.Establishing && kind != ShotKind.Ending;
    }

    private static bool OnWitch(string on, bool fallback)
    {
        if (string.Equals(on, "witch", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(on, "student", StringComparison.OrdinalIgnoreCase) || string.Equals(on, "hansel", StringComparison.OrdinalIgnoreCase)) return false;
        return fallback;
    }

    // Never inside the cage, nor so far off that she cannot see him.
    private float WitchDistanceOn(int line)
    {
        var d = line >= 0 && line < scenario.scene.Count ? scenario.scene[line].film?.witchDistance ?? 0f : 0f;
        return Mathf.Clamp(d > 0f ? d : ScriptedDistance, FilmSet.Front + 0.3f, 3f);
    }

    // She moves only on a cut: each shot has her where its line wants her.
    private void PlaceWitch()
    {
        var d = CurrentShot().WitchDistance;
        if (d <= 0f) d = ScriptedDistance;
        witch.transform.SetPositionAndRotation(witchHome + filmAxis * (Distance - d), witchHomeRotation);
    }

    private struct Track
    {
        public string Value, Previous;
        public float Since;   // film time of the last change; -infinity: settled before the film shows it
    }

    // Where something stands at the current film time: the last change made by a line already
    // reached, at its moment in that line. A line the take never reached changes nothing; a
    // line it skipped on the way counts as done.
    private Track Follow(Func<HexeScenario.FilmBeat, string> value, Func<HexeScenario.FilmBeat, float> at, string initial)
    {
        var track = new Track { Value = initial, Previous = initial, Since = float.NegativeInfinity };
        var reached = LineReached();
        for (var i = 0; i <= reached && i < scenario.scene.Count; i++)
        {
            var beat = scenario.scene[i].film;
            var v = beat != null ? value(beat) : null;
            if (string.IsNullOrEmpty(v)) continue;
            var when = lineSpans.TryGetValue(i, out var span) ? span.Start + Mathf.Clamp01(at(beat)) * (span.End - span.Start) : float.NegativeInfinity;
            if (clock < when) continue;
            track.Previous = track.Value;
            track.Value = v.ToLowerInvariant();
            track.Since = when;
        }
        return track;
    }

    private int LineReached()
    {
        var reached = -1;
        foreach (var kv in lineSpans)
        {
            if (clock >= kv.Value.Start && kv.Key > reached) reached = kv.Key;
        }
        return reached;
    }

    private float Ramp(float since, float seconds) => float.IsNegativeInfinity(since) ? 1f : Smooth((clock - since) / seconds);

    private void StageProps()
    {
        if (set == null) return;
        var bone = Follow(b => b.bone, b => b.boneAt, "hidden");
        var oven = Follow(b => b.oven, b => b.ovenAt, "embers");
        var grip = Follow(b => b.grip.HasValue ? (b.grip.Value ? "on" : "off") : null, b => 0f, "off");

        // His left hand goes out through the hatch with the bone, and back in.
        var reach = bone.Value == "out" ? Ramp(bone.Since, 0.7f) : bone.Previous == "out" ? 1f - Ramp(bone.Since, 0.6f) : 0f;
        if (reach > 0f && hanselLeftUpper != null && hanselLeftLower != null)
        {
            var shoulder = set.Local(hanselLeftUpper.position).y;
            var palm = set.World((FilmSet.HatchFrom + FilmSet.HatchTo) * 0.5f, shoulder - 0.12f, -(FilmSet.Front + 0.13f));
            TwoBoneIK.Solve(hanselLeftUpper, hanselLeftLower, hanselLeftHand, palm + filmAxis * 0.06f,
                hanselLeftLower.position + Vector3.down * 0.2f + set.Root.right * 0.1f, reach);
        }

        // His right hand holds the door post, just inside the bars.
        var hold = grip.Value == "on" ? Ramp(grip.Since, 0.5f) : grip.Previous == "on" ? 1f - Ramp(grip.Since, 0.5f) : 0f;
        if (hold > 0f && hanselRightUpper != null && hanselRightLower != null)
        {
            var shoulder = set.Local(hanselRightUpper.position).y;
            var palm = set.World(FilmSet.HatchFrom - 0.025f, shoulder - 0.05f, -(FilmSet.Front - 0.05f));
            TwoBoneIK.Solve(hanselRightUpper, hanselRightLower, hanselRightHand, palm + filmAxis * 0.05f,
                hanselRightLower.position + Vector3.down * 0.2f - set.Root.right * 0.15f, hold);
        }

        PlaceBone(bone);

        // She feels for his "finger" on a line that asks for it - only while there is one to feel.
        var line = LineReached();
        var beat = line >= 0 && line < scenario.scene.Count ? scenario.scene[line].film : null;
        if (beat != null && beat.witchTouch && bone.Value == "out" && set.Bone.gameObject.activeSelf && witchLeftUpper != null && witchLeftLower != null
            && lineSpans.TryGetValue(line, out var span))
        {
            var u = Mathf.Clamp01((clock - span.Start) / Mathf.Max(0.1f, span.End - span.Start));
            var feel = Smooth(u / 0.25f) * (1f - Smooth((u - 0.8f) / 0.2f));
            var tip = set.Bone.position + set.Bone.forward * (FilmSet.BoneLength * 0.5f);
            TwoBoneIK.Solve(witchLeftUpper, witchLeftLower, witchLeftHand, tip - filmAxis * 0.07f, witchLeftLower.position + Vector3.down * 0.2f, feel);
        }

        var level = oven.Value == "fire" ? Mathf.Lerp(0.15f, 1f, Ramp(oven.Since, 0.8f)) : 0.15f;
        set.SetOven(level, Time.time);
    }

    private void PlaceBone(Track bone)
    {
        var b = set.Bone;
        if (bone.Value == "hidden" || hanselLeftHand == null)
        {
            b.gameObject.SetActive(false);
            return;
        }
        b.gameObject.SetActive(true);

        var forearm = hanselLeftLower != null ? (hanselLeftHand.position - hanselLeftLower.position).normalized : Vector3.down;
        var palm = hanselLeftMiddle != null ? Vector3.Lerp(hanselLeftHand.position, hanselLeftMiddle.position, 0.6f) : hanselLeftHand.position + forearm * 0.05f;
        var inHand = palm + forearm * 0.03f;
        var inHandRotation = Quaternion.LookRotation(forearm, Mathf.Abs(Vector3.Dot(forearm, Vector3.up)) > 0.9f ? filmAxis : Vector3.up);

        if (bone.Value == "slide" && !float.IsNegativeInfinity(bone.Since))
        {
            // Gretel, out of the picture, pushes it in between the side bars, into his hand.
            var p = Smooth((clock - bone.Since) / 1.4f);
            var from = set.World(0.95f, set.Local(palm).y, 0.3f);
            var path = inHand - from;
            var along = path.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(path.normalized, Vector3.up) : inHandRotation;
            b.SetPositionAndRotation(Vector3.Lerp(from, inHand, p), Quaternion.Slerp(along, inHandRotation, Smooth((p - 0.7f) / 0.3f)));
            return;
        }
        b.SetPositionAndRotation(inHand, inHandRotation);
    }

    // What an insert shot looks at: the bone (and her hand, while she feels it), or his hand.
    private Vector3 InsertTarget()
    {
        if (set != null && set.Bone.gameObject.activeSelf)
        {
            var target = set.Bone.position;
            if (witchLeftHand != null && Vector3.Distance(witchLeftHand.position, target) < 0.35f) target = Vector3.Lerp(target, witchLeftHand.position, 0.35f);
            return target;
        }
        if (hanselLeftHand != null) return hanselLeftHand.position;
        return hanselHead != null ? hanselHead.position - Vector3.up * 0.5f : Vector3.zero;
    }

    // A camera that would stand inside the cage (a close shot on her, from his side) steps out
    // through the front bars instead.
    private Vector3 OutOfCage(Vector3 position)
    {
        var local = set.Local(position);
        const float margin = 0.08f;
        if (Mathf.Abs(local.x) < FilmSet.HalfWidth + margin && local.z > -FilmSet.Front - margin && local.z < FilmSet.Back + margin && local.y < set.Height + 0.15f)
        {
            local.z = -FilmSet.Front - margin;
            return set.Root.TransformPoint(local);
        }
        return position;
    }

    private Shot CurrentShot()
    {
        var shot = shots[shots.Count - 1];
        foreach (var s in shots)
        {
            if (clock >= s.Start && clock < s.End) { shot = s; break; }
        }
        return shot;
    }

    private void PoseCamera()
    {
        var shot = CurrentShot();
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
                // Only a little below her eyes: any lower and the shot looks up under the hat's
                // brim, and her hands fill the frame from beneath.
                from = speaker + facing * 1.2f + side * 0.38f - up * 0.1f;
                to = speaker + facing * 1.05f + side * 0.34f - up * 0.08f;
                look = speaker + leadRoom - up * 0.05f;
                fovFrom = fovTo = 32f;
                roll = 3f;
                break;
            case ShotKind.HighAngle:
                from = speaker + facing * 1.0f + side * 0.3f + up * 0.5f;
                to = speaker + facing * 0.88f + side * 0.27f + up * 0.42f;
                look = speaker + leadRoom - up * 0.08f;
                fovFrom = fovTo = 32f;
                break;
            case ShotKind.Insert:
                {
                    // Close on the bone (or his hand, while there is none), from the camera's side.
                    var target = InsertTarget();
                    var right = set != null ? set.Root.right : side;
                    from = target + right * 0.55f + up * 0.16f - axis * 0.12f;
                    to = target + right * 0.47f + up * 0.13f - axis * 0.1f;
                    look = target;
                    fovFrom = 30f; fovTo = 27f;
                }
                break;
            case ShotKind.Oven:
                {
                    var mouth = set != null ? set.OvenMouth : mid - side * 2f;
                    var f = set != null ? set.OvenFacing : side;
                    from = mouth + f * 1.35f + up * 0.28f;
                    to = mouth + f * 1.05f + up * 0.18f;
                    look = mouth + up * 0.03f;
                    fovFrom = 34f; fovTo = 29f;
                }
                break;
            default:   // Ending: pull back and up, away from the house
                from = mid + side * 1.9f + up * 0.05f;
                to = mid + side * 3.8f - axis * 0.6f + up * 1.7f;
                look = mid - up * 0.3f;
                fovFrom = 34f; fovTo = 42f;
                break;
        }

        var position = Vector3.Lerp(from, to, u);
        if (set != null) position = OutOfCage(position);
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
        // Inset from the frame's edges, so a long line wraps onto two instead of running wall to wall.
        Anchor(subtitle.rectTransform, new Vector2(0.14f, 0f), new Vector2(0.86f, 1f));
        subtitle.alignment = TextAnchor.MiddleCenter;

        // Just the title, small and white in the lower bar, over the opening shot.
        titleGroup = Group(bottom.rectTransform, "Title");
        var t1 = Label(titleGroup.transform, title, 30, FontStyle.Normal, Color.white);
        Stretch(t1.rectTransform, (RectTransform)titleGroup.transform);
        t1.alignment = TextAnchor.MiddleCenter;

        // The ending: one word, as quiet as the title.
        creditsGroup = Group(root, "Credits");
        var end = Label(creditsGroup.transform, "End", 40, FontStyle.Normal, Color.white);
        Anchor(end.rectTransform, new Vector2(0f, 0.45f), new Vector2(1f, 0.55f));

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

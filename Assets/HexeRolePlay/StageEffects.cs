using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

// The non-verbal frame around the role-play: light, sound and a hat.
//
// Stepping into a character and back out has to be unmistakable, or the coach's feedback
// sounds like the witch talking. Theatre marks that boundary with several redundant cues at
// once, and so does this: the light drops and turns sickly green with a warm glow from
// below (the classic horror under-light), a shimmer sounds, and a witch's hat appears. On
// "cut" a clap sounds and everything returns. The avatar's posture and voice change too,
// but those live in AvatarPerformer.
//
// Everything here is generated at runtime - no audio files, textures or meshes to import.
public class StageEffects : MonoBehaviour
{
    public enum Cue { SceneStart, Cut, YourTurn }

    [Header("References")]
    public Light KeyLight;
    public Camera StageCamera;
    public Animator Avatar;

    [Header("Scene look")]
    public float TransitionSeconds = 1.1f;
    [Range(0f, 1f)] public float SceneKeyLightScale = 0.45f;
    public Color SceneKeyLightColor = new Color(0.62f, 0.78f, 0.58f);
    [Range(0f, 1f)] public float SceneAmbientScale = 0.45f;
    public Color UnderLightColor = new Color(1f, 0.52f, 0.18f);
    public float UnderLightIntensity = 1.6f;
    public float UnderLightRange = 1.4f;

    [Header("Hat")]
    [Tooltip("Metres above the head bone, along the avatar's up axis, where the hat sits.")]
    public float HatHeight = 0.17f;
    [Tooltip("Metres toward the back of the head.")]
    public float HatBack = 0.02f;
    public Vector3 HatTiltDegrees = new Vector3(-10f, 0f, 9f);
    public float HatScale = 1f;
    public Color HatColor = new Color(0.12f, 0.06f, 0.16f);
    public Color HatBandColor = new Color(0.45f, 0.18f, 0.55f);

    [Header("Sound")]
    [Range(0f, 1f)] public float CueVolume = 0.5f;

    [Header("Camera framing")]
    [Tooltip("Pull the camera back to a waist-up shot during a session, so gestures and posture are visible, and lower it slightly during the scene for a looming, low-angle witch. The original shot is restored afterwards.")]
    public bool FrameCamera = true;
    public float CoachShotDistance = 1.0f;
    public float SceneShotDistance = 0.85f;
    [Tooltip("How much lower the camera sits during the scene, in metres.")]
    public float SceneShotLower = 0.14f;
    public float FramingSeconds = 1.4f;

    public enum Framing { Original, Coach, Scene }

    // 0 = coach framing, 1 = full scene framing. The UI reads this for its vignette.
    public float SceneAmount { get; private set; }

    private float sceneTarget;
    private float baseKeyIntensity;
    private Color baseKeyColor;
    private Color baseAmbient;
    private float baseAmbientIntensity;
    private Light underLight;
    private Transform head;
    private Transform hat;
    private float hatUnitScale = 1f;   // cancels the head bone's own scale (FBX rigs are often in cm)
    private AudioSource cueSource;
    public bool IsCuePlaying => cueSource != null && cueSource.isPlaying;
    private AudioClip sceneStartClip, cutClip, turnClip;

    private Framing framing = Framing.Original;
    private Vector3 cameraHomePosition;
    private Quaternion cameraHomeRotation;
    private Vector3 cameraVelocity;
    private Vector3 front;              // from the avatar toward the audience, on the ground plane
    private float headHeight, chestHeight;

    private void Start()
    {
        if (KeyLight != null)
        {
            baseKeyIntensity = KeyLight.intensity;
            baseKeyColor = KeyLight.color;
        }
        baseAmbient = RenderSettings.ambientLight;
        baseAmbientIntensity = RenderSettings.ambientIntensity;

        if (Avatar != null && Avatar.isHuman) head = Avatar.GetBoneTransform(HumanBodyBones.Head);

        // Framing anchors are measured once, in the bind pose, so the camera does not follow
        // every breath and nod of the animated skeleton.
        if (StageCamera != null)
        {
            cameraHomePosition = StageCamera.transform.position;
            cameraHomeRotation = StageCamera.transform.rotation;
        }
        if (Avatar != null)
        {
            var root = Avatar.transform;
            var chest = Avatar.isHuman ? Avatar.GetBoneTransform(HumanBodyBones.Chest) : null;
            headHeight = head != null ? head.position.y - root.position.y : 1.3f;
            chestHeight = chest != null ? chest.position.y - root.position.y : headHeight * 0.8f;
            var toCamera = StageCamera != null && head != null ? StageCamera.transform.position - head.position : root.forward;
            toCamera.y = 0f;
            front = toCamera.sqrMagnitude > 1e-4f ? toCamera.normalized : root.forward;
        }

        var lightGo = new GameObject("HexeUnderLight");
        lightGo.transform.SetParent(transform, false);
        underLight = lightGo.AddComponent<Light>();
        underLight.type = LightType.Point;
        underLight.color = UnderLightColor;
        underLight.range = UnderLightRange;
        underLight.intensity = 0f;
        underLight.enabled = false;

        var audioGo = new GameObject("HexeStageAudio");
        audioGo.transform.SetParent(transform, false);
        cueSource = audioGo.AddComponent<AudioSource>();
        cueSource.playOnAwake = false;
        cueSource.spatialBlend = 0f;

        sceneStartClip = SynthShimmer();
        cutClip = SynthClap();
        turnClip = SynthChime();

        // Built now, while the skeleton is still in its bind pose, so "upright on the head"
        // is measured before any animation tilts the head.
        if (head != null) hat = BuildHat();
    }

    private void Update()
    {
        var step = Time.deltaTime / Mathf.Max(0.05f, TransitionSeconds);
        SceneAmount = Mathf.MoveTowards(SceneAmount, sceneTarget, step);
        var t = SceneAmount * SceneAmount * (3f - 2f * SceneAmount);

        if (KeyLight != null)
        {
            KeyLight.intensity = Mathf.Lerp(baseKeyIntensity, baseKeyIntensity * SceneKeyLightScale, t);
            KeyLight.color = Color.Lerp(baseKeyColor, SceneKeyLightColor, t);
        }
        RenderSettings.ambientLight = Color.Lerp(baseAmbient, baseAmbient * SceneAmbientScale, t);
        RenderSettings.ambientIntensity = Mathf.Lerp(baseAmbientIntensity, baseAmbientIntensity * SceneAmbientScale, t);

        if (underLight != null)
        {
            underLight.enabled = t > 0.001f;
            underLight.intensity = UnderLightIntensity * t;
            if (head != null && Avatar != null)
            {
                underLight.transform.position = head.position + front * 0.35f - Vector3.up * 0.4f;
            }
        }

        UpdateFraming();
    }

    public void SetFraming(Framing value) => framing = value;

    private void UpdateFraming()
    {
        if (!FrameCamera || StageCamera == null || Avatar == null) return;

        Vector3 targetPosition;
        Quaternion targetRotation;
        if (framing == Framing.Original)
        {
            targetPosition = cameraHomePosition;
            targetRotation = cameraHomeRotation;
        }
        else
        {
            var root = Avatar.transform.position;
            var scene = framing == Framing.Scene;
            var look = root + Vector3.up * Mathf.Lerp(chestHeight, headHeight, scene ? 0.6f : 0.4f);
            targetPosition = look + front * (scene ? SceneShotDistance : CoachShotDistance) + Vector3.up * (scene ? -SceneShotLower : 0.05f);
            targetRotation = Quaternion.LookRotation(look - targetPosition, Vector3.up);
        }

        var cam = StageCamera.transform;
        cam.position = Vector3.SmoothDamp(cam.position, targetPosition, ref cameraVelocity, FramingSeconds * 0.35f);
        cam.rotation = Quaternion.Slerp(cam.rotation, targetRotation, 1f - Mathf.Exp(-Time.deltaTime / (FramingSeconds * 0.3f)));
    }

    private void OnDisable()
    {
        // Leave the scene lit as it was found, whatever state the play was in.
        if (KeyLight != null)
        {
            KeyLight.intensity = baseKeyIntensity;
            KeyLight.color = baseKeyColor;
        }
        RenderSettings.ambientLight = baseAmbient;
        RenderSettings.ambientIntensity = baseAmbientIntensity;
        if (FrameCamera && StageCamera != null && cameraHomePosition != Vector3.zero)
        {
            StageCamera.transform.SetPositionAndRotation(cameraHomePosition, cameraHomeRotation);
        }
    }

    public void SetSceneMode(bool on)
    {
        sceneTarget = on ? 1f : 0f;
    }

    public void PlayCue(Cue cue)
    {
        if (cueSource == null) return;
        var clip = cue == Cue.SceneStart ? sceneStartClip : cue == Cue.Cut ? cutClip : turnClip;
        var volume = cue == Cue.YourTurn ? CueVolume * 0.45f : CueVolume;
        cueSource.PlayOneShot(clip, volume);
    }

    public async UniTask SetHatAsync(bool visible, CancellationToken token)
    {
        if (hat == null) return;
        var from = hat.localScale.x;
        var to = visible ? HatScale * hatUnitScale : 0f;
        var duration = visible ? 0.4f : 0.25f;
        if (visible) hat.gameObject.SetActive(true);

        for (var t = 0f; t < duration; t += Time.deltaTime)
        {
            var k = t / duration;
            // Overshoot on the way in, so it pops rather than grows.
            var eased = visible ? 1f + 2.2f * Mathf.Pow(k - 1f, 3f) + 1.2f * Mathf.Pow(k - 1f, 2f) : k * k;
            hat.localScale = Vector3.one * Mathf.LerpUnclamped(from, to, eased);
            await UniTask.Yield(token);
        }
        hat.localScale = Vector3.one * to;
        if (!visible) hat.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ hat

    private Transform BuildHat()
    {
        var root = Avatar.transform;
        var hatGo = new GameObject("WitchHat");
        hatGo.transform.position = head.position + root.up * HatHeight - root.forward * HatBack;
        hatGo.transform.rotation = root.rotation * Quaternion.Euler(HatTiltDegrees);
        hatGo.transform.SetParent(head, true);
        hatUnitScale = 1f / Mathf.Max(1e-4f, head.lossyScale.x);

        AddPart(hatGo.transform, "Brim", BuildBrim(0.07f, 0.19f, 0.012f, 32), HatColor);
        AddPart(hatGo.transform, "Cone", BuildCone(0.082f, 0.27f, 0.085f, 28, 12), HatColor);
        AddPart(hatGo.transform, "Band", BuildBand(0.086f, 0.032f, 28), HatBandColor);

        hatGo.transform.localScale = Vector3.zero;
        hatGo.SetActive(false);
        return hatGo.transform;
    }

    private static void AddPart(Transform parent, string name, Mesh mesh, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = MakeMaterial(color);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    private static Material MakeMaterial(Color color)
    {
        var shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        var mat = new Material(shader) { color = color };
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.15f);
        return mat;
    }

    // Flat ring with a slight droop at the rim; both faces, so it reads from below too.
    private static Mesh BuildBrim(float inner, float outer, float droop, int segments)
    {
        var verts = new Vector3[segments * 2];
        var tris = new int[segments * 12];
        for (var i = 0; i < segments; i++)
        {
            var a = i / (float)segments * Mathf.PI * 2f;
            var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            verts[i * 2] = dir * inner;
            verts[i * 2 + 1] = dir * outer - Vector3.up * droop;
            var n = (i + 1) % segments;
            var t = i * 12;
            tris[t] = i * 2; tris[t + 1] = n * 2; tris[t + 2] = i * 2 + 1;
            tris[t + 3] = n * 2; tris[t + 4] = n * 2 + 1; tris[t + 5] = i * 2 + 1;
            tris[t + 6] = i * 2; tris[t + 7] = i * 2 + 1; tris[t + 8] = n * 2;
            tris[t + 9] = n * 2; tris[t + 10] = i * 2 + 1; tris[t + 11] = n * 2 + 1;
        }
        var mesh = new Mesh { vertices = verts, triangles = tris };
        mesh.RecalculateNormals();
        return mesh;
    }

    // Cone whose tip bends backward - the silhouette that says "witch" at a glance.
    private static Mesh BuildCone(float radius, float height, float bend, int segments, int rings)
    {
        var verts = new Vector3[(rings + 1) * segments + 1];
        var tris = new int[rings * segments * 6 + segments * 3];
        for (var r = 0; r <= rings; r++)
        {
            var h = r / (float)rings;
            var rad = radius * (1f - h);
            var center = new Vector3(0f, h * height, -bend * h * h);
            for (var s = 0; s < segments; s++)
            {
                var a = s / (float)segments * Mathf.PI * 2f;
                verts[r * segments + s] = center + new Vector3(Mathf.Cos(a) * rad, 0f, Mathf.Sin(a) * rad);
            }
        }
        verts[verts.Length - 1] = new Vector3(0f, height, -bend);
        var ti = 0;
        for (var r = 0; r < rings; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                var n = (s + 1) % segments;
                var a = r * segments + s; var b = r * segments + n;
                var c = (r + 1) * segments + s; var d = (r + 1) * segments + n;
                tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
                tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
            }
        }
        for (var s = 0; s < segments; s++)
        {
            tris[ti++] = rings * segments + s;
            tris[ti++] = verts.Length - 1;
            tris[ti++] = rings * segments + (s + 1) % segments;
        }
        var mesh = new Mesh { vertices = verts, triangles = tris };
        mesh.RecalculateNormals();
        return mesh;
    }

    private static Mesh BuildBand(float radius, float height, int segments)
    {
        var verts = new Vector3[segments * 2];
        var tris = new int[segments * 6];
        for (var s = 0; s < segments; s++)
        {
            var a = s / (float)segments * Mathf.PI * 2f;
            var p = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            verts[s * 2] = p;
            verts[s * 2 + 1] = p * 0.97f + Vector3.up * height;
            var n = (s + 1) % segments;
            var t = s * 6;
            tris[t] = s * 2; tris[t + 1] = s * 2 + 1; tris[t + 2] = n * 2;
            tris[t + 3] = n * 2; tris[t + 4] = s * 2 + 1; tris[t + 5] = n * 2 + 1;
        }
        var mesh = new Mesh { vertices = verts, triangles = tris };
        mesh.RecalculateNormals();
        return mesh;
    }

    // ------------------------------------------------------------------ sound

    private const int Rate = 44100;

    // Rising arpeggio with a shimmer - the "magic happens" sting for entering the scene.
    private static AudioClip SynthShimmer()
    {
        var notes = new[] { 523.25f, 659.25f, 783.99f, 1046.5f, 1318.5f };
        var length = Mathf.CeilToInt(Rate * 1.4f);
        var data = new float[length];
        for (var n = 0; n < notes.Length; n++)
        {
            var start = Mathf.RoundToInt(Rate * 0.08f * n);
            for (var i = start; i < length; i++)
            {
                var t = (i - start) / (float)Rate;
                var env = Mathf.Exp(-t * 3.2f) * Mathf.Min(1f, t * 200f);
                var vib = 1f + 0.004f * Mathf.Sin(2f * Mathf.PI * 6f * t);
                data[i] += 0.16f * env * (Mathf.Sin(2f * Mathf.PI * notes[n] * vib * t) + 0.3f * Mathf.Sin(4f * Mathf.PI * notes[n] * t));
            }
        }
        return MakeClip("HexeShimmer", data);
    }

    // A single sharp clap: a burst of band-limited noise with a fast decay.
    private static AudioClip SynthClap()
    {
        var length = Mathf.CeilToInt(Rate * 0.25f);
        var data = new float[length];
        var rng = new System.Random(7);
        float low = 0f, prev = 0f;
        for (var i = 0; i < length; i++)
        {
            var t = i / (float)Rate;
            // Two transients a few ms apart, as real claps have.
            var env = Mathf.Exp(-t * 38f) + (t > 0.012f ? 0.6f * Mathf.Exp(-(t - 0.012f) * 30f) : 0f);
            var noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            low += 0.35f * (noise - low);          // lowpass
            var band = low - prev * 0.6f;          // crude highpass on top
            prev = low;
            data[i] = Mathf.Clamp(band * env * 1.6f, -1f, 1f);
        }
        return MakeClip("HexeClap", data);
    }

    // Soft two-tone chime: "your turn".
    private static AudioClip SynthChime()
    {
        var length = Mathf.CeilToInt(Rate * 0.5f);
        var data = new float[length];
        for (var i = 0; i < length; i++)
        {
            var t = i / (float)Rate;
            var env = Mathf.Exp(-t * 7f) * Mathf.Min(1f, t * 300f);
            data[i] = 0.25f * env * (Mathf.Sin(2f * Mathf.PI * 880f * t) + 0.5f * Mathf.Sin(2f * Mathf.PI * 1320f * t));
        }
        return MakeClip("HexeChime", data);
    }

    private static AudioClip MakeClip(string name, float[] data)
    {
        var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}

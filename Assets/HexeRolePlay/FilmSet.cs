using System.Collections.Generic;
using UnityEngine;

// The set of the scripted film, built when the film starts and taken down when it ends, so the
// live scene never sees it: a wooden cage around Hansel, a brick oven beside it, and the
// chicken bone Gretel slips him. Everything is a low-poly mesh with vertex colours, drawn with
// the forest's own material so it sits in the same world.
//
// Set coordinates: Hansel stands at the origin, x is the film camera's side of the line
// between him and the witch, y is up, z runs from the witch to Hansel (she stands at
// z = -witchDistance). The cage's front bars face her at z = -Front.
//
// The front has a wider gap between the two door posts, at x = HatchFrom..HatchTo: that is
// where every shot on Hansel looks through, and where the bone goes out. The film's
// close-ups sit about 0.3 m to the camera side, so the gap is on that side too.
public class FilmSet
{
    public const float HalfWidth = 0.6f;
    public const float Front = 0.42f;
    public const float Back = 0.5f;
    public const float HatchFrom = -0.04f;
    public const float HatchTo = 0.27f;
    public const float BoneLength = 0.13f;

    public Transform Root { get; private set; }
    public Transform Bone { get; private set; }
    public float Height { get; private set; }

    private GameObject oven, fire;
    private Material fireMaterial;
    private Light fireLight;
    private readonly List<Object> owned = new List<Object>();
    private Vector3 ovenMouthLocal;
    private Vector3 ovenFacingLocal;

    public Vector3 OvenMouth => Root.TransformPoint(ovenMouthLocal);
    public Vector3 OvenFacing => Root.TransformDirection(ovenFacingLocal);

    public Vector3 World(float x, float y, float z) => Root.TransformPoint(new Vector3(x, y, z));
    public Vector3 Local(Vector3 world) => Root.InverseTransformPoint(world);

    // hansel: where Hansel stands (on the ground). axis: horizontal, from the witch to Hansel.
    // ovenPlace: (side, up, along the axis) from the middle between the two.
    // headHeight: Hansel's head above the ground, so the cage fits him.
    public static FilmSet Build(Vector3 hansel, Vector3 axis, float witchDistance, Vector3 ovenPlace, int layer, float headHeight)
    {
        var set = new FilmSet();
        var root = new GameObject("HexeFilmSet");
        root.layer = layer;
        root.transform.SetPositionAndRotation(hansel, Quaternion.LookRotation(axis, Vector3.up));
        set.Root = root.transform;
        set.owned.Add(root);
        set.Height = Mathf.Max(1.7f, headHeight + 0.45f);

        var material = FindForestMaterial(set.owned);
        var random = new System.Random(7);

        var cage = new MeshDraft();
        BuildCage(cage, set.Height, random);
        // No shadows from the cage: bar and roof shadows would fall across the visitor's face,
        // which is what the film is for.
        set.Add("Cage", cage, material, layer, Vector3.zero).GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var ovenDraft = new MeshDraft();
        BuildOven(ovenDraft, random, out var mouthCenter, out var mouthWidth);
        var ovenPos = new Vector3(ovenPlace.x, ovenPlace.y, -witchDistance * 0.5f + ovenPlace.z);
        set.oven = set.Add("Oven", ovenDraft, material, layer, ovenPos);
        // The mouth faces the actors' side (+x): turn the oven so it does, wherever it stands.
        var toActors = new Vector3(-ovenPos.x, 0f, -witchDistance * 0.5f - ovenPos.z);
        if (toActors.sqrMagnitude > 1e-4f) set.oven.transform.localRotation = Quaternion.FromToRotation(Vector3.right, toActors.normalized);
        set.ovenMouthLocal = ovenPos + set.oven.transform.localRotation * mouthCenter;
        set.ovenFacingLocal = set.oven.transform.localRotation * Vector3.right;

        var fireDraft = new MeshDraft();
        AddArch(fireDraft, new Vector3(0.5f + 0.012f, 0f, 0f), mouthCenter.y - 0.2f, mouthWidth * 0.82f, 0.16f, new Color(1f, 1f, 1f));
        set.fireMaterial = FireMaterial(set.owned);
        set.fire = set.Add("OvenFire", fireDraft, set.fireMaterial, layer, Vector3.zero, set.oven.transform);

        var lightGo = new GameObject("OvenLight") { layer = layer };
        lightGo.transform.SetParent(set.oven.transform, false);
        lightGo.transform.localPosition = mouthCenter + Vector3.right * 0.35f;
        set.fireLight = lightGo.AddComponent<Light>();
        set.fireLight.type = LightType.Point;
        set.fireLight.color = new Color(1f, 0.52f, 0.22f);
        set.fireLight.range = 3.5f;
        set.fireLight.shadows = LightShadows.None;

        var boneDraft = new MeshDraft();
        BuildBone(boneDraft);
        var bone = set.Add("Bone", boneDraft, material, layer, Vector3.zero);
        bone.transform.SetParent(null, true);
        bone.SetActive(false);
        set.Bone = bone.transform;

        set.SetOven(0.15f, 0f);
        return set;
    }

    // level 0..1: embers to a roaring fire.
    public void SetOven(float level, float time)
    {
        var flicker = 0.85f + 0.3f * Mathf.PerlinNoise(time * 6f, 0.4f) * Mathf.Clamp01(level * 2f);
        var glow = Mathf.Clamp01(level) * flicker;
        if (fireMaterial != null)
        {
            var c = Color.Lerp(new Color(0.25f, 0.05f, 0.02f), new Color(1f, 0.72f, 0.3f), glow);
            fireMaterial.color = c;
            if (fireMaterial.HasProperty("_EmissionColor")) fireMaterial.SetColor("_EmissionColor", c);
        }
        if (fireLight != null) fireLight.intensity = 0.25f + glow * 2.6f;
    }

    public void Destroy()
    {
        foreach (var o in owned)
        {
            if (o != null) Object.Destroy(o);
        }
        owned.Clear();
    }

    private GameObject Add(string name, MeshDraft draft, Material material, int layer, Vector3 localPosition, Transform parent = null)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(parent != null ? parent : Root, false);
        go.transform.localPosition = localPosition;
        var mesh = draft.ToMesh(name);
        owned.Add(mesh);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        owned.Add(go);
        return go;
    }

    // ------------------------------------------------------------------ pieces

    private static void BuildCage(MeshDraft d, float h, System.Random random)
    {
        var wood = new Color(0.36f, 0.24f, 0.14f);
        var dark = new Color(0.25f, 0.16f, 0.09f);
        Color Vary(Color c) { var v = 0.85f + 0.3f * (float)random.NextDouble(); return new Color(c.r * v, c.g * v, c.b * v); }
        float Lean() => ((float)random.NextDouble() - 0.5f) * 0.02f;

        // Corner posts.
        foreach (var x in new[] { -HalfWidth, HalfWidth })
            foreach (var z in new[] { -Front, Back })
                d.AddBox(new Vector3(x, h * 0.5f, z), new Vector3(0.07f, h, 0.07f), Quaternion.identity, Vary(dark));

        // Front: bars, then the two door posts around the hatch, and a lock.
        foreach (var x in new[] { -0.44f, -0.26f, 0.43f })
            d.AddPrism(new Vector3(x + Lean(), 0.03f, -Front), new Vector3(x + Lean(), h, -Front), 0.022f, 7, Vary(wood));
        d.AddBox(new Vector3(HatchFrom - 0.025f, h * 0.5f, -Front), new Vector3(0.05f, h, 0.05f), Quaternion.identity, Vary(dark));
        d.AddBox(new Vector3(HatchTo + 0.025f, h * 0.5f, -Front), new Vector3(0.05f, h, 0.05f), Quaternion.identity, Vary(dark));
        d.AddBox(new Vector3(HatchTo + 0.03f, h * 0.55f, -Front - 0.04f), new Vector3(0.07f, 0.09f, 0.03f), Quaternion.identity, new Color(0.18f, 0.18f, 0.2f));

        // Back and sides. On the camera's side (+x), the bars at z 0.06 and 0.22 leave the gap
        // Gretel pushes the bone through.
        foreach (var x in new[] { -0.44f, -0.28f, -0.12f, 0.04f, 0.2f, 0.36f })
            d.AddPrism(new Vector3(x + Lean(), 0.03f, Back), new Vector3(x + Lean(), h, Back), 0.022f, 7, Vary(wood));
        foreach (var side in new[] { -HalfWidth, HalfWidth })
            foreach (var z in new[] { -0.26f, -0.1f, 0.06f, 0.22f, 0.36f })
                d.AddPrism(new Vector3(side, 0.03f, z + Lean()), new Vector3(side, h, z + Lean()), 0.022f, 7, Vary(wood));

        // Beams: bottom and top all round; a middle one on the sides and back only, so the hatch
        // stays open where his hand goes out.
        var w = HalfWidth * 2f + 0.07f;
        var depth = Front + Back + 0.07f;
        var midZ = (Back - Front) * 0.5f;
        foreach (var y in new[] { 0.06f, h })
        {
            d.AddBox(new Vector3(0f, y, -Front), new Vector3(w, 0.07f, 0.06f), Quaternion.identity, Vary(dark));
            d.AddBox(new Vector3(0f, y, Back), new Vector3(w, 0.07f, 0.06f), Quaternion.identity, Vary(dark));
            d.AddBox(new Vector3(-HalfWidth, y, midZ), new Vector3(0.06f, 0.07f, depth), Quaternion.identity, Vary(dark));
            d.AddBox(new Vector3(HalfWidth, y, midZ), new Vector3(0.06f, 0.07f, depth), Quaternion.identity, Vary(dark));
        }
        var mid = h * 0.5f;
        d.AddBox(new Vector3(0f, mid, Back), new Vector3(w, 0.06f, 0.05f), Quaternion.identity, Vary(dark));
        d.AddBox(new Vector3(-HalfWidth, mid, midZ), new Vector3(0.05f, 0.06f, depth), Quaternion.identity, Vary(dark));
        d.AddBox(new Vector3(HalfWidth, mid, midZ), new Vector3(0.05f, 0.06f, depth), Quaternion.identity, Vary(dark));

        // Roof planks, a little uneven.
        for (var z = -Front - 0.04f; z < Back + 0.06f; z += 0.155f)
        {
            var tilt = Quaternion.Euler(((float)random.NextDouble() - 0.5f) * 4f, 0f, ((float)random.NextDouble() - 0.5f) * 3f);
            d.AddBox(new Vector3(0f, h + 0.055f, z + 0.07f), new Vector3(w + 0.12f, 0.035f, 0.14f), tilt, Vary(wood));
        }
    }

    // A brick bread oven: stone base, brick body under a barrel vault, an arched mouth facing +x.
    private static void BuildOven(MeshDraft d, System.Random random, out Vector3 mouthCenter, out float mouthWidth)
    {
        var stone = new Color(0.42f, 0.4f, 0.38f);
        var brick = new Color(0.56f, 0.3f, 0.2f);
        Color Vary(Color c) { var v = 0.85f + 0.3f * (float)random.NextDouble(); return new Color(c.r * v, c.g * v, c.b * v); }

        const float baseH = 0.45f, bodyH = 0.5f, half = 0.5f;
        d.AddBox(new Vector3(0f, baseH * 0.5f, 0f), new Vector3(1.15f, baseH, 1.15f), Quaternion.identity, Vary(stone));
        d.AddBox(new Vector3(0f, baseH + bodyH * 0.5f, 0f), new Vector3(1f, bodyH, 1f), Quaternion.identity, Vary(brick));

        // Barrel vault along x, with flat end caps.
        const int steps = 10;
        var top = baseH + bodyH;
        for (var i = 0; i < steps; i++)
        {
            var a0 = Mathf.PI * i / steps;
            var a1 = Mathf.PI * (i + 1) / steps;
            var p0 = new Vector3(0f, top + Mathf.Sin(a0) * half, Mathf.Cos(a0) * half);
            var p1 = new Vector3(0f, top + Mathf.Sin(a1) * half, Mathf.Cos(a1) * half);
            var c = Vary(brick);
            var outward = new Vector3(0f, Mathf.Sin((a0 + a1) * 0.5f), Mathf.Cos((a0 + a1) * 0.5f));
            d.AddQuad(p0 + Vector3.right * half, p1 + Vector3.right * half, p1 - Vector3.right * half, p0 - Vector3.right * half, c, outward);
            d.AddTriangle(new Vector3(half, top, 0f), p1 + Vector3.right * half, p0 + Vector3.right * half, c, Vector3.right);
            d.AddTriangle(new Vector3(-half, top, 0f), p0 - Vector3.right * half, p1 - Vector3.right * half, c, Vector3.left);
        }
        d.AddBox(new Vector3(-0.22f, top + half + 0.15f, 0.18f), new Vector3(0.18f, 0.45f, 0.18f), Quaternion.identity, Vary(brick));

        // The mouth: a dark arch on the front, the fire drawn just in front of it by the caller.
        mouthWidth = 0.46f;
        var bottom = baseH + 0.06f;
        AddArch(d, new Vector3(half + 0.006f, 0f, 0f), bottom, mouthWidth, 0.2f, new Color(0.04f, 0.025f, 0.02f));
        mouthCenter = new Vector3(half, bottom + 0.2f, 0f);
    }

    // An upright arch facing +x at `at`: a rectangle `rise` high under a half disc.
    private static void AddArch(MeshDraft d, Vector3 at, float bottom, float width, float rise, Color color)
    {
        var r = width * 0.5f;
        var b = new Vector3(at.x, bottom, at.z);
        var spring = b + Vector3.up * rise;
        d.AddQuad(b + Vector3.forward * r, b - Vector3.forward * r, spring - Vector3.forward * r, spring + Vector3.forward * r, color, Vector3.right);
        const int steps = 10;
        for (var i = 0; i < steps; i++)
        {
            var a0 = Mathf.PI * i / steps;
            var a1 = Mathf.PI * (i + 1) / steps;
            var p0 = spring + new Vector3(0f, Mathf.Sin(a0) * r, Mathf.Cos(a0) * r);
            var p1 = spring + new Vector3(0f, Mathf.Sin(a1) * r, Mathf.Cos(a1) * r);
            d.AddTriangle(spring, p0, p1, color, Vector3.right);
        }
    }

    // A chicken bone along +z, centred on the origin.
    private static void BuildBone(MeshDraft d)
    {
        var color = new Color(0.92f, 0.88f, 0.78f);
        var h = BoneLength * 0.5f - 0.015f;
        d.AddPrism(new Vector3(0f, 0f, -h), new Vector3(0f, 0f, h), 0.011f, 7, color);
        foreach (var z in new[] { -h, h })
        {
            d.AddSphere(new Vector3(0.011f, 0f, z), 0.016f, color);
            d.AddSphere(new Vector3(-0.011f, 0f, z), 0.016f, color);
        }
    }

    // ------------------------------------------------------------------ materials

    private static Material FindForestMaterial(List<Object> owned)
    {
        foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
        {
            var m = r.sharedMaterial;
            if (m != null && m.shader != null && m.shader.name == "Hexe/VertexColorLit") return m;
        }
        var shader = Shader.Find("Hexe/VertexColorLit");
        if (shader != null)
        {
            var made = new Material(shader);
            owned.Add(made);
            return made;
        }
        // Without the forest shader the vertex colours are lost: one plain wood colour, then.
        var fallback = new Material(Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse")) { color = new Color(0.4f, 0.28f, 0.18f) };
        owned.Add(fallback);
        return fallback;
    }

    private static Material FireMaterial(List<Object> owned)
    {
        var unlit = Shader.Find("Unlit/Color");
        Material m;
        if (unlit != null) m = new Material(unlit);
        else
        {
            m = new Material(Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse"));
            m.EnableKeyword("_EMISSION");
        }
        owned.Add(m);
        return m;
    }

    // ------------------------------------------------------------------ mesh building

    private class MeshDraft
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Color> colors = new List<Color>();
        private readonly List<int> triangles = new List<int>();

        // With a normal given, the winding is turned to face it (Unity draws the clockwise side).
        public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Color color, Vector3? normal = null)
        {
            if (normal.HasValue && Vector3.Dot(Vector3.Cross(b - a, c - a), normal.Value) < 0f) { var t = b; b = c; c = t; }
            var n = normal ?? Vector3.Cross(b - a, c - a).normalized;
            var i = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c);
            normals.Add(n); normals.Add(n); normals.Add(n);
            colors.Add(color); colors.Add(color); colors.Add(color);
            triangles.Add(i); triangles.Add(i + 1); triangles.Add(i + 2);
        }

        // Corners in order round the edge; with a normal given, turned to face it.
        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, Vector3? normal = null)
        {
            if (normal.HasValue && Vector3.Dot(Vector3.Cross(b - a, c - a), normal.Value) < 0f) { var t = b; b = d; d = t; }
            var n = normal ?? Vector3.Cross(b - a, c - a).normalized;
            var i = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            for (var k = 0; k < 4; k++) { normals.Add(n); colors.Add(color); }
            triangles.Add(i); triangles.Add(i + 1); triangles.Add(i + 2);
            triangles.Add(i); triangles.Add(i + 2); triangles.Add(i + 3);
        }

        public void AddBox(Vector3 center, Vector3 size, Quaternion rotation, Color color)
        {
            var h = size * 0.5f;
            Vector3 P(float x, float y, float z) => center + rotation * new Vector3(x * h.x, y * h.y, z * h.z);
            AddQuad(P(-1, -1, 1), P(-1, 1, 1), P(1, 1, 1), P(1, -1, 1), color, rotation * Vector3.forward);
            AddQuad(P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), P(-1, -1, -1), color, rotation * Vector3.back);
            AddQuad(P(1, -1, 1), P(1, 1, 1), P(1, 1, -1), P(1, -1, -1), color, rotation * Vector3.right);
            AddQuad(P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1), P(-1, -1, 1), color, rotation * Vector3.left);
            AddQuad(P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1), color, rotation * Vector3.up);
            AddQuad(P(-1, -1, 1), P(1, -1, 1), P(1, -1, -1), P(-1, -1, -1), color, rotation * Vector3.down);
        }

        public void AddPrism(Vector3 from, Vector3 to, float radius, int sides, Color color)
        {
            var axis = to - from;
            if (axis.sqrMagnitude < 1e-8f) return;
            var rot = Quaternion.LookRotation(axis.normalized, Mathf.Abs(Vector3.Dot(axis.normalized, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up);
            for (var i = 0; i < sides; i++)
            {
                var a0 = Mathf.PI * 2f * i / sides;
                var a1 = Mathf.PI * 2f * (i + 1) / sides;
                var o0 = rot * new Vector3(Mathf.Cos(a0) * radius, Mathf.Sin(a0) * radius, 0f);
                var o1 = rot * new Vector3(Mathf.Cos(a1) * radius, Mathf.Sin(a1) * radius, 0f);
                var n = (o0 + o1).normalized;
                AddQuad(from + o0, to + o0, to + o1, from + o1, color, n);
                AddTriangle(to, to + o1, to + o0, color, axis.normalized);
                AddTriangle(from, from + o0, from + o1, color, -axis.normalized);
            }
        }

        public void AddSphere(Vector3 center, float radius, Color color)
        {
            const int rings = 4, segments = 6;
            for (var r = 0; r < rings; r++)
            {
                var t0 = Mathf.PI * r / rings;
                var t1 = Mathf.PI * (r + 1) / rings;
                for (var s = 0; s < segments; s++)
                {
                    var p0 = Mathf.PI * 2f * s / segments;
                    var p1 = Mathf.PI * 2f * (s + 1) / segments;
                    Vector3 P(float t, float p) => center + new Vector3(Mathf.Sin(t) * Mathf.Cos(p), Mathf.Cos(t), Mathf.Sin(t) * Mathf.Sin(p)) * radius;
                    var outward = (P((t0 + t1) * 0.5f, (p0 + p1) * 0.5f) - center).normalized;
                    AddQuad(P(t0, p0), P(t0, p1), P(t1, p1), P(t1, p0), color, outward);
                }
            }
        }

        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

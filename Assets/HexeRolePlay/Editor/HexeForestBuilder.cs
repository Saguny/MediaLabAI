using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Builds the play's set: a witch's clearing in a dark pine forest at dusk, with the
// gingerbread house, a cookie path, candy, glowing mushrooms, fireflies, fog and a moonlit sky.
//
// Everything is generated here from a fixed seed, in a flat-shaded low-poly style that suits
// the anime avatars and costs little to render: a handful of combined meshes with vertex
// colours and one shader (Environment/HexeVertexColorLit). No third-party models, so nothing
// to license. The meshes are saved under Environment/Generated so the scene keeps them.
//
// Layout: the witch stands at the origin facing +Z, Hansel ~1.25 m in front of her. The
// film's establishing and two-shots look across from +X, so the house sits on the -X side,
// behind the pair; over-the-shoulder shots look along Z into the trees.
public static class HexeForestBuilder
{
    private const string Folder = "Assets/HexeRolePlay/Environment";
    private const string Generated = Folder + "/Generated";
    private const string RootName = "HexeForest";
    private const int Seed = 1812;   // the Grimms' first edition

    private static readonly Vector3 HouseCenter = new Vector3(-6.6f, 0f, -1.4f);
    private const float ClearingRadius = 8.5f;
    private const float ForestRadius = 42f;

    [MenuItem("Tools/Hexe Role-Play/Build Forest Set")]
    public static void BuildMenu()
    {
        Build();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
    }

    // Called by Set Up Scene: builds the set once.
    public static void EnsureBuilt()
    {
        if (GameObject.Find(RootName) == null) Build();
    }

    public static void Build()
    {
        var old = GameObject.Find(RootName);
        if (old != null) Object.DestroyImmediate(old);
        if (!AssetDatabase.IsValidFolder(Generated)) AssetDatabase.CreateFolder(Folder, "Generated");

        var material = LoadOrCreateMaterial("HexeForest.mat", Shader.Find("Hexe/VertexColorLit"));
        var root = new GameObject(RootName);
        var random = new System.Random(Seed);

        AddMesh(root, "Ground", BuildGround(random), material, false);
        AddMesh(root, "Trees", BuildTrees(random), material, true);
        AddMesh(root, "Undergrowth", BuildUndergrowth(random), material, true);
        AddMesh(root, "GingerbreadHouse", BuildHouse(random), material, true);

        AddLights(root);
        AddFireflies(root);
        SetUpSky();
        Debug.Log("HexeForestBuilder: forest set built.");
    }

    // ================================================================== geometry

    // Flat-shaded triangles with vertex colours, in world space through a transform stack.
    private class MeshBuilder
    {
        public readonly List<Vector3> V = new List<Vector3>();
        public readonly List<Vector3> N = new List<Vector3>();
        public readonly List<Color> C = new List<Color>();
        public Matrix4x4 M = Matrix4x4.identity;

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Color color)
        {
            a = M.MultiplyPoint3x4(a);
            b = M.MultiplyPoint3x4(b);
            c = M.MultiplyPoint3x4(c);
            var n = Vector3.Cross(b - a, c - a).normalized;
            var lin = Lin(color);
            V.Add(a); V.Add(b); V.Add(c);
            N.Add(n); N.Add(n); N.Add(n);
            C.Add(lin); C.Add(lin); C.Add(lin);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            Tri(a, b, c, color);
            Tri(a, c, d, color);
        }

        public void Box(Vector3 center, Vector3 size, Quaternion rotation, Color color)
        {
            var saved = M;
            M = M * Matrix4x4.TRS(center, rotation, size);
            var p = new[]
            {
                new Vector3(-.5f, -.5f, -.5f), new Vector3(.5f, -.5f, -.5f), new Vector3(.5f, .5f, -.5f), new Vector3(-.5f, .5f, -.5f),
                new Vector3(-.5f, -.5f, .5f), new Vector3(.5f, -.5f, .5f), new Vector3(.5f, .5f, .5f), new Vector3(-.5f, .5f, .5f),
            };
            Quad(p[0], p[3], p[2], p[1], color);
            Quad(p[5], p[6], p[7], p[4], color);
            Quad(p[4], p[7], p[3], p[0], color);
            Quad(p[1], p[2], p[6], p[5], color);
            Quad(p[3], p[7], p[6], p[2], color);
            Quad(p[0], p[1], p[5], p[4], color);
            M = saved;
        }

        // A cylinder or cone (topRadius 0) standing on its base along local +Y.
        public void Cylinder(Vector3 basePoint, Quaternion rotation, float height, float bottomRadius, float topRadius, int sides, Color side, Color? cap = null, float yaw = 0f)
        {
            var saved = M;
            M = M * Matrix4x4.TRS(basePoint, rotation * Quaternion.Euler(0f, yaw, 0f), Vector3.one);
            var top = new Vector3(0f, height, 0f);
            for (var i = 0; i < sides; i++)
            {
                var a0 = i * Mathf.PI * 2f / sides;
                var a1 = (i + 1) * Mathf.PI * 2f / sides;
                var d0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0));
                var d1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                var b0 = d0 * bottomRadius;
                var b1 = d1 * bottomRadius;
                if (topRadius <= 0f)
                {
                    Tri(b0, top, b1, side);
                }
                else
                {
                    var t0 = top + d0 * topRadius;
                    var t1 = top + d1 * topRadius;
                    Quad(b0, t0, t1, b1, side);
                    Tri(top, t1, t0, cap ?? side);
                }
                Tri(Vector3.zero, b0, b1, cap ?? side);
            }
            M = saved;
        }

        // A lumpy ball: an octahedron subdivided once, each vertex pushed in or out.
        public void Blob(Vector3 center, Vector3 scale, float lumpiness, System.Random random, Color color, float yaw = 0f)
        {
            var saved = M;
            M = M * Matrix4x4.TRS(center, Quaternion.Euler(0f, yaw, 0f), scale);
            var faces = new List<Vector3[]>();
            var o = new[] { Vector3.up, Vector3.right, Vector3.forward, Vector3.left, Vector3.back, Vector3.down };
            int[,] tris = { { 0, 2, 1 }, { 0, 3, 2 }, { 0, 4, 3 }, { 0, 1, 4 }, { 5, 1, 2 }, { 5, 2, 3 }, { 5, 3, 4 }, { 5, 4, 1 } };
            var jitter = new Dictionary<Vector3, float>();
            float J(Vector3 p)
            {
                var key = new Vector3(Mathf.Round(p.x * 100f), Mathf.Round(p.y * 100f), Mathf.Round(p.z * 100f));
                if (!jitter.TryGetValue(key, out var j)) jitter[key] = j = 1f + ((float)random.NextDouble() - 0.5f) * 2f * lumpiness;
                return j;
            }
            for (var t = 0; t < 8; t++)
            {
                var a = o[tris[t, 0]];
                var b = o[tris[t, 1]];
                var c = o[tris[t, 2]];
                var ab = (a + b).normalized;
                var bc = (b + c).normalized;
                var ca = (c + a).normalized;
                foreach (var f in new[] { new[] { a, ab, ca }, new[] { ab, b, bc }, new[] { ca, bc, c }, new[] { ab, bc, ca } }) faces.Add(f);
            }
            foreach (var f in faces) Tri(f[0] * J(f[0]), f[1] * J(f[1]), f[2] * J(f[2]), color);
            M = saved;
        }

        public Mesh ToMesh(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(V);
            mesh.SetNormals(N);
            mesh.SetColors(C);
            var idx = new int[V.Count];
            for (var i = 0; i < idx.Length; i++) idx[i] = i;
            mesh.SetTriangles(idx, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    // Vertex colours are not converted to linear by Unity; the shader expects linear.
    private static Color Lin(Color c) =>
        PlayerSettings.colorSpace == ColorSpace.Linear ? new Color(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b), c.a) : c;

    private static Color Dim(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

    private static Color Glow(Color c, float amount) => new Color(c.r, c.g, c.b, 1f - amount);

    private static Color Vary(Color c, System.Random r, float amount)
    {
        float J() => 1f + ((float)r.NextDouble() - 0.5f) * 2f * amount;
        return new Color(Mathf.Clamp01(c.r * J()), Mathf.Clamp01(c.g * J()), Mathf.Clamp01(c.b * J()), c.a);
    }

    private static float Range(System.Random r, float a, float b) => a + (float)r.NextDouble() * (b - a);

    // Flat clearing, rising gently into hills where the trees thin out into the fog.
    private static float Height(float x, float z)
    {
        var r = new Vector2(x, z).magnitude;
        var rise = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ClearingRadius + 3f, ForestRadius, r));
        return rise * (1.2f + 3.5f * Mathf.PerlinNoise(x * 0.06f + 10f, z * 0.06f + 3f));
    }

    private static Vector3 OnGround(float x, float z) => new Vector3(x, Height(x, z), z);

    // ================================================================== parts

    private static Mesh BuildGround(System.Random random)
    {
        var b = new MeshBuilder();
        const int rings = 34, segments = 72;
        var moss = new Color(0.17f, 0.27f, 0.13f);
        var dark = new Color(0.1f, 0.17f, 0.1f);
        var dirt = new Color(0.33f, 0.24f, 0.15f);

        float RingRadius(int i) => i == 0 ? 0f : Mathf.Pow(i / (float)rings, 1.3f) * (ForestRadius + 18f);
        Vector3 P(int ring, int seg)
        {
            var a = (seg % segments) * Mathf.PI * 2f / segments + (ring % 2) * Mathf.PI / segments;
            var r = RingRadius(ring);
            return OnGround(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }
        Color At(Vector3 p)
        {
            var n = Mathf.PerlinNoise(p.x * 0.25f + 40f, p.z * 0.25f + 7f);
            var c = Color.Lerp(dark, moss, n);
            // Trodden earth where the actors stand and along the path to the door.
            var bare = Mathf.Max(1f - p.magnitude / 3.2f, PathCloseness(p) * 0.85f);
            c = Color.Lerp(c, dirt, Mathf.Clamp01(bare * 1.4f) * (0.75f + 0.25f * n));
            return Vary(c, random, 0.02f);
        }

        for (var ring = 0; ring < rings; ring++)
        {
            for (var s = 0; s < segments; s++)
            {
                var a = P(ring, s);
                var c = P(ring + 1, s);
                var d = P(ring + 1, s + 1);
                if (ring == 0)
                {
                    b.Tri(Vector3.zero, d, c, At((c + d) / 3f));
                    continue;
                }
                var e = P(ring, s + 1);
                b.Tri(a, e, c, At((a + c + e) / 3f));
                b.Tri(e, d, c, At((e + c + d) / 3f));
            }
        }
        return Save(b.ToMesh("HexeGround"));
    }

    private static readonly Vector3 DoorPoint = HouseCenter + Quaternion.Euler(0f, HouseYaw(), 0f) * new Vector3(0f, 0f, 1.9f);

    // The house faces the spot between the actors.
    private static float HouseYaw()
    {
        var to = new Vector3(0f, 0f, 0.6f) - HouseCenter;
        return Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
    }

    // 1 on the line from the clearing to the door, fading out over a metre.
    private static float PathCloseness(Vector3 p)
    {
        var a = new Vector3(-1.6f, 0f, 0.3f);
        var bPoint = new Vector3(DoorPoint.x, 0f, DoorPoint.z);
        var ab = bPoint - a;
        var t = Mathf.Clamp01(Vector3.Dot(new Vector3(p.x, 0f, p.z) - a, ab) / ab.sqrMagnitude);
        var d = Vector3.Distance(new Vector3(p.x, 0f, p.z), a + ab * t);
        return Mathf.Clamp01(1f - d / 1.1f);
    }

    private static bool Free(Vector3 p, List<Vector3> taken, float spacing)
    {
        if (Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(HouseCenter.x, 0f, HouseCenter.z)) < 4.6f) return false;
        if (PathCloseness(p) > 0f) return false;
        foreach (var t in taken) if ((t - p).sqrMagnitude < spacing * spacing) return false;
        return true;
    }

    private static Mesh BuildTrees(System.Random random)
    {
        var b = new MeshBuilder();
        var taken = new List<Vector3>();
        var bark = new Color(0.24f, 0.16f, 0.11f);
        var needles = new Color(0.1f, 0.22f, 0.15f);
        var needlesBlue = new Color(0.1f, 0.2f, 0.22f);
        var deadBark = new Color(0.2f, 0.18f, 0.18f);

        for (var attempt = 0; attempt < 4000 && taken.Count < 300; attempt++)
        {
            // Denser close in, where the camera sees them; the fog swallows the far ones.
            var r = Mathf.Lerp(ClearingRadius - 1.5f, ForestRadius, Mathf.Pow((float)random.NextDouble(), 1.35f));
            var a = (float)random.NextDouble() * Mathf.PI * 2f;
            var p = OnGround(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            if (!Free(p, taken, r < 16f ? 1.9f : 2.6f)) continue;
            taken.Add(p);

            var tilt = Quaternion.Euler(Range(random, -3f, 3f), 0f, Range(random, -3f, 3f));
            var yaw = Range(random, 0f, 360f);
            if (random.NextDouble() < 0.07)
            {
                DeadTree(b, p, tilt, yaw, random, Vary(deadBark, random, 0.1f));
                continue;
            }

            var height = Range(random, 6f, 12f) * (r < 12f ? 0.9f : 1f);
            var trunk = Range(random, 1.1f, 1.8f);
            var radius = height * Range(random, 0.2f, 0.26f);
            b.Cylinder(p - Vector3.up * 0.3f, tilt, trunk + 1f, radius * 0.11f, radius * 0.07f, 6, Vary(bark, random, 0.12f), yaw: yaw);
            var tiers = 4;
            var green = Vary(Color.Lerp(needles, needlesBlue, (float)random.NextDouble()), random, 0.12f);
            for (var i = 0; i < tiers; i++)
            {
                var k = i / (float)tiers;
                var baseY = trunk + (height - trunk) * k * 0.78f;
                var tierHeight = (height - trunk) * (0.48f - 0.08f * k);
                var tierRadius = radius * (1f - 0.62f * k);
                var shade = Color.Lerp(Dim(green, 0.8f), Dim(green, 1.15f), k);
                b.Cylinder(p + tilt * new Vector3(0f, baseY, 0f), tilt, tierHeight, tierRadius, 0f, 7, shade, Dim(shade, 0.6f), yaw + i * 23f);
            }
        }
        return Save(b.ToMesh("HexeTrees"));
    }

    // A bare, crooked tree: the witch's forest needs a few.
    private static void DeadTree(MeshBuilder b, Vector3 p, Quaternion tilt, float yaw, System.Random random, Color bark)
    {
        var height = Range(random, 3.5f, 6f);
        b.Cylinder(p - Vector3.up * 0.2f, tilt, height, 0.28f, 0.06f, 6, bark, yaw: yaw);
        var branches = random.Next(3, 6);
        for (var i = 0; i < branches; i++)
        {
            var y = height * Range(random, 0.35f, 0.85f);
            var dir = Quaternion.FromToRotation(Vector3.up, Quaternion.Euler(Range(random, -70f, -35f), yaw + i * 360f / branches + Range(random, -25f, 25f), 0f) * Vector3.up);
            var length = Range(random, 1f, 2.2f) * (1f - y / height * 0.5f);
            var start = p + tilt * new Vector3(0f, y, 0f);
            b.Cylinder(start, dir, length, 0.08f, 0.02f, 5, bark);
            // A crook at the end.
            var mid = start + dir * new Vector3(0f, length, 0f);
            b.Cylinder(mid, Quaternion.Euler(Range(random, -40f, 10f), Range(random, 0f, 360f), 0f), length * 0.5f, 0.03f, 0.008f, 4, bark);
        }
    }

    private static Mesh BuildUndergrowth(System.Random random)
    {
        var b = new MeshBuilder();
        var taken = new List<Vector3>();

        // Boulders, half sunk in the moss.
        var stone = new Color(0.34f, 0.34f, 0.37f);
        for (var i = 0; i < 26; i++)
        {
            var r = Range(random, 5f, 30f);
            var a = Range(random, 0f, Mathf.PI * 2f);
            var p = OnGround(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            if (!Free(p, taken, 1.5f)) continue;
            taken.Add(p);
            var s = Range(random, 0.25f, 1.1f);
            b.Blob(p - Vector3.up * s * 0.35f, new Vector3(s * Range(random, 1f, 1.6f), s * Range(random, 0.6f, 0.9f), s), 0.22f, random, Vary(stone, random, 0.1f), Range(random, 0f, 360f));
        }

        // Grass tufts, thickest around the clearing's edge.
        var grass = new[] { new Color(0.24f, 0.38f, 0.16f), new Color(0.2f, 0.32f, 0.14f), new Color(0.3f, 0.4f, 0.18f) };
        for (var i = 0; i < 650; i++)
        {
            var r = Mathf.Lerp(2.2f, 22f, Mathf.Pow((float)random.NextDouble(), 0.8f));
            var a = Range(random, 0f, Mathf.PI * 2f);
            var p = OnGround(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            if (PathCloseness(p) > 0.3f || Vector3.Distance(p, HouseCenter) < 2.4f) continue;
            var color = Vary(grass[random.Next(grass.Length)], random, 0.12f);
            var blades = random.Next(3, 6);
            for (var k = 0; k < blades; k++)
            {
                var dir = Quaternion.Euler(0f, Range(random, 0f, 360f), 0f);
                var h = Range(random, 0.18f, 0.42f);
                var lean = dir * new Vector3(0f, h, Range(random, 0.05f, 0.15f));
                var w = dir * new Vector3(0.03f, 0f, 0f);
                b.Tri(p - w, p + lean, p + w, color);
            }
        }

        // Glowing mushrooms in little rings and clusters.
        var caps = new[] { new Color(0.35f, 0.95f, 0.9f), new Color(0.75f, 0.45f, 1f), new Color(0.55f, 1f, 0.5f) };
        var stem = new Color(0.86f, 0.82f, 0.72f);
        var toadstool = new Color(0.78f, 0.12f, 0.1f);
        for (var i = 0; i < 34; i++)
        {
            var r = Range(random, 3.2f, 16f);
            var a = Range(random, 0f, Mathf.PI * 2f);
            var center = OnGround(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            if (PathCloseness(center) > 0.2f || Vector3.Distance(center, HouseCenter) < 2.6f) continue;
            var glowing = random.NextDouble() < 0.7;
            var cap = glowing ? Glow(caps[random.Next(caps.Length)], 0.55f) : toadstool;
            var count = random.Next(2, 6);
            for (var k = 0; k < count; k++)
            {
                var p = center + new Vector3(Range(random, -0.35f, 0.35f), 0f, Range(random, -0.35f, 0.35f));
                var h = Range(random, 0.08f, 0.26f);
                var w = h * Range(random, 0.5f, 0.8f);
                var lean = Quaternion.Euler(Range(random, -12f, 12f), 0f, Range(random, -12f, 12f));
                b.Cylinder(p, lean, h, w * 0.22f, w * 0.18f, 5, stem);
                b.Cylinder(p + lean * new Vector3(0f, h * 0.92f, 0f), lean, w * 0.65f, w, 0f, 8, cap, Dim(cap, 0.7f), Range(random, 0f, 360f));
            }
        }
        return Save(b.ToMesh("HexeUndergrowth"));
    }

    // The gingerbread house, built in its own frame (door toward local +Z) and then placed.
    private static Mesh BuildHouse(System.Random random)
    {
        var b = new MeshBuilder();
        b.M = Matrix4x4.TRS(HouseCenter, Quaternion.Euler(0f, HouseYaw(), 0f), Vector3.one);

        var ginger = new Color(0.62f, 0.34f, 0.15f);
        var gingerDark = new Color(0.5f, 0.26f, 0.11f);
        var choc = new Color(0.27f, 0.14f, 0.08f);
        var icing = new Color(0.97f, 0.95f, 0.92f);
        var window = Glow(new Color(1f, 0.72f, 0.32f), 1f);
        var candy = new[] { new Color(0.9f, 0.12f, 0.2f), new Color(0.2f, 0.75f, 0.35f), new Color(1f, 0.55f, 0.8f), new Color(1f, 0.85f, 0.2f), new Color(0.35f, 0.6f, 1f) };

        const float w = 3.4f, d = 3.6f, h = 2.3f;
        // Walls, as stacked "biscuit" courses for texture.
        for (var y = 0f; y < h - 0.01f; y += 0.46f)
        {
            var c = Vary((int)(y / 0.46f) % 2 == 0 ? ginger : gingerDark, random, 0.04f);
            b.Box(new Vector3(0f, y + 0.23f, 0f), new Vector3(w, 0.46f, d), Quaternion.identity, c);
        }
        // Gables.
        var roofRise = 1.7f;
        foreach (var z in new[] { d / 2f, -d / 2f })
        {
            var s = Mathf.Sign(z);
            var a = new Vector3(-w / 2f, h, z);
            var top = new Vector3(0f, h + roofRise, z);
            var c = new Vector3(w / 2f, h, z);
            if (s > 0) b.Tri(c, top, a, ginger); else b.Tri(a, top, c, ginger);
        }
        // Roof slabs of chocolate with an icing edge and candy studs.
        var slope = Mathf.Atan2(roofRise, w / 2f) * Mathf.Rad2Deg;
        var slabLength = Mathf.Sqrt(roofRise * roofRise + w * w / 4f) + 0.45f;
        foreach (var side in new[] { -1f, 1f })
        {
            var rot = Quaternion.Euler(0f, 0f, -side * slope);
            var center = new Vector3(side * w / 4f, h + roofRise / 2f + 0.12f, 0f) + rot * new Vector3(side * 0.18f, 0f, 0f);
            b.Box(center, new Vector3(slabLength, 0.18f, d + 0.7f), rot, choc);
            var eave = center + rot * new Vector3(side * slabLength / 2f, -0.06f, 0f);
            b.Box(eave, new Vector3(0.16f, 0.16f, d + 0.75f), rot, icing);
            // Icing drips along the eave.
            for (var z = -d / 2f - 0.25f; z <= d / 2f + 0.25f; z += 0.32f)
            {
                b.Box(eave + new Vector3(0f, -0.12f - 0.08f * Mathf.PerlinNoise(z * 3f, side), z), new Vector3(0.1f, 0.18f + 0.15f * Mathf.PerlinNoise(z * 5f, side), 0.1f), Quaternion.identity, icing);
            }
            // Candy studs in rows on the roof.
            for (var row = 0; row < 3; row++)
            {
                for (var z = -d / 2f; z <= d / 2f; z += 0.6f)
                {
                    var along = -slabLength / 2f + 0.45f + row * 0.62f;
                    var p = center + rot * new Vector3(side * along, 0.13f, z + (row % 2) * 0.3f);
                    b.Blob(p, Vector3.one * 0.11f, 0.15f, random, candy[random.Next(candy.Length)]);
                }
            }
        }
        // Ridge.
        b.Box(new Vector3(0f, h + roofRise + 0.2f, 0f), new Vector3(0.24f, 0.24f, d + 0.75f), Quaternion.identity, icing);
        // Chimney.
        b.Box(new Vector3(-0.8f, h + roofRise - 0.1f, -0.7f), new Vector3(0.5f, 1.4f, 0.5f), Quaternion.identity, gingerDark);
        b.Box(new Vector3(-0.8f, h + roofRise + 0.65f, -0.7f), new Vector3(0.62f, 0.14f, 0.62f), Quaternion.identity, icing);

        // Front: door, windows with icing frames, corner piping.
        var front = d / 2f + 0.02f;
        b.Box(new Vector3(0f, 0.8f, front), new Vector3(0.95f, 1.6f, 0.08f), Quaternion.identity, choc);
        b.Box(new Vector3(0.28f, 0.8f, front + 0.06f), new Vector3(0.08f, 0.08f, 0.05f), Quaternion.identity, candy[3]);
        foreach (var x in new[] { -0.6f, 0.6f })
        {
            for (var y = 0.05f; y < 1.75f; y += 0.18f) b.Box(new Vector3(x, y + 0.09f, front + 0.03f), new Vector3(0.14f, 0.17f, 0.08f), Quaternion.identity, ((int)(y / 0.18f) % 2 == 0) ? candy[0] : icing);
        }
        b.Box(new Vector3(0f, 1.72f, front + 0.03f), new Vector3(1.34f, 0.14f, 0.08f), Quaternion.identity, icing);
        void Window(Vector3 c, Quaternion r)
        {
            b.Box(c, new Vector3(0.62f, 0.62f, 0.06f), r, window);
            b.Box(c + r * new Vector3(0f, 0f, 0.04f), new Vector3(0.72f, 0.07f, 0.05f), r, icing);
            b.Box(c + r * new Vector3(0f, 0f, 0.04f), new Vector3(0.07f, 0.72f, 0.05f), r, icing);
            b.Box(c + r * new Vector3(0f, 0.36f, 0.04f), new Vector3(0.8f, 0.09f, 0.06f), r, icing);
            b.Box(c + r * new Vector3(0f, -0.36f, 0.04f), new Vector3(0.8f, 0.09f, 0.06f), r, icing);
        }
        Window(new Vector3(-1.15f, 1.25f, front), Quaternion.identity);
        Window(new Vector3(1.15f, 1.25f, front), Quaternion.identity);
        Window(new Vector3(w / 2f + 0.02f, 1.25f, 0.4f), Quaternion.Euler(0f, 90f, 0f));
        Window(new Vector3(-w / 2f - 0.02f, 1.25f, 0.4f), Quaternion.Euler(0f, -90f, 0f));
        Window(new Vector3(0f, h + 0.6f, front), Quaternion.identity);   // the attic
        foreach (var x in new[] { -w / 2f, w / 2f })
        foreach (var z in new[] { -d / 2f, d / 2f })
        {
            b.Box(new Vector3(x, h / 2f, z), new Vector3(0.14f, h, 0.14f), Quaternion.identity, icing);
        }
        // Gumdrops along the foot of the walls.
        for (var x = -w / 2f + 0.2f; x < w / 2f; x += 0.38f)
        {
            if (Mathf.Abs(x) < 0.7f) continue;
            b.Cylinder(new Vector3(x, 0f, front + 0.2f), Quaternion.identity, 0.24f, 0.15f, 0.05f, 7, candy[random.Next(candy.Length)]);
        }

        // Lollipops either side of the path, and candy canes.
        foreach (var x in new[] { -1.35f, 1.35f })
        {
            var basePoint = new Vector3(x, 0f, front + 1.2f);
            b.Cylinder(basePoint, Quaternion.identity, 1.35f, 0.04f, 0.04f, 6, icing);
            var disc = basePoint + new Vector3(0f, 1.55f, 0f);
            const int slices = 12;
            for (var i = 0; i < slices; i++)
            {
                var a0 = i * Mathf.PI * 2f / slices;
                var a1 = (i + 1) * Mathf.PI * 2f / slices;
                var c = i % 2 == 0 ? (x < 0 ? candy[0] : candy[4]) : icing;
                var p0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0f) * 0.38f;
                var p1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0f) * 0.38f;
                var f = new Vector3(0f, 0f, 0.06f);
                b.Tri(disc + f, disc + p0 + f, disc + p1 + f, c);
                b.Tri(disc - f, disc + p1 - f, disc + p0 - f, c);
                b.Quad(disc + p0 - f, disc + p1 - f, disc + p1 + f, disc + p0 + f, c);
            }
        }
        foreach (var x in new[] { -2.3f, -1.9f, 1.9f, 2.3f })
        {
            var basePoint = new Vector3(x, 0f, front + 0.6f);
            for (var i = 0; i < 6; i++)
            {
                b.Cylinder(basePoint + new Vector3(0f, i * 0.16f, 0f), Quaternion.identity, 0.16f, 0.05f, 0.05f, 6, i % 2 == 0 ? candy[0] : icing);
            }
            var hook = basePoint + new Vector3(0f, 0.96f, 0f);
            var dir = Mathf.Sign(x);
            for (var i = 0; i < 5; i++)
            {
                var a = i / 4f * Mathf.PI;
                var p = hook + new Vector3(dir * (0.12f - Mathf.Cos(a) * 0.12f), Mathf.Sin(a) * 0.14f, 0f);
                b.Blob(p, Vector3.one * 0.055f, 0f, random, i % 2 == 0 ? icing : candy[0]);
            }
        }

        // The cookie path, from the door to where the actors stand.
        b.M = Matrix4x4.identity;
        var start = DoorPoint;
        var end = new Vector3(-1.6f, 0f, 0.3f);
        var steps = Mathf.RoundToInt(Vector3.Distance(start, end) / 0.75f);
        var cookie = new Color(0.8f, 0.6f, 0.34f);
        for (var i = 0; i <= steps; i++)
        {
            var p = Vector3.Lerp(start, end, i / (float)steps) + new Vector3(Range(random, -0.12f, 0.12f), 0f, Range(random, -0.12f, 0.12f));
            var r = Range(random, 0.26f, 0.34f);
            b.Cylinder(p + Vector3.up * 0.005f, Quaternion.identity, 0.05f, r, r * 0.95f, 10, Vary(cookie, random, 0.06f), Vary(cookie, random, 0.08f), Range(random, 0f, 360f));
            for (var k = 0; k < 3; k++)
            {
                var a = Range(random, 0f, Mathf.PI * 2f);
                var q = p + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r * 0.55f + Vector3.up * 0.058f;
                b.Box(q, new Vector3(0.05f, 0.02f, 0.05f), Quaternion.Euler(0f, Range(random, 0f, 90f), 0f), choc);
            }
        }
        return Save(b.ToMesh("HexeGingerbreadHouse"));
    }

    // ================================================================== light, sky, life

    private static void AddLights(GameObject root)
    {
        var yaw = Quaternion.Euler(0f, HouseYaw(), 0f);
        Light Point(string name, Vector3 at, Color color, float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.position = at;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.renderMode = LightRenderMode.ForcePixel;
            l.shadows = LightShadows.None;
            return l;
        }
        // Warm glow spilling from the windows onto the path.
        Point("HouseGlow", HouseCenter + yaw * new Vector3(0f, 1.3f, 2.6f), new Color(1f, 0.62f, 0.3f), 2.4f, 7f);
        // Cold mushroom light in the trees behind the witch, for a rim on the far side.
        Point("WoodGlow", new Vector3(1.8f, 0.6f, -4.5f), new Color(0.35f, 0.85f, 0.9f), 1.2f, 5f);
    }

    private static void AddFireflies(GameObject root)
    {
        var go = new GameObject("Fireflies");
        go.transform.SetParent(root.transform, false);
        go.transform.position = new Vector3(-2f, 1.4f, 0f);
        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.loop = true;
        main.prewarm = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 9f);
        main.startSpeed = 0.05f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.07f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.95f, 0.45f), new Color(0.7f, 1f, 0.4f));
        main.maxParticles = 120;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;

        var emission = ps.emission;
        emission.rateOverTime = 12f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(16f, 2.2f, 14f);

        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.35f;
        noise.frequency = 0.4f;
        noise.scrollSpeed = 0.2f;

        // Each one blinks on and off over its life.
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0.2f, 0.35f), new GradientAlphaKey(1f, 0.55f), new GradientAlphaKey(0.9f, 0.8f), new GradientAlphaKey(0f, 1f) });
        col.color = g;

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        var material = LoadOrCreateMaterial("HexeFirefly.mat", Shader.Find("Legacy Shaders/Particles/Additive"));
        material.mainTexture = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
        material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f) * 1.6f);
        EditorUtility.SetDirty(material);
        renderer.sharedMaterial = material;
    }

    private static void SetUpSky()
    {
        var sky = LoadOrCreateMaterial("HexeNightSky.mat", Shader.Find("Hexe/NightSky"));
        RenderSettings.skybox = sky;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.42f, 0.4f, 0.58f);
        RenderSettings.ambientEquatorColor = new Color(0.36f, 0.3f, 0.36f);
        RenderSettings.ambientGroundColor = new Color(0.14f, 0.13f, 0.11f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = new Color(0.2f, 0.17f, 0.3f);
        RenderSettings.fogDensity = 0.034f;

        var camera = Camera.main;
        if (camera != null)
        {
            camera.clearFlags = CameraClearFlags.Skybox;
            // The scene's camera only had to see the avatar before; now it sees the forest.
            camera.farClipPlane = Mathf.Max(camera.farClipPlane, 150f);
            EditorUtility.SetDirty(camera);
        }
    }

    // ================================================================== assets

    private static Material LoadOrCreateMaterial(string file, Shader shader)
    {
        var path = Folder + "/" + file;
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
        }
        return material;
    }

    private static Mesh Save(Mesh mesh)
    {
        var path = Generated + "/" + mesh.name + ".asset";
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null)
        {
            EditorUtility.CopySerialized(mesh, existing);
            Object.DestroyImmediate(mesh);
            return existing;
        }
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    private static void AddMesh(GameObject root, string name, Mesh mesh, Material material, bool castShadows)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        go.isStatic = true;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = material;
        r.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        r.receiveShadows = true;
    }
}

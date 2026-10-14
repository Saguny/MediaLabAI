using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Builds the avatar's body animation set from hexe_body_poses.json: humanoid clips written
// as muscle curves, arm masks, and an Animator Controller with a stance blend tree and
// gesture layers (see AvatarPerformer for how they are driven).
//
// Poses are data rather than hand-made .anim files so they can be tuned without an
// animation tool, and so they retarget to any humanoid avatar. They are placeholders in
// spirit: any state's motion can be swapped for mocap later without touching code, as long
// as the state names stay.
public static class HexeBodyAnimationBuilder
{
    public const string Folder = "Assets/HexeRolePlay/Animations";
    public const string PosesFile = "Assets/HexeRolePlay/Editor/hexe_body_poses.json";
    public const string ControllerPath = Folder + "/HexeAvatar.controller";
    public const string PreviewFolder = "Temp/HexePosePreviews";

    [MenuItem("Tools/Hexe Role-Play/Build Animator")]
    public static void BuildMenu()
    {
        var animator = FindAvatar();
        if (animator == null) return;
        Build(animator);
    }

    public static AnimatorController Build(Animator animator)
    {
        var data = JObject.Parse(File.ReadAllText(PosesFile));
        var poses = ResolvePoses((JObject)data["poses"]);
        Directory.CreateDirectory(Folder);

        var rest = RestBodyPose(animator);
        var clips = new Dictionary<string, AnimationClip>();
        foreach (var kv in (JObject)data["clips"])
        {
            clips[kv.Key] = LoadMocap(kv.Key) ?? BuildClip(kv.Key, (JObject)kv.Value, poses, rest);
        }

        var masks = new Dictionary<string, AvatarMask>
        {
            { "RightHand", BuildMask("RightHand", AvatarMaskBodyPart.RightArm, AvatarMaskBodyPart.RightFingers) },
            { "LeftHand", BuildMask("LeftHand", AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.LeftFingers) },
            { "BothHands", BuildMask("BothHands", AvatarMaskBodyPart.RightArm, AvatarMaskBodyPart.RightFingers, AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.LeftFingers) },
        };

        AssetDatabase.DeleteAsset(ControllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        controller.AddParameter("Witch", AnimatorControllerParameterType.Float);
        // ModelController drives this integer for its idle system; declared so it has a target.
        controller.AddParameter("BaseParam", AnimatorControllerParameterType.Int);

        var baseMachine = controller.layers[0].stateMachine;
        var stance = controller.CreateBlendTreeInController("Stance", out var tree, 0);
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Witch";
        tree.useAutomaticThresholds = false;
        tree.AddChild(clips["CoachIdle"], 0f);
        tree.AddChild(clips["WitchIdle"], 1f);
        baseMachine.defaultState = stance;

        foreach (var layerName in new[] { "RightHand", "LeftHand", "BothHands" })
        {
            controller.AddLayer(layerName);
            var layers = controller.layers;
            var index = layers.Length - 1;
            var layer = layers[index];
            layer.defaultWeight = 0f;
            layer.avatarMask = masks[layerName];
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            layers[index] = layer;
            controller.layers = layers;

            var machine = controller.layers[index].stateMachine;
            // ModelController cross-fades every upper layer to a state named "Default".
            var idle = machine.AddState("Default");
            machine.defaultState = idle;

            foreach (var kv in (JObject)data["clips"])
            {
                if ((string)kv.Value["layer"] != layerName) continue;
                var state = machine.AddState(kv.Key);
                state.motion = clips[kv.Key];
                var back = state.AddTransition(idle);
                back.hasExitTime = true;
                back.exitTime = 0.97f;
                back.duration = 0.1f;
            }
        }

        AssetDatabase.SaveAssets();

        Undo.RecordObject(animator, "Assign Hexe animator");
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        EditorUtility.SetDirty(animator);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(animator.gameObject.scene);

        Debug.Log($"HexeBodyAnimationBuilder: built {clips.Count} clips and {ControllerPath}, assigned to {animator.name}.");
        return controller;
    }

    // Motion-capture override: Animations/Mocap/<ClipName>.fbx (e.g. Wave.fbx) replaces the
    // keyframed clip of the same name. It must be a Humanoid rig or it will not retarget, so
    // the importer is forced to Humanoid. The state keeps its name, so nothing else changes.
    private static AnimationClip LoadMocap(string name)
    {
        var path = $"{Folder}/Mocap/{name}.fbx";
        if (!File.Exists(path)) return null;
        var importer = (ModelImporter)AssetImporter.GetAtPath(path);
        if (importer.animationType != ModelImporterAnimationType.Human)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.SaveAndReimport();
        }
        Debug.Log($"HexeBodyAnimationBuilder: using mocap for '{name}'.");
        return AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));
    }

    // ------------------------------------------------------------------ poses

    // Poses may name a "base" pose and override some muscles; flatten that inheritance.
    public static Dictionary<string, Dictionary<string, float>> ResolvePoses(JObject raw)
    {
        var resolved = new Dictionary<string, Dictionary<string, float>>();
        Dictionary<string, float> Resolve(string name, int depth)
        {
            if (resolved.TryGetValue(name, out var done)) return done;
            if (depth > 16 || raw[name] == null) throw new Exception($"Pose '{name}' is missing or its bases loop.");
            var obj = (JObject)raw[name];
            var result = obj["base"] != null ? new Dictionary<string, float>(Resolve((string)obj["base"], depth + 1)) : new Dictionary<string, float>();
            foreach (var p in obj.Properties())
            {
                if (p.Name == "base" || p.Name.StartsWith("_")) continue;
                result[p.Name] = (float)p.Value;
            }
            resolved[name] = result;
            return result;
        }
        foreach (var p in raw.Properties()) if (!p.Name.StartsWith("_")) Resolve(p.Name, 0);
        return resolved;
    }

    private struct RestPose
    {
        public Vector3 BodyPosition;
        public Quaternion BodyRotation;
    }

    private static RestPose RestBodyPose(Animator animator)
    {
        var handler = new HumanPoseHandler(animator.avatar, animator.transform);
        var pose = new HumanPose();
        handler.GetHumanPose(ref pose);
        handler.Dispose();
        // Upright and centred; only the height is taken from the avatar.
        return new RestPose { BodyPosition = new Vector3(0f, pose.bodyPosition.y, 0f), BodyRotation = Quaternion.identity };
    }

    private static AnimationClip BuildClip(string name, JObject spec, Dictionary<string, Dictionary<string, float>> poses, RestPose rest)
    {
        var clip = new AnimationClip { name = name, frameRate = 30 };
        var keys = ((JArray)spec["keys"]).Select(k => (time: (float)k[0], pose: (string)k[1])).ToList();
        foreach (var k in keys)
        {
            if (!poses.ContainsKey(k.pose)) throw new Exception($"Clip '{name}' uses unknown pose '{k.pose}'.");
        }

        var muscles = keys.SelectMany(k => poses[k.pose].Keys).Where(m => !m.StartsWith("Root")).Distinct().ToList();
        foreach (var muscle in muscles)
        {
            if (Array.IndexOf(HumanTrait.MuscleName, muscle) < 0)
            {
                Debug.LogWarning($"HexeBodyAnimationBuilder: '{muscle}' is not a humanoid muscle (clip {name}).");
                continue;
            }
            var curve = new AnimationCurve(keys.Select(k => new Keyframe(k.time, poses[k.pose].TryGetValue(muscle, out var v) ? v : 0f)).ToArray());
            for (var i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0f);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), ClipAttribute(muscle)), curve);
        }

        // Root: fixed in place, with an optional per-pose height offset (a crouch or hunch).
        var drop = keys.Select(k => poses[k.pose].TryGetValue("RootDrop", out var d) ? d : 0f).ToArray();
        void Root(string attr, Func<int, float> value)
        {
            var c = new AnimationCurve(keys.Select((k, i) => new Keyframe(k.time, value(i))).ToArray());
            for (var i = 0; i < c.length; i++) c.SmoothTangents(i, 0f);
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), attr), c);
        }
        Root("RootT.x", i => rest.BodyPosition.x);
        Root("RootT.y", i => rest.BodyPosition.y - drop[i]);
        Root("RootT.z", i => rest.BodyPosition.z);
        Root("RootQ.x", i => rest.BodyRotation.x);
        Root("RootQ.y", i => rest.BodyRotation.y);
        Root("RootQ.z", i => rest.BodyRotation.z);
        Root("RootQ.w", i => rest.BodyRotation.w);

        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = spec["loop"] != null && (bool)spec["loop"];
        settings.keepOriginalOrientation = true;
        settings.keepOriginalPositionY = true;
        settings.keepOriginalPositionXZ = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        var path = $"{Folder}/{name}.anim";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(clip, path);
        return clip;
    }

    // HumanTrait names fingers "Left Index 1 Stretched"; clip curves name them
    // "LeftHand.Index.1 Stretched". Body muscles are identical in both.
    public static string ClipAttribute(string muscle)
    {
        var parts = muscle.Split(' ');
        var fingers = new[] { "Thumb", "Index", "Middle", "Ring", "Little" };
        if (parts.Length >= 3 && (parts[0] == "Left" || parts[0] == "Right") && fingers.Contains(parts[1]))
        {
            return $"{parts[0]}Hand.{parts[1]}.{string.Join(" ", parts.Skip(2))}";
        }
        return muscle;
    }

    private static AvatarMask BuildMask(string name, params AvatarMaskBodyPart[] enabledParts)
    {
        var mask = new AvatarMask();
        for (var i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
        {
            var part = (AvatarMaskBodyPart)i;
            mask.SetHumanoidBodyPartActive(part, enabledParts.Contains(part));
        }
        var path = $"{Folder}/{name}.mask";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mask, path);
        return mask;
    }

    // ------------------------------------------------------------------ preview

    // Renders every pose (front and side) to PNGs in Temp/HexePosePreviews, applying the
    // muscles in edit mode and restoring the skeleton afterwards. For checking poses
    // without entering play mode.
    [MenuItem("Tools/Hexe Role-Play/Debug/Render Pose Previews")]
    public static void RenderPosePreviews()
    {
        var animator = FindAvatar();
        if (animator == null) return;
        var data = JObject.Parse(File.ReadAllText(PosesFile));
        var poses = ResolvePoses((JObject)data["poses"]);
        var only = data["_previewOnly"] as JArray;
        Directory.CreateDirectory(PreviewFolder);

        var bones = animator.GetComponentsInChildren<Transform>(true);
        var saved = bones.Select(b => (b, b.localPosition, b.localRotation, b.localScale)).ToList();
        var handler = new HumanPoseHandler(animator.avatar, animator.transform);
        var pose = new HumanPose();
        handler.GetHumanPose(ref pose);
        var restBody = pose.bodyPosition;
        var restRot = pose.bodyRotation;

        var camGo = new GameObject("HexePreviewCamera") { hideFlags = HideFlags.HideAndDontSave };
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.25f, 0.27f, 0.3f);
        cam.fieldOfView = 30f;
        var rt = new RenderTexture(512, 640, 24);
        cam.targetTexture = rt;
        var tex = new Texture2D(512, 640, TextureFormat.RGB24, false);

        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        var root = animator.transform;
        var count = 0;

        // Several renders happen within one editor frame; without this the skinned meshes
        // keep the pose they were skinned with at the start of the frame.
        var skinned = animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var forced = skinned.Select(s => s.forceMatrixRecalculationPerRender).ToArray();
        foreach (var s in skinned) s.forceMatrixRecalculationPerRender = true;

        try
        {
            foreach (var kv in poses)
            {
                if (only != null && only.Count > 0 && !only.Any(t => (string)t == kv.Key)) continue;
                for (var m = 0; m < pose.muscles.Length; m++) pose.muscles[m] = 0f;
                foreach (var mv in kv.Value)
                {
                    var idx = Array.IndexOf(HumanTrait.MuscleName, mv.Key);
                    if (idx >= 0) pose.muscles[idx] = mv.Value;
                }
                pose.bodyPosition = restBody;
                pose.bodyRotation = restRot;
                handler.SetHumanPose(ref pose);

                var center = Vector3.Lerp(hips.position, head.position, 0.55f);
                var span = Vector3.Distance(hips.position, head.position);
                foreach (var view in new[] { ("front", root.forward), ("side", root.right) })
                {
                    cam.transform.position = center + view.Item2 * span * 3.6f;
                    cam.transform.LookAt(center);
                    cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, 512, 640), 0, 0);
                    tex.Apply();
                    RenderTexture.active = null;
                    File.WriteAllBytes($"{PreviewFolder}/{kv.Key}_{view.Item1}.png", tex.EncodeToPNG());
                }
                count++;
            }
        }
        finally
        {
            foreach (var s in saved)
            {
                if (s.b == null) continue;
                s.b.localPosition = s.localPosition;
                s.b.localRotation = s.localRotation;
                s.b.localScale = s.localScale;
            }
            for (var i = 0; i < skinned.Length; i++) if (skinned[i] != null) skinned[i].forceMatrixRecalculationPerRender = forced[i];
            handler.Dispose();
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(camGo);
        }
        Debug.Log($"HexeBodyAnimationBuilder: rendered {count} poses to {Path.GetFullPath(PreviewFolder)}");
    }

    [MenuItem("Tools/Hexe Role-Play/Debug/Log Muscle Ranges")]
    public static void LogMuscles()
    {
        var lines = new List<string>();
        for (var i = 0; i < HumanTrait.MuscleCount; i++)
        {
            lines.Add($"{i}\t{HumanTrait.MuscleName[i]}\t{HumanTrait.GetMuscleDefaultMin(i):F0}..{HumanTrait.GetMuscleDefaultMax(i):F0}");
        }
        File.WriteAllLines($"{PreviewFolder}_muscles.txt", lines);
        Debug.Log($"HexeBodyAnimationBuilder: wrote {Path.GetFullPath(PreviewFolder + "_muscles.txt")}");
    }

    public static Animator FindAvatar()
    {
        var chatdoll = UnityEngine.Object.FindObjectOfType<ChatdollKit.Model.ModelController>();
        var animator = chatdoll != null && chatdoll.AvatarModel != null ? chatdoll.AvatarModel.GetComponent<Animator>() : null;
        if (animator == null || !animator.isHuman)
        {
            Debug.LogError("HexeBodyAnimationBuilder: no humanoid avatar found via ModelController.AvatarModel.");
            return null;
        }
        return animator;
    }
}

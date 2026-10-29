using System.Linq;
using ChatdollKit.Dialog;
using ChatdollKit.Extension.OpenAI;
using ChatdollKit.IO;
using ChatdollKit.Model;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// One-click wiring of the role-play into the open ChatdollKit scene. Safe to run again:
// existing components are reused and only references are refreshed.
public static class HexeSceneSetup
{
    // The avatar that plays the visitor's part in the replay film.
    public const string HanselPrefabPath = "Assets/Milltina_kisekae(Clone).prefab";

    [MenuItem("Tools/Hexe Role-Play/Set Up Scene")]
    public static void SetUp()
    {
        var chatdoll = Object.FindObjectOfType<ChatdollKit.ChatdollKit>();
        if (chatdoll == null)
        {
            Debug.LogError("HexeSceneSetup: no ChatdollKit in the open scene.");
            return;
        }
        var model = chatdoll.GetComponent<ModelController>();
        var animator = model != null && model.AvatarModel != null ? model.AvatarModel.GetComponent<Animator>() : null;
        if (animator == null || !animator.isHuman)
        {
            Debug.LogError("HexeSceneSetup: ModelController.AvatarModel needs a humanoid Animator.");
            return;
        }

        if (animator.runtimeAnimatorController == null || AssetDatabase.GetAssetPath(animator.runtimeAnimatorController) != HexeBodyAnimationBuilder.ControllerPath)
        {
            HexeBodyAnimationBuilder.Build(animator);
        }

        var said = chatdoll.GetComponent<SaidBlendshapePlayer>();
        var face = said != null && said.FaceRenderer != null
            ? said.FaceRenderer
            : animator.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).OrderByDescending(r => r.sharedMesh.blendShapeCount).FirstOrDefault();

        var faceLayer = Ensure<FaceExpressionLayer>(animator.gameObject);
        faceLayer.FaceRenderer = face;

        var root = GameObject.Find("HexeRolePlay");
        if (root == null)
        {
            root = new GameObject("HexeRolePlay");
            Undo.RegisterCreatedObjectUndo(root, "Create Hexe role-play");
        }

        var capture = Ensure<SpeechCapture>(root);
        var recorder = Ensure<SceneRecorder>(root);
        var stage = Ensure<StageEffects>(root);
        var ui = Ensure<HexeStageUI>(root);
        var performer = Ensure<AvatarPerformer>(root);
        var director = Ensure<HexeDirector>(root);
        var replay = Ensure<CinematicReplay>(root);
        var hansel = EnsureHansel(root);
        var tracking = Object.FindObjectOfType<LiveFaceTracking>(true);

        capture.Microphone = chatdoll.GetComponent<ChatdollMicrophone>();

        stage.KeyLight = Object.FindObjectsOfType<Light>().FirstOrDefault(l => l.type == LightType.Directional);
        stage.StageCamera = Camera.main;
        stage.Avatar = animator;
        ui.Stage = stage;

        performer.ModelController = model;
        performer.Animator = animator;
        performer.Face = faceLayer;
        performer.Stage = stage;
        performer.UI = ui;
        var loader = chatdoll.GetComponent<OpenAITTSLoader>();
        if (loader != null && string.IsNullOrEmpty(performer.CoachVoice)) performer.CoachVoice = loader.Voice;

        director.Chatdoll = chatdoll;
        director.Dialog = chatdoll.GetComponent<DialogController>();
        director.Model = model;
        director.Microphone = capture.Microphone;
        director.Performer = performer;
        director.Capture = capture;
        director.Recorder = recorder;
        director.UI = ui;
        director.Stage = stage;
        director.Replay = replay;
        director.FaceTracking = tracking;

        recorder.Witch = animator;
        recorder.WitchFace = face;
        recorder.VisitorTracking = tracking;

        replay.Recorder = recorder;
        replay.Performer = performer;
        replay.Stage = stage;
        replay.Hansel = hansel;
        replay.SourceCamera = Camera.main;

        foreach (var c in new Component[] { faceLayer, capture, recorder, stage, ui, performer, director, replay }) EditorUtility.SetDirty(c);
        if (hansel != null) EditorUtility.SetDirty(hansel);
        HexeForestBuilder.EnsureBuilt();

        EditorSceneManager.MarkSceneDirty(root.scene);
        EditorSceneManager.SaveScene(root.scene);
        Selection.activeGameObject = root;
        Debug.Log("HexeSceneSetup: role-play wired and scene saved. Press Play, then Space (or say the wake word) to start.");
    }

    // A second avatar under the role-play root, hidden until the film. It gets the same body
    // controller as the coach (upright stance, gestures), which retargets to any humanoid.
    private static HanselActor EnsureHansel(GameObject root)
    {
        var existing = root.GetComponentInChildren<HanselActor>(true);
        if (existing != null)
        {
            StripMissingScripts(existing.gameObject);
            AssignBodyController(existing.Avatar);
            return existing;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HanselPrefabPath);
        if (prefab == null)
        {
            Debug.LogWarning($"HexeSceneSetup: no avatar at {HanselPrefabPath} - the replay film will show the witch alone.");
            return null;
        }
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
        instance.name = "Hansel";
        Undo.RegisterCreatedObjectUndo(instance, "Create Hansel");
        StripMissingScripts(instance);
        var animator = instance.GetComponentInChildren<Animator>(true);
        AssignBodyController(animator);
        var actor = Undo.AddComponent<HanselActor>(instance);
        actor.Avatar = animator;
        instance.SetActive(false);
        return actor;
    }

    [MenuItem("Tools/Hexe Role-Play/Test Film (Play mode)")]
    public static void TestFilm()
    {
        var director = Object.FindObjectOfType<HexeDirector>();
        if (!Application.isPlaying || director == null)
        {
            Debug.LogWarning("HexeSceneSetup: enter Play mode first.");
            return;
        }
        director.TestFilm();
    }

    // VRChat avatars carry components that only exist in the VRChat SDK (PhysBones, contacts,
    // SPS sockets). Here they are dead weight, and activating the avatar logs each one as an
    // error - which stops Play mode when Error Pause is on. The copy is unpacked so they can go.
    private static void StripMissingScripts(GameObject avatar)
    {
        if (PrefabUtility.IsPartOfPrefabInstance(avatar))
        {
            PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(avatar), PrefabUnpackMode.Completely, InteractionMode.UserAction);
        }
        var removed = 0;
        foreach (var t in avatar.GetComponentsInChildren<Transform>(true))
        {
            removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
        }
        if (removed > 0) Debug.Log($"HexeSceneSetup: removed {removed} VRChat-only components from {avatar.name}.");
    }

    // Build Animator recreates the controller asset, so the reference is refreshed every run.
    private static void AssignBodyController(Animator animator)
    {
        var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(HexeBodyAnimationBuilder.ControllerPath);
        if (animator == null || controller == null) return;
        Undo.RecordObject(animator, "Assign Hexe animator");
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        EditorUtility.SetDirty(animator);
    }

    private static T Ensure<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        return c != null ? c : Undo.AddComponent<T>(go);
    }
}

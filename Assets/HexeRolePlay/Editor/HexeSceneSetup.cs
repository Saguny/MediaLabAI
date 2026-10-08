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

        foreach (var c in new Component[] { faceLayer, capture, recorder, stage, ui, performer, director }) EditorUtility.SetDirty(c);
        EditorSceneManager.MarkSceneDirty(root.scene);
        EditorSceneManager.SaveScene(root.scene);
        Selection.activeGameObject = root;
        Debug.Log("HexeSceneSetup: role-play wired and scene saved. Press Play, then Space (or say the wake word) to start.");
    }

    private static T Ensure<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        return c != null ? c : Undo.AddComponent<T>(go);
    }
}

using UnityEditor;
using UnityEngine;

public static class WireSaidPlayer
{
    // Name of the avatar root to drive. Everything else is discovered from it.
    private const string AvatarRootName = "Lapwing";
    private const string MappingFile = "arkit_to_lapwing.json";

    [MenuItem("Tools/Wire Face Pipeline")]
    public static void Wire()
    {
        var chatdoll = GameObject.Find("ChatdollKit");
        if (chatdoll == null)
        {
            Debug.LogError("WireFacePipeline: no GameObject named 'ChatdollKit' in the scene.");
            return;
        }

        var avatar = GameObject.Find(AvatarRootName);
        if (avatar == null)
        {
            Debug.LogError($"WireFacePipeline: no GameObject named '{AvatarRootName}' in the scene.");
            return;
        }

        // Face mesh = the SkinnedMeshRenderer with the most blend shapes, the same
        // heuristic ChatdollKit's AvatarUtility uses.
        SkinnedMeshRenderer face = null;
        var best = 0;
        foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh == null) continue;
            if (smr.sharedMesh.blendShapeCount > best)
            {
                best = smr.sharedMesh.blendShapeCount;
                face = smr;
            }
        }

        if (face == null)
        {
            Debug.LogError($"WireFacePipeline: no SkinnedMeshRenderer with blend shapes under '{AvatarRootName}'.");
            return;
        }

        var audio = chatdoll.GetComponent<AudioSource>();

        // Point ChatdollKit's own model plumbing at the new avatar.
        var modelController = chatdoll.GetComponent<ChatdollKit.Model.ModelController>();
        if (modelController != null)
        {
            modelController.AvatarModel = avatar;
            modelController.SkinnedMeshRenderer = face;
            EditorUtility.SetDirty(modelController);
        }

        // Speech-driven mouth.
        var player = chatdoll.GetComponent<SaidBlendshapePlayer>();
        if (player == null) player = chatdoll.AddComponent<SaidBlendshapePlayer>();
        player.FaceRenderer = face;
        player.VoiceAudioSource = audio;
        player.MappingFileName = MappingFile;
        EditorUtility.SetDirty(player);

        // Tracker-style eyes and brows, on the avatar so it sits with the face mesh.
        var eyes = avatar.GetComponent<ArkitFaceAnimator>();
        if (eyes == null) eyes = avatar.AddComponent<ArkitFaceAnimator>();
        eyes.FaceRenderer = face;
        if (eyes.LookTarget == null && Camera.main != null) eyes.LookTarget = Camera.main.transform;
        EditorUtility.SetDirty(eyes);

        // Head sway, also on the avatar (needs the Humanoid head bone).
        var head = avatar.GetComponent<NaturalHeadMotion>();
        if (head == null) head = avatar.AddComponent<NaturalHeadMotion>();
        head.VoiceAudioSource = audio;
        if (head.LookTarget == null && Camera.main != null) head.LookTarget = Camera.main.transform;
        EditorUtility.SetDirty(head);

        // Breathing, so she is never completely still between utterances.
        var breath = avatar.GetComponent<BreathingMotion>();
        if (breath == null) breath = avatar.AddComponent<BreathingMotion>();
        EditorUtility.SetDirty(breath);

        // Live webcam puppeteering. Disabled by default: it overrides the generated
        // animation, so it is switched on for the demo rather than running always.
        var live = avatar.GetComponent<LiveFaceTracking>();
        if (live == null)
        {
            live = avatar.AddComponent<LiveFaceTracking>();
            live.enabled = false;
        }
        live.FaceRenderer = face;
        EditorUtility.SetDirty(live);

        // Listener feedback and orienting. Lives on the avatar next to the head/face layers
        // that consume its output.
        var social = avatar.GetComponent<SocialReactions>();
        if (social == null) social = avatar.AddComponent<SocialReactions>();
        social.Microphone = Object.FindObjectOfType<ChatdollKit.IO.ChatdollMicrophone>();
        if (social.LookTarget == null && Camera.main != null) social.LookTarget = Camera.main.transform;
        EditorUtility.SetDirty(social);

        // Retire the Milltina-era systems. ArkitFaceAnimator now owns eyes and blinking,
        // and ChatdollKit's own Blink/uLipSync would write the same shapes underneath it.
        DisableEverywhere<NaturalGazeController>();
        DisableEverywhere<ExpressiveEyes>();

        var ckBlink = chatdoll.GetComponent<ChatdollKit.Model.Blink>();
        if (ckBlink != null) { ckBlink.enabled = false; EditorUtility.SetDirty(ckBlink); }

        // The preset-expression proxy holds a FaceClipConfiguration captured on the previous
        // avatar; its stored indices address a different mesh entirely. Point it at the new
        // face and drop the stale clips - presets are unused now anyway.
        var proxy = chatdoll.GetComponent<ChatdollKit.Model.VRCFaceExpressionProxy>();
        if (proxy != null)
        {
            proxy.SkinnedMeshRenderer = face;
            proxy.FaceClipConfiguration = null;
            EditorUtility.SetDirty(proxy);
        }

        var uls = chatdoll.GetComponent<uLipSync.uLipSync>();
        if (uls != null) { uls.enabled = false; EditorUtility.SetDirty(uls); }
        var ulsbs = chatdoll.GetComponent<uLipSync.uLipSyncBlendShape>();
        if (ulsbs != null) { ulsbs.enabled = false; EditorUtility.SetDirty(ulsbs); }

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(chatdoll.scene);

        Debug.Log(
            $"WireFacePipeline: avatar={avatar.name} face={face.name} ({best} shapes) " +
            $"audio={(audio != null ? "set" : "NULL")} mapping={MappingFile}");
    }

    private static void DisableEverywhere<T>() where T : MonoBehaviour
    {
        foreach (var c in Object.FindObjectsOfType<T>(true))
        {
            c.enabled = false;
            EditorUtility.SetDirty(c);
        }
    }
}

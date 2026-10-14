using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Keeps a machine you are not sitting at in step with GitHub: a fix pushed from anywhere
// lands there within a minute. Turn it on per machine with
// Tools > Hexe Role-Play > Auto-Update from GitHub (kept in EditorPrefs, so the dev machine
// stays manual).
//
// Every minute it fetches in the background. When the branch's upstream has new commits:
//   - in Edit mode it fast-forwards and reimports right away;
//   - in Play mode it waits until no session is running, leaves Play mode, updates, and
//     presses Play again - scripts and the scenario JSON are only read on Play anyway.
// Local edits on this machine are stashed around the update. If one clashes with it, the
// GitHub version wins and the local edit stays in `git stash list`.
[InitializeOnLoad]
public static class HexeAutoUpdate
{
    private const string MenuPath = "Tools/Hexe Role-Play/Auto-Update from GitHub";
    private const string EnabledKey = "HexeAutoUpdate.Enabled";
    private const string ReplayKey = "HexeAutoUpdate.Replay";
    private const double IntervalSec = 60;

    private static readonly string Root = Path.GetDirectoryName(Application.dataPath);
    private static double nextCheck;
    private static Task<bool> check;   // background fetch; true when upstream is ahead
    private static bool pending;

    static HexeAutoUpdate()
    {
        EditorApplication.update += Tick;
    }

    private static bool Enabled
    {
        get => EditorPrefs.GetBool(EnabledKey, false);
        set => EditorPrefs.SetBool(EnabledKey, value);
    }

    [MenuItem(MenuPath)]
    private static void Toggle()
    {
        Enabled = !Enabled;
        nextCheck = 0;
        Debug.Log(Enabled ? "Hexe auto-update on: checking GitHub every minute." : "Hexe auto-update off.");
    }

    [MenuItem(MenuPath, true)]
    private static bool ToggleValidate()
    {
        Menu.SetChecked(MenuPath, Enabled);
        return true;
    }

    private static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

        if (check != null && check.IsCompleted)
        {
            pending = check.Status == TaskStatus.RanToCompletion && check.Result;
            check = null;
        }

        if (pending)
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                pending = false;
                Apply();
            }
            else if (EditorApplication.isPlaying && !UnityEngine.Object.FindObjectsOfType<HexeDirector>().Any(d => d.IsRunning))
            {
                SessionState.SetBool(ReplayKey, true);
                EditorApplication.isPlaying = false;
            }
            return;
        }

        // Back from an update that left Play mode (and possibly recompiled): press Play again.
        if (SessionState.GetBool(ReplayKey, false) && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            SessionState.SetBool(ReplayKey, false);
            EditorApplication.isPlaying = true;
            return;
        }

        if (!Enabled || check != null || EditorApplication.timeSinceStartup < nextCheck) return;
        nextCheck = EditorApplication.timeSinceStartup + IntervalSec;
        check = Task.Run(() =>
        {
            if (Git("fetch --quiet").code != 0) return false;   // offline: try again next minute
            var behind = Git("rev-list --count HEAD..@{u}");
            return behind.code == 0 && behind.output.Trim() != "0";
        });
    }

    private static void Apply()
    {
        var before = Git("rev-parse HEAD").output.Trim();
        var merge = Git("merge --ff-only --autostash @{u}");
        if (merge.code != 0)
        {
            Debug.LogError($"Hexe auto-update: could not fast-forward (local commits on this machine?):\n{merge.output}");
            return;
        }

        // A clashing autostash leaves conflict markers behind but still exits 0.
        var clashes = Git("diff --name-only --diff-filter=U").output.Trim();
        if (clashes.Length > 0)
        {
            Git("reset --hard --quiet");
            Debug.LogWarning($"Hexe auto-update: local edits clashed with the update and were replaced (they are kept in `git stash list`):\n{clashes}");
        }

        var changed = Git($"diff --name-only {before} HEAD").output;
        Debug.Log($"Hexe auto-update: updated to {Git("log -1 --format=\"%h %s\"").output.Trim()}\n{changed}");
        AssetDatabase.Refresh();

        var scene = EditorSceneManager.GetActiveScene().path;
        if (scene.Length > 0 && changed.Contains(scene)) EditorSceneManager.OpenScene(scene);
    }

    private static (int code, string output) Git(string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Nobody is there to answer a credential prompt; fail instead of hanging.
        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
        try
        {
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, p.ExitCode == 0 ? output : output + err.Result);
            }
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}

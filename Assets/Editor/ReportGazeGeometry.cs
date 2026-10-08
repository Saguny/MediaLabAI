using UnityEditor;
using UnityEngine;

public static class ReportGazeGeometry
{
    // Prints the actual world-space geometry the gaze solver works from, so the angles can
    // be checked against reality instead of assumed. Run in play mode.
    [MenuItem("Tools/Report Gaze Geometry")]
    public static void Report()
    {
        var animator = Object.FindObjectOfType<ArkitFaceAnimator>();
        if (animator == null)
        {
            Debug.LogError("ReportGazeGeometry: no ArkitFaceAnimator in the scene.");
            return;
        }

        var cams = Object.FindObjectsOfType<Camera>();
        Debug.Log($"--- cameras in scene: {cams.Length} ---");
        foreach (var c in cams)
        {
            Debug.Log($"  '{c.name}' tag={c.tag} enabled={c.enabled} pos={c.transform.position} depth={c.depth}" +
                      (Camera.main == c ? "   <- Camera.main" : ""));
        }

        var root = animator.transform;
        var eyeL = animator.EyeBoneL;
        var eyeR = animator.EyeBoneR;
        var head = animator.HeadBone;
        var target = animator.LookTarget;

        Debug.Log($"--- avatar ---");
        Debug.Log($"  root '{root.name}' pos={root.position} rot={root.rotation.eulerAngles} scale={root.lossyScale}");
        Debug.Log($"  head  = {(head != null ? head.name + " pos=" + head.position : "NULL")}");
        Debug.Log($"  eyeL  = {(eyeL != null ? eyeL.name + " pos=" + eyeL.position : "NULL")}");
        Debug.Log($"  eyeR  = {(eyeR != null ? eyeR.name + " pos=" + eyeR.position : "NULL")}");
        Debug.Log($"  LookTarget = {(target != null ? target.name + " pos=" + target.position : "NULL")}");

        if (eyeL == null || target == null) return;

        var origin = eyeL.position;
        var dir = target.position - origin;
        var local = root.InverseTransformDirection(dir);
        var horizontal = new Vector2(local.x, local.z).magnitude;
        var yaw = Mathf.Atan2(local.x, Mathf.Max(0.001f, local.z)) * Mathf.Rad2Deg;
        var pitch = Mathf.Atan2(local.y, Mathf.Max(0.001f, horizontal)) * Mathf.Rad2Deg;

        Debug.Log($"--- solved gaze (eye -> target, in avatar frame) ---");
        Debug.Log($"  eye world height   = {origin.y:F3}");
        Debug.Log($"  target world height= {target.position.y:F3}   (delta {target.position.y - origin.y:+0.000;-0.000})");
        Debug.Log($"  distance           = {dir.magnitude:F3} m");
        Debug.Log($"  local dir          = {local}");
        Debug.Log($"  yaw   = {yaw:F2} deg  (clamped to +-{animator.GazeMaxYawDegrees})");
        Debug.Log($"  pitch = {pitch:F2} deg  (clamped to +-{animator.GazeMaxPitchDegrees})");
        Debug.Log($"  -> pitch is {(Mathf.Abs(pitch) > animator.GazeMaxPitchDegrees ? "SATURATING the clamp" : "within range")}");
    }
}

using UnityEngine;

// Analytic two-bone IK for an arm, in world space, whatever the rig's bone axes: the upper arm
// and forearm are turned (never stretched) so the wrist reaches the target, with the elbow
// bending towards the hint. Weight blends from the pose the arm already has to the solved one,
// so a reach can fade in and out on top of whatever the Animator or a recording did.
public static class TwoBoneIK
{
    public static void Solve(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 hint, float weight)
    {
        if (upper == null || lower == null || end == null || weight <= 0f) return;
        weight = Mathf.Clamp01(weight);

        var startUpper = upper.rotation;
        var startLower = lower.rotation;

        var a = upper.position;
        var la = Vector3.Distance(a, lower.position);
        var lb = Vector3.Distance(lower.position, end.position);
        if (la < 1e-4f || lb < 1e-4f) return;

        var toTarget = target - a;
        if (toTarget.sqrMagnitude < 1e-8f) return;
        var dir = toTarget.normalized;
        var dist = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(la - lb) + 1e-3f, (la + lb) * 0.999f);

        // The elbow sits on the circle the two bone lengths allow; the hint picks where on it.
        var bend = Vector3.ProjectOnPlane(hint - a, dir);
        if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(lower.position - a, dir);
        if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(Vector3.down, dir);
        bend.Normalize();
        var cos = Mathf.Clamp((la * la + dist * dist - lb * lb) / (2f * la * dist), -1f, 1f);
        var elbow = a + dir * (la * cos) + bend * (la * Mathf.Sqrt(1f - cos * cos));
        var wrist = a + dir * dist;

        upper.rotation = Quaternion.FromToRotation(lower.position - a, elbow - a) * upper.rotation;
        lower.rotation = Quaternion.FromToRotation(end.position - lower.position, wrist - lower.position) * lower.rotation;

        if (weight < 1f)
        {
            var solvedUpper = upper.rotation;
            var solvedLower = lower.rotation;
            upper.rotation = Quaternion.Slerp(startUpper, solvedUpper, weight);
            // The forearm's solved world rotation was relative to the solved upper arm; carry it
            // over to the blended one before blending it too.
            var lowerLocal = Quaternion.Inverse(solvedUpper) * solvedLower;
            var startLowerLocal = Quaternion.Inverse(startUpper) * startLower;
            lower.rotation = upper.rotation * Quaternion.Slerp(startLowerLocal, lowerLocal, weight);
        }
    }
}

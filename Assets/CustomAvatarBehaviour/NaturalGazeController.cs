using UnityEngine;

// Drives the Humanoid rig's eye bones to track a target (defaults to the main camera),
// with periodic brief look-aways so the gaze doesn't read as an unbroken robotic stare.
// Runs in LateUpdate so it overrides whatever pose the Animator produced this frame.
public class NaturalGazeController : MonoBehaviour
{
    [Header("Target")]
    public Transform LookTarget;

    [Header("Gaze Behaviour")]
    public float EyeTurnSpeed = 6f;
    public float MaxEyeAngle = 25f;
    public float LookAwayIntervalMin = 3f;
    public float LookAwayIntervalMax = 7f;
    public float LookAwayDurationMin = 0.4f;
    public float LookAwayDurationMax = 1.2f;

    private Animator animator;
    private Transform leftEye;
    private Transform rightEye;
    private float nextLookAwayAt;
    private bool isLookingAway;
    private float lookAwayUntil;
    private Vector3 lookAwayOffset;

    private void Start()
    {
        animator = GetComponent<Animator>();
        if (animator == null || !animator.isHuman)
        {
            enabled = false;
            return;
        }

        leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
        rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);

        // Eyes are optional Humanoid bones - Unity's auto-mapper frequently leaves them
        // unassigned even when the transforms exist, so fall back to a name search under Head.
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (leftEye == null && head != null)
        {
            leftEye = FindChildByName(head, "lefteye");
        }
        if (rightEye == null && head != null)
        {
            rightEye = FindChildByName(head, "righteye");
        }

        if (LookTarget == null && Camera.main != null)
        {
            LookTarget = Camera.main.transform;
        }

        ScheduleNextLookAway();
    }

    private void ScheduleNextLookAway()
    {
        nextLookAwayAt = Time.time + Random.Range(LookAwayIntervalMin, LookAwayIntervalMax);
    }

    private void LateUpdate()
    {
        if (LookTarget == null || (leftEye == null && rightEye == null))
        {
            return;
        }

        if (!isLookingAway && Time.time >= nextLookAwayAt)
        {
            isLookingAway = true;
            lookAwayUntil = Time.time + Random.Range(LookAwayDurationMin, LookAwayDurationMax);
            lookAwayOffset = Random.insideUnitSphere * 0.3f;
        }
        else if (isLookingAway && Time.time >= lookAwayUntil)
        {
            isLookingAway = false;
            ScheduleNextLookAway();
        }

        var targetPos = LookTarget.position;
        if (isLookingAway)
        {
            targetPos += lookAwayOffset;
        }

        ApplyEyeLook(leftEye, targetPos);
        ApplyEyeLook(rightEye, targetPos);
    }

    private void ApplyEyeLook(Transform eye, Vector3 targetPos)
    {
        if (eye == null)
        {
            return;
        }

        var toTarget = targetPos - eye.position;
        if (toTarget.sqrMagnitude < 0.0001f)
        {
            return;
        }

        var desiredRotation = Quaternion.LookRotation(toTarget, eye.parent.up);
        var localDesired = Quaternion.Inverse(eye.parent.rotation) * desiredRotation;
        var clamped = ClampRotation(localDesired, MaxEyeAngle);

        eye.rotation = Quaternion.Slerp(eye.rotation, eye.parent.rotation * clamped, Time.deltaTime * EyeTurnSpeed);
    }

    private Quaternion ClampRotation(Quaternion q, float maxAngle)
    {
        q.ToAngleAxis(out var angle, out var axis);
        if (angle > 180f)
        {
            angle -= 360f;
        }
        angle = Mathf.Clamp(angle, -maxAngle, maxAngle);
        return Quaternion.AngleAxis(angle, axis);
    }

    private static Transform FindChildByName(Transform root, string nameContainsLowerCase)
    {
        foreach (Transform child in root)
        {
            if (child.name.ToLowerInvariant().Contains(nameContainsLowerCase))
            {
                return child;
            }
            var found = FindChildByName(child, nameContainsLowerCase);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }
}

using System.Collections.Generic;
using UnityEngine;

// The named gestures (Wave, PointForward, ThinkChin...) as hand targets on her own body,
// solved by NaturalArmMotion's arm IK: the hand goes to her chin, her ear, her chest or the
// other hand, wherever those are on this avatar and however she stands. Muscle-curve clips
// can only approximate that, and on this rig ThinkChin swung the arm out across the body.
//
// Each gesture keeps the name and the timing of the clip it replaces (hexe_body_poses.json),
// so scripts and the director don't change. Its keys are where the hand is at that moment;
// before the first key the hand travels there from wherever it was, after the last it goes
// back, and in between it moves from key to key. The arm's own dynamics round all of it off.
//
// Coordinates are the body's, mirrored per arm: x outwards (away from the body's midline on
// that arm's side), y up the spine, z forwards. A key on the Shoulder is in arm lengths from
// that shoulder; on a landmark it is in metres from it, scaled to her size.
public static class ArmGestures
{
    public enum Shape { Relaxed, Open, Flat, Point, Think, Curled }
    public enum Mark { Shoulder, Chest, Chin, Ear }

    public class Key
    {
        public float T;
        public Vector3 Hand;
        public Vector3 Fingers, Palm, Pole;   // directions: where the fingers point, the palm faces, the elbow points
        public Shape Shape;
        public float Shoulder;                // degrees the shoulder lifts (a shrug)
        public float Alternate;               // metres up for the right hand and down for the left (rubbing)
    }

    public class Gesture
    {
        public bool BothHands;
        public Mark At;
        public float End;                     // the hand is back by then
        public Key[] Keys;
    }

    // One moment of a gesture, ready to blend in.
    public struct Sample
    {
        public float Weight;                  // how much of the arm the gesture has now
        public Vector3 Hand, Fingers, Palm, Pole;
        public Shape ShapeA, ShapeB;
        public float ShapeMix;
        public float Shoulder, Alternate;
    }

    // Where a body is this frame, for placing landmark keys.
    public struct Body
    {
        public Vector3 Up, Right, Forward;    // the body frame (Right is her right)
        public Vector3 LeftShoulder, RightShoulder;
        public Vector3 Eyes, HeadUp, HeadForward, HeadRight;
        public float Scale;                   // her size against an adult's (1 = arm of 0.53 m)
    }

    private static Key K(float t, Vector3 hand, Vector3 fingers, Vector3 palm, Vector3 pole, Shape shape, float shoulder = 0f, float alternate = 0f) =>
        new Key { T = t, Hand = hand, Fingers = fingers, Palm = palm, Pole = pole, Shape = shape, Shoulder = shoulder, Alternate = alternate };

    private static Key[] Repeat(float[] times, params System.Func<float, Key>[] keys)
    {
        var result = new Key[times.Length];
        for (var i = 0; i < times.Length; i++) result[i] = keys[i % keys.Length](times[i]);
        return result;
    }

    public static readonly Dictionary<string, Gesture> All = new Dictionary<string, Gesture>
    {
        // Hand up and out beside the head, palm to the visitor, swinging from the elbow (bent
        // about 105 degrees, as people hold a wave).
        ["Wave"] = new Gesture
        {
            At = Mark.Shoulder, End = 2.1f,
            Keys = Repeat(new[] { 0.45f, 0.7f, 0.95f, 1.2f, 1.45f },
                t => K(t, new Vector3(0.53f, 0.28f, 0.25f), new Vector3(0.55f, 1f, 0f), new Vector3(0f, 0f, 1f), new Vector3(1f, -0.7f, 0.1f), Shape.Open),
                t => K(t, new Vector3(0.47f, 0.28f, 0.25f), new Vector3(-0.25f, 1f, 0f), new Vector3(0f, 0f, 1f), new Vector3(1f, -0.7f, 0.1f), Shape.Open)),
        },
        // Arm out towards the visitor at chest height, index finger leading.
        ["PointForward"] = new Gesture
        {
            At = Mark.Shoulder, End = 1.6f,
            Keys = new[]
            {
                K(0.35f, new Vector3(0.02f, -0.12f, 0.82f), new Vector3(-0.05f, 0.05f, 1f), new Vector3(-0.6f, -0.8f, 0f), new Vector3(0.9f, -0.6f, -0.2f), Shape.Point),
                K(1.1f, new Vector3(0.02f, -0.12f, 0.85f), new Vector3(-0.05f, 0.05f, 1f), new Vector3(-0.6f, -0.8f, 0f), new Vector3(0.9f, -0.6f, -0.2f), Shape.Point),
            },
        },
        // Index finger to the middle of her own chest: the wrist off to her side, the finger
        // pointing in and back, so its tip (about 17 cm from the wrist) reaches the sternum.
        ["PointSelf"] = new Gesture
        {
            At = Mark.Chest, End = 1.6f,
            Keys = new[]
            {
                K(0.35f, new Vector3(0.10f, -0.14f, 0.25f), new Vector3(-0.75f, -0.1f, -0.65f), new Vector3(-0.3f, -0.9f, 0.3f), new Vector3(0.8f, -0.8f, 0.2f), Shape.Point),
                K(1.1f, new Vector3(0.10f, -0.14f, 0.24f), new Vector3(-0.75f, -0.1f, -0.65f), new Vector3(-0.3f, -0.9f, 0.3f), new Vector3(0.8f, -0.8f, 0.2f), Shape.Point),
            },
        },
        // An open hand to the side, palm up: "over there", "this one".
        ["PresentSide"] = new Gesture
        {
            At = Mark.Shoulder, End = 2.0f,
            Keys = new[]
            {
                K(0.4f, new Vector3(0.48f, -0.5f, 0.42f), new Vector3(0.6f, 0f, 0.8f), new Vector3(0f, 1f, 0f), new Vector3(0.4f, -1f, -0.4f), Shape.Flat),
                K(1.5f, new Vector3(0.50f, -0.5f, 0.42f), new Vector3(0.6f, 0f, 0.8f), new Vector3(0f, 1f, 0f), new Vector3(0.4f, -1f, -0.4f), Shape.Flat),
            },
        },
        // A cupped hand at her own ear, palm forward: "what was that?" The wrist sits out to the
        // side at ear height and the fingers curve in towards the ear - with the wrist right at
        // the ear the elbow would have to fold past what an elbow does (about 150 degrees).
        ["HandToEar"] = new Gesture
        {
            At = Mark.Ear, End = 2.2f,
            Keys = new[]
            {
                K(0.45f, new Vector3(0.125f, 0.015f, -0.04f), new Vector3(-0.7f, 0.6f, -0.2f), new Vector3(0f, 0f, 1f), new Vector3(1f, 0.1f, 0.3f), Shape.Open),
                K(1.7f, new Vector3(0.125f, 0.015f, -0.04f), new Vector3(-0.7f, 0.6f, -0.2f), new Vector3(0f, 0f, 1f), new Vector3(1f, 0.1f, 0.3f), Shape.Open),
            },
        },
        // Index finger up, palm to the visitor, wagging from the wrist.
        ["FingerWag"] = new Gesture
        {
            At = Mark.Shoulder, End = 2.0f,
            Keys = Repeat(new[] { 0.35f, 0.6f, 0.85f, 1.1f, 1.5f },
                t => K(t, new Vector3(0.02f, -0.30f, 0.55f), new Vector3(0.35f, 1f, 0.1f), new Vector3(0f, 0f, 1f), new Vector3(0.9f, -1f, -0.2f), Shape.Point),
                t => K(t, new Vector3(0.02f, -0.30f, 0.55f), new Vector3(-0.35f, 1f, 0.1f), new Vector3(0f, 0f, 1f), new Vector3(0.9f, -1f, -0.2f), Shape.Point)),
        },
        // Hand under her chin, index along the jaw, elbow down in front of her.
        ["ThinkChin"] = new Gesture
        {
            At = Mark.Chin, End = 2.4f,
            Keys = new[]
            {
                K(0.45f, new Vector3(0.01f, -0.09f, 0.03f), new Vector3(0f, 1f, -0.15f), new Vector3(-0.3f, 0f, -1f), new Vector3(0.3f, -1f, 0.6f), Shape.Think),
                K(1.9f, new Vector3(0.015f, -0.09f, 0.035f), new Vector3(0.05f, 1f, -0.15f), new Vector3(-0.3f, 0f, -1f), new Vector3(0.3f, -1f, 0.6f), Shape.Think),
            },
        },
        // Palm up, the fingers curling in again and again: "come here".
        ["Beckon"] = new Gesture
        {
            At = Mark.Shoulder, End = 2.2f,
            Keys = Repeat(new[] { 0.35f, 0.6f, 0.85f, 1.1f, 1.35f, 1.6f },
                t => K(t, new Vector3(0.1f, -0.38f, 0.58f), new Vector3(0.1f, 0f, 1f), new Vector3(0f, 1f, 0.15f), new Vector3(0.6f, -1f, -0.4f), Shape.Open),
                t => K(t, new Vector3(0.1f, -0.38f, 0.54f), new Vector3(0.1f, 0.5f, 0.8f), new Vector3(0f, 1f, -0.3f), new Vector3(0.6f, -1f, -0.4f), Shape.Curled)),
        },
        // Both arms open, palms up and forward: welcome.
        ["OpenArms"] = new Gesture
        {
            BothHands = true, At = Mark.Shoulder, End = 2.0f,
            Keys = new[]
            {
                K(0.45f, new Vector3(0.55f, -0.35f, 0.45f), new Vector3(0.5f, 0.1f, 0.85f), new Vector3(-0.2f, 0.9f, 0.3f), new Vector3(0.5f, -1f, -0.4f), Shape.Flat),
                K(1.5f, new Vector3(0.57f, -0.34f, 0.45f), new Vector3(0.5f, 0.1f, 0.85f), new Vector3(-0.2f, 0.9f, 0.3f), new Vector3(0.5f, -1f, -0.4f), Shape.Flat),
            },
        },
        // One clap in front of the chest: palms meet, hold, part.
        ["Clap"] = new Gesture
        {
            BothHands = true, At = Mark.Chest, End = 1.2f,
            Keys = new[]
            {
                K(0.25f, new Vector3(0.15f, -0.18f, 0.30f), new Vector3(-0.15f, 0.6f, 0.8f), new Vector3(-1f, 0f, 0f), new Vector3(0.7f, -1f, -0.4f), Shape.Flat),
                K(0.38f, new Vector3(0.0175f, -0.18f, 0.30f), new Vector3(-0.15f, 0.6f, 0.8f), new Vector3(-1f, 0f, 0f), new Vector3(0.7f, -1f, -0.4f), Shape.Flat),
                K(0.55f, new Vector3(0.0175f, -0.18f, 0.30f), new Vector3(-0.15f, 0.6f, 0.8f), new Vector3(-1f, 0f, 0f), new Vector3(0.7f, -1f, -0.4f), Shape.Flat),
                K(0.75f, new Vector3(0.15f, -0.18f, 0.30f), new Vector3(-0.15f, 0.6f, 0.8f), new Vector3(-1f, 0f, 0f), new Vector3(0.7f, -1f, -0.4f), Shape.Flat),
            },
        },
        // Shoulders up, hands low and out, palms up: "who knows?"
        ["Shrug"] = new Gesture
        {
            BothHands = true, At = Mark.Shoulder, End = 1.6f,
            Keys = new[]
            {
                K(0.4f, new Vector3(0.32f, -0.72f, 0.30f), new Vector3(0.6f, 0f, 0.8f), new Vector3(0f, 1f, 0.1f), new Vector3(0.4f, -1f, -0.6f), Shape.Open, 14f),
                K(1.1f, new Vector3(0.33f, -0.71f, 0.30f), new Vector3(0.6f, 0f, 0.8f), new Vector3(0f, 1f, 0.1f), new Vector3(0.4f, -1f, -0.6f), Shape.Open, 14f),
            },
        },
        // The witch's greedy rub: palms together in front of her, sliding up and down.
        ["RubHands"] = new Gesture
        {
            BothHands = true, At = Mark.Chest, End = 2.4f,
            Keys = Repeat(new[] { 0.4f, 0.7f, 1.0f, 1.3f, 1.6f, 1.9f },
                t => K(t, new Vector3(0.0175f, -0.20f, 0.28f), new Vector3(-0.25f, 0.5f, 0.85f), new Vector3(-1f, 0f, 0f), new Vector3(0.7f, -1f, -0.3f), Shape.Open, 0f, 0.025f),
                t => K(t, new Vector3(0.0175f, -0.20f, 0.28f), new Vector3(-0.25f, 0.5f, 0.85f), new Vector3(-1f, 0f, 0f), new Vector3(0.7f, -1f, -0.3f), Shape.Open, 0f, -0.025f)),
        },
    };

    public static Sample Evaluate(Gesture g, float t)
    {
        float S(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        var keys = g.Keys;
        var first = keys[0];
        var last = keys[keys.Length - 1];
        Key a, b;
        float mix, weight;
        if (t <= first.T)
        {
            a = b = first; mix = 0f;
            weight = S(t / Mathf.Max(0.05f, first.T));
        }
        else if (t >= last.T)
        {
            a = b = last; mix = 0f;
            weight = 1f - S((t - last.T) / Mathf.Max(0.05f, g.End - last.T));
        }
        else
        {
            var i = 0;
            while (i < keys.Length - 2 && t > keys[i + 1].T) i++;
            a = keys[i]; b = keys[i + 1];
            mix = S((t - a.T) / Mathf.Max(0.01f, b.T - a.T));
            weight = 1f;
        }
        return new Sample
        {
            Weight = weight,
            Hand = Vector3.Lerp(a.Hand, b.Hand, mix),
            Fingers = Vector3.Lerp(a.Fingers, b.Fingers, mix),
            Palm = Vector3.Lerp(a.Palm, b.Palm, mix),
            Pole = Vector3.Lerp(a.Pole, b.Pole, mix),
            ShapeA = a.Shape, ShapeB = b.Shape, ShapeMix = mix,
            Shoulder = Mathf.Lerp(a.Shoulder, b.Shoulder, mix),
            Alternate = Mathf.Lerp(a.Alternate, b.Alternate, mix),
        };
    }

    // The world position a gesture puts the wrist of the arm on this side (+1 right, -1 left).
    public static Vector3 Target(Gesture g, Sample s, Body body, int side, float armLength)
    {
        var outward = body.Right * side;
        Vector3 Body(Vector3 v) => outward * v.x + body.Up * v.y + body.Forward * v.z;
        var shoulder = side > 0 ? body.RightShoulder : body.LeftShoulder;
        var lift = body.Up * (s.Alternate * side * body.Scale);
        switch (g.At)
        {
            case Mark.Shoulder:
                return shoulder + Body(s.Hand) * armLength + lift;
            case Mark.Chest:
                return (body.LeftShoulder + body.RightShoulder) * 0.5f + Body(s.Hand) * body.Scale + lift;
            case Mark.Chin:
                return Chin(body) + Body(s.Hand) * body.Scale + lift;
            default:
                return Ear(body, side) + Body(s.Hand) * body.Scale + lift;
        }
    }

    // Faces are measured from the eyes: the chin about 10 cm below and a little in front, an ear
    // 7.5 cm to the side and a little behind - adult sizes, scaled to her.
    public static Vector3 Chin(Body b) => b.Eyes - b.HeadUp * (0.10f * b.Scale) + b.HeadForward * (0.02f * b.Scale);
    public static Vector3 Ear(Body b, int side) => b.Eyes + b.HeadRight * (side * 0.075f * b.Scale) - b.HeadForward * (0.025f * b.Scale) - b.HeadUp * (0.01f * b.Scale);

    // Body directions from the gesture's mirrored coordinates.
    public static Vector3 Direction(Vector3 v, Body body, int side) =>
        (body.Right * (side * v.x) + body.Up * v.y + body.Forward * v.z).normalized;
}

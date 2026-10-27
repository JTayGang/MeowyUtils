using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>2.5D: flat layers stacked along one tilted axis and seen through a single perspective camera.</summary>
internal readonly struct LayerCamera
{
    /// <summary>Where the stack's origin lands on screen.</summary>
    public readonly Vector2 Center;

    /// <summary>Perspective distance in px. Smaller = stronger perspective; a layer at h = Distance would touch the lens.</summary>
    public readonly float Distance;

    private readonly Vector3 _u, _v, _n;

    public LayerCamera(Vector2 center, float distance, float tilt, float azimuth)
    {
        Center = center;
        Distance = MathF.Max(1f, distance);

        // Rotate the plane basis about the axis perpendicular to the lean direction (Rodrigues)
        var k = new Vector3(-MathF.Sin(azimuth), MathF.Cos(azimuth), 0f);
        float c = MathF.Cos(tilt), s = MathF.Sin(tilt);
        _u = Rotate(Vector3.UnitX, k, c, s);
        _v = Rotate(Vector3.UnitY, k, c, s);
        _n = Rotate(Vector3.UnitZ, k, c, s);
    }

    private static Vector3 Rotate(Vector3 v, Vector3 k, float c, float s) =>
        v * c + Vector3.Cross(k, v) * s + k * (Vector3.Dot(k, v) * (1f - c));

    /// <summary>Projects (u, v) on the layer at height h; scale is the perspective magnification there (1 at the origin plane).</summary>
    public Vector2 Project(float u, float v, float h, out float scale)
    {
        Vector3 p = _u * u + _v * v + _n * h;

        // Never let a point reach the lens: the divide blows up as h approaches Distance.
        float denom = MathF.Max(Distance * 0.12f, Distance - p.Z);
        scale = Distance / denom;
        return new Vector2(Center.X + p.X * scale, Center.Y + p.Y * scale);
    }

    /// <summary>Magnification at the middle of the layer at height h (a ring of radius r measures about r * this on screen).</summary>
    public float ScaleAt(float h) => Distance / MathF.Max(Distance * 0.12f, Distance - _n.Z * h);
}

/// <summary>A closed ring's radius wobble: two travelling harmonics, as a fraction of the radius.</summary>
internal readonly record struct RingWobble(float Amp1, int K1, float Phase1, float Amp2, int K2, float Phase2)
{
    public static RingWobble None => default;

    public float At(float angle) => Amp1 * MathF.Sin(K1 * angle + Phase1) + Amp2 * MathF.Sin(K2 * angle + Phase2);
}

/// <summary>Fills StrandPaths with rings and polygons on a layer of a LayerCamera, ready to hand to a stroke.</summary>
internal static class SigilGeometry
{
    /// <summary>A circle (or wobbly ring) of radius on the layer at height, starting at angle (turning the start rotates it).</summary>
    public static void Ring(StrandPath path, int segments, in LayerCamera cam, float radius, float height, float angle,
                            in RingWobble wobble, float centerU = 0f, float centerV = 0f)
    {
        for (int i = 0; i < segments; i++)
        {
            float a = angle + MathF.Tau * i / segments;
            float r = radius * (1f + wobble.At(a));
            path.Points[i] = cam.Project(centerU + r * MathF.Cos(a), centerV + r * MathF.Sin(a), height, out _);
        }
        path.Points[segments] = path.Points[0];
        path.Count = segments + 1;
        path.BuildArc();
    }

    /// <summary>A regular polygon (a triangle for 3 sides), one corner at angle.</summary>
    public static void Polygon(StrandPath path, in LayerCamera cam, int sides, float radius, float height, float angle)
    {
        for (int i = 0; i < sides; i++)
        {
            float a = angle + MathF.Tau * i / sides;
            path.Points[i] = cam.Project(radius * MathF.Cos(a), radius * MathF.Sin(a), height, out _);
        }
        path.Points[sides] = path.Points[0];
        path.Count = sides + 1;
        path.BuildArc();
    }

    /// <summary>A star polygon {n/k}: n corners, each joined to the one k places on</summary>
    public static void Star(StrandPath path, in LayerCamera cam, int n, int k, float radius, float height, float angle)
    {
        for (int i = 0; i < n; i++)
        {
            float a = angle + MathF.Tau * ((i * k) % n) / n;
            path.Points[i] = cam.Project(radius * MathF.Cos(a), radius * MathF.Sin(a), height, out _);
        }
        path.Points[n] = path.Points[0];
        path.Count = n + 1;
        path.BuildArc();
    }

    /// <summary>An open curve between two anchors on the layer at height: a quadratic bow whose middle sits bow off the chord.</summary>
    public static void Span(StrandPath path, int segments, in LayerCamera cam, Vector2 from, Vector2 to, Vector2 bow, float height, in RingWobble wobble)
    {
        Vector2 chord = to - from;
        float cl = chord.Length();
        Vector2 side = cl > 1e-4f ? new Vector2(-chord.Y, chord.X) / cl : Vector2.UnitY;
        Vector2 control = (from + to) * 0.5f + bow * 2f;  // a quadratic's middle is halfway to its control point

        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            float k = 1f - t;
            Vector2 q = k * k * from + 2f * k * t * control + t * t * to;
            q += side * (wobble.At(t * MathF.Tau) * MathF.Sin(t * MathF.PI));
            path.Points[i] = cam.Project(q.X, q.Y, height, out _);
        }
        path.Count = segments + 1;
        path.BuildArc();
    }

    /// <summary>A point on a ring, with the perspective scale there (multiply sizes by it) and the outward screen direction.</summary>
    public static Vector2 PointOn(in LayerCamera cam, float radius, float height, float angle, out float scale, out Vector2 outward)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        Vector2 p = cam.Project(radius * c, radius * s, height, out scale);
        Vector2 q = cam.Project((radius + 8f) * c, (radius + 8f) * s, height, out _);
        Vector2 d = q - p;
        float l = d.Length();
        outward = l > 1e-5f ? d / l : new Vector2(c, s);
        return p;
    }

    /// <summary>The plane radius that lands a ring at screenRadius px on a layer at height (before any tilt).</summary>
    public static float PlaneRadius(float screenRadius, float height, float distance) =>
        screenRadius * MathF.Max(0.12f, 1f - height / distance);
}

/// <summary>A small alphabet of angular runes as polylines in a [-1, 1] box (+Y down, "up" is -Y).</summary>
internal static class RuneGlyphs
{
    /// <summary>First glyph of the Silence-flavoured set (stitched seam, crossed circle, closed eye).</summary>
    public const int FirstSilence = 14;

    /// <summary>How many glyphs the general script spans.</summary>
    public const int LegacyCount = 18;

    /// <summary>First glyph of the infernal set: occult marks such as a leviathan cross, horned sigil and eye in a triangle.</summary>
    public const int FirstInfernal = 18;

    /// <summary>How many glyphs the infernal set has.</summary>
    public const int InfernalCount = 13;

    /// <summary>The strokes of glyph index (wrapped into range), each a polyline.</summary>
    public static Vector2[][] Strokes(int index) => All[(int)((uint)index % (uint)All.Length)];

    /// <summary>Stable glyph for a seed from the count glyphs starting at first (a themed set such as the infernal one).</summary>
    public static int Pick(int seed, int first, int count) => first + (int)(DrawHelpers.Hash01(seed * 31 + 7) * count) % count;

    private static Vector2[] L(params float[] xy)
    {
        var pts = new Vector2[xy.Length / 2];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
        return pts;
    }

    private static Vector2[] Arc(float cx, float cy, float rx, float ry, float a0, float a1, int segs)
    {
        var pts = new Vector2[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            float a = a0 + (a1 - a0) * i / segs;
            pts[i] = new Vector2(cx + MathF.Cos(a) * rx, cy + MathF.Sin(a) * ry);
        }
        return pts;
    }

    /// <summary>A figure eight (lemniscate of Bernoulli) centred at (cx, cy), half-width a.</summary>
    private static Vector2[] Eight(float cx, float cy, float a, int segs)
    {
        var pts = new Vector2[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            float t = MathF.Tau * i / segs;
            float sn = MathF.Sin(t), d = 1f + sn * sn;
            pts[i] = new Vector2(cx + a * MathF.Cos(t) / d, cy + a * sn * MathF.Cos(t) / d);
        }
        return pts;
    }

    /// <summary>An outward spiral from the centre (cx, cy), turns turns, ending at radius r1.</summary>
    private static Vector2[] Spiral(float cx, float cy, float r0, float r1, float turns, float a0, int segs)
    {
        var pts = new Vector2[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            float k = i / (float)segs;
            float a = a0 + MathF.Tau * turns * k, r = r0 + (r1 - r0) * k;
            pts[i] = new Vector2(cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r);
        }
        return pts;
    }

    /// <summary>A star polygon {n/k} inscribed in radius r, one point straight down (inverted), as one closed polyline.</summary>
    private static Vector2[] Star(float cx, float cy, float r, int n, int k)
    {
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float a = MathF.PI * 0.5f + MathF.Tau * ((i * k) % n) / n;
            pts[i] = new Vector2(cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r);
        }
        return pts;
    }

    private static readonly Vector2[][][] All =
    {
        // 0 trident-fork: a staff splitting into two arms
        new[] { L(0f, 1f, 0f, -1f), L(0f, -0.05f, -0.7f, -0.8f), L(0f, -0.05f, 0.7f, -0.8f) },
        // 1 horned: a staff with a triangle bowed off it
        new[] { L(-0.3f, 1f, -0.3f, -1f), L(-0.3f, -0.65f, 0.55f, -0.15f, -0.3f, 0.35f) },
        // 2 two barbs
        new[] { L(-0.3f, 1f, -0.3f, -1f), L(-0.3f, -0.9f, 0.6f, -0.3f), L(-0.3f, -0.15f, 0.6f, 0.45f) },
        // 3 open wedge with a seed
        new[] { L(0.5f, -0.75f, -0.45f, 0f, 0.5f, 0.75f), L(0.62f, -0.02f, 0.62f, 0.02f) },
        // 4 cross
        new[] { L(-0.7f, -0.8f, 0.7f, 0.8f), L(0.7f, -0.8f, -0.7f, 0.8f) },
        // 5 diamond on legs
        new[] { L(0f, -0.95f, 0.6f, -0.4f, 0f, 0.15f, -0.6f, -0.4f, 0f, -0.95f), L(0f, 0.15f, 0.6f, 0.95f), L(0f, 0.15f, -0.6f, 0.95f) },
        // 6 bowtie
        new[] { L(-0.7f, -0.8f, 0.7f, 0.8f, 0.7f, -0.8f, -0.7f, 0.8f, -0.7f, -0.8f) },
        // 7 staff with notches
        new[] { L(0f, -1f, 0f, 1f), L(-0.3f, -0.55f, 0.3f, -0.55f), L(-0.3f, 0.55f, 0.3f, 0.55f) },
        // 8 lightning
        new[] { L(0.5f, -1f, -0.4f, -0.25f, 0.4f, 0.25f, -0.5f, 1f) },
        // 9 hooked staff
        new[] { L(0f, -1f, 0f, 1f), L(0f, -1f, 0.55f, -0.5f), L(0f, 1f, -0.55f, 0.5f) },
        // 10 double bow
        new[] { L(-0.35f, -1f, -0.35f, 1f), L(-0.35f, -1f, 0.45f, -0.5f, -0.35f, 0f), L(-0.35f, 0f, 0.5f, 0.5f, -0.35f, 1f) },
        // 11 ringed slash
        new[] { Arc(0f, 0f, 0.62f, 0.62f, 0f, MathF.Tau, 14), L(-0.45f, 0.75f, 0.45f, -0.75f) },
        // 12 eye
        new[] { L(-0.85f, 0f, 0f, -0.5f, 0.85f, 0f, 0f, 0.5f, -0.85f, 0f), L(0f, -0.08f, 0f, 0.08f) },
        // 13 horned crescent
        new[] { Arc(0f, 0.1f, 0.6f, 0.6f, 0.15f * MathF.PI, 0.85f * MathF.PI, 8), L(-0.5f, -0.1f, -0.7f, -0.8f), L(0.5f, -0.1f, 0.7f, -0.8f) },

        // Silence set: 14 stitched seam: a sealed mouth
        new[] { L(-0.85f, 0f, 0.85f, 0f), L(-0.6f, -0.32f, -0.6f, 0.32f), L(-0.3f, -0.32f, -0.3f, 0.32f), L(0f, -0.32f, 0f, 0.32f), L(0.3f, -0.32f, 0.3f, 0.32f), L(0.6f, -0.32f, 0.6f, 0.32f) },
        // 15 muted: a ring struck through, with a stilled wave inside
        new[] { Arc(0f, 0f, 0.8f, 0.8f, 0f, MathF.Tau, 16), L(-0.56f, -0.56f, 0.56f, 0.56f), L(-0.4f, 0.12f, 0.4f, 0.12f) },
        // 16 closed eye: a lid with lashes
        new[] { Arc(0f, -0.35f, 0.85f, 0.7f, 0.15f * MathF.PI, 0.85f * MathF.PI, 8), L(-0.55f, 0.3f, -0.65f, 0.7f), L(0f, 0.38f, 0f, 0.85f), L(0.55f, 0.3f, 0.65f, 0.7f) },
        // 17 hush: a bar over a falling mark
        new[] { L(-0.8f, -0.55f, 0.8f, -0.55f), L(0f, -0.55f, 0f, 0.9f), L(-0.35f, 0.35f, 0f, 0.9f, 0.35f, 0.35f) },

        // Infernal set: 18 leviathan cross: a double cross over an infinity
        new[] { L(0f, -1f, 0f, 0.46f), L(-0.42f, -0.72f, 0.42f, -0.72f), L(-0.64f, -0.30f, 0.64f, -0.30f), Eight(0f, 0.74f, 0.66f, 20) },
        // 19 the horned one: an inverted triangle, horned, with a single eye
        new[] { L(-0.74f, -0.34f, 0.74f, -0.34f, 0f, 0.96f, -0.74f, -0.34f), L(-0.74f, -0.34f, -0.96f, -0.96f), L(0.74f, -0.34f, 0.96f, -0.96f), L(-0.22f, -0.02f, 0f, 0.1f, 0.22f, -0.02f, 0f, -0.14f, -0.22f, -0.02f) },
        // 20 inverted cross
        new[] { L(0f, -1f, 0f, 1f), L(-0.55f, 0.34f, 0.55f, 0.34f) },
        // 21 eye in a triangle
        new[] { L(0f, -0.96f, 0.88f, 0.8f, -0.88f, 0.8f, 0f, -0.96f), L(-0.46f, 0.24f, 0f, -0.02f, 0.46f, 0.24f, 0f, 0.5f, -0.46f, 0.24f), L(0f, 0.14f, 0f, 0.34f) },
        // 22 ram-horn spiral
        new[] { Spiral(0f, 0f, 0.06f, 0.92f, 1.55f, 0.6f, 22) },
        // 23 serpent: an S with a forked tongue
        new[] { L(0.5f, -0.78f, 0.1f, -0.98f, -0.4f, -0.78f, -0.4f, -0.4f, 0.4f, -0.04f, 0.5f, 0.4f, 0.2f, 0.8f, -0.4f, 0.96f), L(0.5f, -0.78f, 0.66f, -0.9f), L(0.5f, -0.78f, 0.7f, -0.7f) },
        // 24 trident
        new[] { L(0f, -0.96f, 0f, 1f), L(-0.6f, -0.96f, -0.6f, -0.22f), L(0.6f, -0.96f, 0.6f, -0.22f), Arc(0f, -0.22f, 0.6f, 0.56f, 0f, MathF.PI, 8), L(-0.8f, -0.72f, -0.6f, -0.96f, -0.4f, -0.72f), L(0.4f, -0.72f, 0.6f, -0.96f, 0.8f, -0.72f), L(-0.2f, -0.74f, 0f, -0.96f, 0.2f, -0.74f) },
        // 25 skull
        new[] { Arc(0f, -0.2f, 0.74f, 0.72f, MathF.PI, MathF.Tau, 12), L(-0.74f, -0.2f, -0.6f, 0.34f, -0.34f, 0.46f, -0.34f, 0.94f, 0.34f, 0.94f, 0.34f, 0.46f, 0.6f, 0.34f, 0.74f, -0.2f), L(-0.46f, -0.28f, -0.14f, -0.28f, -0.3f, 0.06f, -0.46f, -0.28f), L(0.46f, -0.28f, 0.14f, -0.28f, 0.3f, 0.06f, 0.46f, -0.28f), L(0f, 0.12f, -0.08f, 0.32f, 0.08f, 0.32f, 0f, 0.12f), L(-0.17f, 0.66f, -0.17f, 0.94f), L(0f, 0.62f, 0f, 0.94f), L(0.17f, 0.66f, 0.17f, 0.94f) },
        // 26 ouroboros: a ring that has begun to eat itself
        new[] { Arc(0f, 0f, 0.8f, 0.8f, 0.3f, 5.9f, 20), L(0.9f, 0.4f, 0.76f, 0.26f, 0.98f, 0.18f), L(0f, -0.06f, 0f, 0.06f) },
        // 27 spade: a barbed tail-tip on its stem
        new[] { L(0f, -0.96f, 0.52f, -0.3f, 0f, 0.14f, -0.52f, -0.3f, 0f, -0.96f), L(0f, 0.14f, 0f, 0.4f, 0.22f, 0.7f, 0f, 0.96f) },
        // 28 hellfire: three flames
        new[] { L(-0.6f, 0.96f, -0.82f, 0.4f, -0.6f, 0.0f, -0.38f, 0.4f, -0.6f, 0.96f), L(0f, 0.96f, -0.26f, 0.2f, 0f, -0.96f, 0.26f, 0.2f, 0f, 0.96f), L(0.6f, 0.96f, 0.38f, 0.4f, 0.6f, 0.0f, 0.82f, 0.4f, 0.6f, 0.96f) },
        // 29 the sealed star: a pentagram in its circle
        new[] { Arc(0f, 0f, 0.92f, 0.92f, 0f, MathF.Tau, 18), Star(0f, 0f, 0.78f, 5, 2) },
        // 30 the claw: three barbed strokes torn from one point
        new[] { L(-0.7f, -0.9f, -0.1f, 0.0f, -0.5f, 0.95f), L(0f, -0.96f, 0.14f, 0.1f, -0.06f, 0.96f), L(0.7f, -0.9f, 0.34f, 0.0f, 0.4f, 0.95f) },
    };
}

/// <summary>How a sigil stack moves: the lean wanders and lurches when the cast lands, it swings slowly round, and breathes.</summary>
internal readonly record struct SigilSway(float BaseTilt, float TiltSway, float Precession, float LurchTilt, float LurchSeconds, float BreathBase, float BreathAmp)
{
    /// <summary>The lean in radians; it tips further as the status lifts. Effects add their own extras after.</summary>
    public float Tilt(float age, float leave)
        => BaseTilt + TiltSway * MathF.Sin(age * 0.23f + 1.3f) + LurchTilt * MathF.Exp(-age / LurchSeconds) + 0.10f * leave;

    public float Azimuth(float start, float dir, float age) => start + dir * Precession * age;

    public float Breath(float age) => BreathBase + BreathAmp * MathF.Sin(age * 1.9f + 0.7f);
}

/// <summary>A sigil stack's camera for one cast: SigilSway plus the cast's swing direction. Update once per Emit.</summary>
internal sealed class SigilView
{
    private readonly SigilSway _sway;
    private readonly float _distance;
    private float _azimuth, _dir = 1f;

    public LayerCamera Cam;
    public float Breath = 1f;

    public SigilView(in SigilSway sway, float distance = 1500f) { _sway = sway; _distance = distance; }

    public void Begin(int seed)
    {
        _azimuth = DrawHelpers.Hash01(seed + 1) * MathF.Tau;
        _dir = DrawHelpers.Sign(seed + 2);
    }

    /// <summary>extraTilt adds to the lean (times tiltScale), shift moves the origin, distanceScale zooms.</summary>
    public void Update(in CastFrame f, float extraTilt = 0f, Vector2 shift = default, float distanceScale = 1f, float tiltScale = 1f)
    {
        float tilt = (_sway.Tilt(f.Age, 0f) + extraTilt) * tiltScale + 0.10f * f.Leave;
        Cam = new LayerCamera(f.Center + shift, _distance * f.Px * distanceScale, tilt, _sway.Azimuth(_azimuth, _dir, f.Age));
        Breath = _sway.Breath(f.Age);
    }
}

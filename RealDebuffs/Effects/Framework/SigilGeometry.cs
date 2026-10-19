using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A closed ring's radius wobble: two travelling harmonics, as a fraction of the radius. Keeps a ring from ever being a perfect circle (and moving, whatever it is made of).</summary>
internal readonly record struct RingWobble(float Amp1, int K1, float Phase1, float Amp2, int K2, float Phase2)
{
    public static RingWobble None => default;

    public float At(float angle) => Amp1 * MathF.Sin(K1 * angle + Phase1) + Amp2 * MathF.Sin(K2 * angle + Phase2);
}

/// <summary>Fills StrandPaths with rings and polygons on a layer of a LayerCamera, ready to hand to a stroke. Closed shapes end exactly where they began (Closed strokes need it).</summary>
internal static class SigilGeometry
{
    /// <summary>A circle (or wobbly ring) of <paramref name="radius"/> on the layer at <paramref name="height"/>, starting at <paramref name="angle"/>; the start turning is how a ring rotates. The path needs capacity for segments + 1 points.</summary>
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

    /// <summary>A regular polygon (a triangle for 3 sides), one corner at <paramref name="angle"/>. Two triangles hung at different heights and turned against each other make a hexagram that keeps drifting in and out of true. Capacity sides + 1.</summary>
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

    /// <summary>A star polygon {n/k}: n corners, each joined to the one k places on, drawn as a single closed stroke (a pentagram is {5/2}, a heptagram {7/3}), so a Reveal lays it down in one continuous pen stroke. n and k should share no factor, or one stroke cannot reach every corner. One corner at <paramref name="angle"/>. Capacity n + 1.</summary>
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

    /// <summary>
    /// An open curve between two anchors on the layer at <paramref name="height"/> (plane coordinates, as for Ring): a quadratic bow whose middle sits <paramref name="bow"/> off the chord, plus a sideways wobble
    /// (amplitudes in plane px, evaluated over one turn of its harmonics from end to end) that is pinned to nothing at both ends so the anchors never move. Capacity segments + 1.
    /// For a bolt, a cable or a rope hung between two points; hand it to any stroke.
    /// </summary>
    public static void Span(StrandPath path, int segments, in LayerCamera cam, Vector2 from, Vector2 to, Vector2 bow, float height, in RingWobble wobble)
    {
        Vector2 chord = to - from;
        float cl = chord.Length();
        Vector2 side = cl > 1e-4f ? new Vector2(-chord.Y, chord.X) / cl : Vector2.UnitY;
        Vector2 control = (from + to) * 0.5f + bow * 2f;      // a quadratic's middle is halfway to its control point

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

    /// <summary>A point on a ring, with the perspective scale there (what to multiply a size by) and the screen direction pointing outward from the ring's centre.</summary>
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

    /// <summary>The plane radius that lands a ring at <paramref name="screenRadius"/> px on a layer at <paramref name="height"/> (before any tilt). Lets a layout be written as what the viewer sees.</summary>
    public static float PlaneRadius(float screenRadius, float height, float distance) =>
        screenRadius * MathF.Max(0.12f, 1f - height / distance);
}

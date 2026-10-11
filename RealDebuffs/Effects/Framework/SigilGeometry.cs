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

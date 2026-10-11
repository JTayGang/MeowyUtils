using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// 2.5D: flat layers stacked along one tilted axis and seen through a single perspective camera. A point is (u, v) on a layer plus
/// the layer's height h toward the viewer (all in px at 1080p, scale yourself). Tilt the stack and the layers slide over one another
/// (parallax), the near side of every ring grows, and a slow precession of the tilt keeps the whole thing alive without moving anything
/// by hand. Build a fresh one each frame; it is a few floats.
/// </summary>
internal readonly struct LayerCamera
{
    /// <summary>Where the stack's origin lands on screen.</summary>
    public readonly Vector2 Center;

    /// <summary>Perspective distance in px. Smaller = stronger perspective; a layer at h = Distance would touch the lens.</summary>
    public readonly float Distance;

    private readonly Vector3 _u, _v, _n;

    /// <param name="tilt">Radians the stack's axis leans away from the line of sight.</param>
    /// <param name="azimuth">Which way it leans (radians); changing it over time is the precession.</param>
    public LayerCamera(Vector2 center, float distance, float tilt, float azimuth)
    {
        Center = center;
        Distance = MathF.Max(1f, distance);

        // Rotate the plane basis about the axis perpendicular to the lean direction (Rodrigues), so the stack's axis ends up at (sin t cos a, sin t sin a, cos t).
        var k = new Vector3(-MathF.Sin(azimuth), MathF.Cos(azimuth), 0f);
        float c = MathF.Cos(tilt), s = MathF.Sin(tilt);
        _u = Rotate(Vector3.UnitX, k, c, s);
        _v = Rotate(Vector3.UnitY, k, c, s);
        _n = Rotate(Vector3.UnitZ, k, c, s);
    }

    private static Vector3 Rotate(Vector3 v, Vector3 k, float c, float s) =>
        v * c + Vector3.Cross(k, v) * s + k * (Vector3.Dot(k, v) * (1f - c));

    /// <summary>Projects (u, v) on the layer at height h. <paramref name="scale"/> is the perspective magnification there (1 at the origin plane); size things that sit on it by that.</summary>
    public Vector2 Project(float u, float v, float h, out float scale)
    {
        Vector3 p = _u * u + _v * v + _n * h;

        // Never let a point reach the lens: the divide blows up as h approaches Distance.
        float denom = MathF.Max(Distance * 0.12f, Distance - p.Z);
        scale = Distance / denom;
        return new Vector2(Center.X + p.X * scale, Center.Y + p.Y * scale);
    }

    /// <summary>Magnification at the middle of the layer at height h (what a ring of radius r on it will roughly measure on screen, as r * this).</summary>
    public float ScaleAt(float h) => Distance / MathF.Max(Distance * 0.12f, Distance - _n.Z * h);
}

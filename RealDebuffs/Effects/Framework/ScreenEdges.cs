using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>Screen edges, numbered clockwise from the top. Pinned: effects pick edges by number (FromPerimeter maps onto it; Disease's far tentacle chooses by value), so do not reorder.</summary>
public enum ScreenEdge : byte
{
    Top = 0,
    Right = 1,
    Bottom = 2,
    Left = 3,
}

/// <summary>
/// Geometry for effects whose strands enter from, and are anchored to, the edges of the screen.
/// Shared so every such effect (tendrils, chains, a future rope) agrees on what "an anchor on the
/// left edge" and "inward" mean.
/// </summary>
public static class ScreenEdges
{
    /// <summary>
    /// A point on <paramref name="edge"/>, <paramref name="along"/> (0..1) of the way along it and
    /// pushed <paramref name="overhang"/> pixels OUTSIDE the screen, so the strand's base is hidden.
    /// Top/Bottom run left to right; Right/Left run top to bottom.
    /// </summary>
    public static Vector2 Anchor(Vector2 size, ScreenEdge edge, float along, float overhang) => edge switch
    {
        ScreenEdge.Top    => new Vector2(along * size.X, -overhang),
        ScreenEdge.Right  => new Vector2(size.X + overhang, along * size.Y),
        ScreenEdge.Bottom => new Vector2(along * size.X, size.Y + overhang),
        _                 => new Vector2(-overhang, along * size.Y),
    };

    /// <summary>Unit vector pointing from the edge into the screen.</summary>
    public static Vector2 Inward(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Top    => new Vector2(0f, 1f),
        ScreenEdge.Right  => new Vector2(-1f, 0f),
        ScreenEdge.Bottom => new Vector2(0f, -1f),
        _                 => new Vector2(1f, 0f),
    };

    /// <summary>
    /// Maps a position on the screen's perimeter (0..4, wrapping, clockwise from the top-left
    /// corner: one unit per edge) to an anchor point, with the same overhang as <see cref="Anchor"/>.
    /// Handy for "pick two spots on the border at least a quarter turn apart".
    /// </summary>
    public static Vector2 FromPerimeter(Vector2 size, float p, float overhang, out ScreenEdge edge)
    {
        p %= 4f;
        if (p < 0f) p += 4f;

        edge = (ScreenEdge)Math.Min(3, (int)p);
        float along = p - (int)p;

        // Bottom and Left run against the clockwise direction of travel.
        if (edge == ScreenEdge.Bottom || edge == ScreenEdge.Left) along = 1f - along;
        return Anchor(size, edge, along, overhang);
    }

    /// <summary>The edge nearest a point, by plain distance. Works for points outside the screen too.</summary>
    public static ScreenEdge Nearest(Vector2 size, Vector2 p)
    {
        float top = p.Y, bottom = size.Y - p.Y, left = p.X, right = size.X - p.X;
        float best = top; var edge = ScreenEdge.Top;
        if (right  < best) { best = right;  edge = ScreenEdge.Right; }
        if (bottom < best) { best = bottom; edge = ScreenEdge.Bottom; }
        if (left   < best) {                edge = ScreenEdge.Left; }
        return edge;
    }

    /// <summary>
    /// Finds where a strand last crosses the screen boundary on its way out, scanning from its far
    /// end backwards: the point where a chain "disappears off the edge". Returns false if the
    /// strand never leaves the screen (or never enters it). <paramref name="outward"/> is the unit
    /// direction of travel at the crossing, pointing out of the screen.
    /// </summary>
    public static bool TryFindExit(StrandPath path, Vector2 size, out Vector2 point, out Vector2 outward)
    {
        point = default;
        outward = default;

        for (int i = path.Count - 1; i >= 1; i--)
        {
            Vector2 outside = path.Points[i], inside = path.Points[i - 1];
            if (Inside(outside, size) || !Inside(inside, size)) continue;

            // Walk from the inside point toward the outside one and stop at the first violated slab.
            Vector2 d = outside - inside;
            float t = 1f;
            if (outside.X < 0f)       t = MathF.Min(t, (0f - inside.X) / d.X);
            if (outside.X > size.X)   t = MathF.Min(t, (size.X - inside.X) / d.X);
            if (outside.Y < 0f)       t = MathF.Min(t, (0f - inside.Y) / d.Y);
            if (outside.Y > size.Y)   t = MathF.Min(t, (size.Y - inside.Y) / d.Y);

            point = inside + d * Math.Clamp(t, 0f, 1f);
            float len = d.Length();
            outward = len > 1e-4f ? d / len : Inward(Nearest(size, point)) * -1f;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The mirror of <see cref="TryFindExit"/>: finds where a strand first comes ON screen, scanning
    /// from its start, the point where it appears from behind the edge it is anchored to.
    /// <paramref name="inward"/> is the unit direction of travel at the crossing, pointing into the screen.
    /// </summary>
    public static bool TryFindEntry(StrandPath path, Vector2 size, out Vector2 point, out Vector2 inward)
    {
        point = default;
        inward = default;

        for (int i = 0; i < path.Count - 1; i++)
        {
            Vector2 outside = path.Points[i], inside = path.Points[i + 1];
            if (Inside(outside, size) || !Inside(inside, size)) continue;

            Vector2 d = inside - outside;
            float t = 0f;
            if (outside.X < 0f && d.X > 0f)       t = MathF.Max(t, (0f - outside.X) / d.X);
            if (outside.X > size.X && d.X < 0f)   t = MathF.Max(t, (size.X - outside.X) / d.X);
            if (outside.Y < 0f && d.Y > 0f)       t = MathF.Max(t, (0f - outside.Y) / d.Y);
            if (outside.Y > size.Y && d.Y < 0f)   t = MathF.Max(t, (size.Y - outside.Y) / d.Y);

            point = outside + d * Math.Clamp(t, 0f, 1f);
            float len = d.Length();
            inward = len > 1e-4f ? d / len : Inward(Nearest(size, point));
            return true;
        }
        return false;
    }

    private static bool Inside(Vector2 p, Vector2 size) => p.X >= 0f && p.X <= size.X && p.Y >= 0f && p.Y <= size.Y;
}

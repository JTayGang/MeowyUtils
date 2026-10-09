using System.Numerics;
using Dalamud.Bindings.ImGui;
namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Draws one viscous-liquid particle as lit geometry. Shared by every liquid particle material
/// (<see cref="Materials.ParticleGoop"/> and its subclasses); a material only chooses the
/// <see cref="LiquidMatcap"/>, this decides the shape.
///
/// A particle becomes one of three things, from its thread fields:
///   * Attached  (Tether &gt; 0, ThreadEnd == Position): a pendant drop. One continuous shape from the
///     anchor on the surface, through a meniscus flare and a thinning neck, into a bead. As the neck
///     thins the shape stretches; that stretch is the whole signature of a viscous liquid.
///   * Detached  (Tether &gt; 0, ThreadEnd != Position): a falling teardrop, plus the stub of thread
///     recoiling up toward the surface.
///   * Free      (Tether == 0): a bead, stretched along its velocity into a teardrop.
///
/// Each is a surface of revolution about a straight axis, so a radius profile r(s) is all the geometry
/// there is. The normal at a vertex follows from that profile's slope, and the result is shaded through
/// the liquid matcap with no per-pixel work.
/// </summary>
internal static class GoopDraw
{
    private const int Half = 6;                       // vertices across the visible half of the tube, minus one
    private const int MaxRows = 30;                   // profile samples: keeps Half+3 vertices per row under MeshDraw.MaxVerts
    private const float MinRadius = 0.45f;            // a thread thinner than this still draws as a hairline

    /// <summary>Everything about how this particle is coloured, resolved once.</summary>
    private struct Look
    {
        public LiquidMatcap Liquid;
        public float Alpha;
        public float Feather;
        public bool  Overridden;
        public float Dye;
        public Vector2 Uv;
    }

    public static void Draw(ImDrawListPtr dl, in ParticlePrimitive p, LiquidMatcap liquid, in MaterialContext ctx)
    {
        float alpha = p.Brightness * ctx.Alpha;
        float rb = p.Size;
        if (alpha <= 0.01f || rb < 0.5f) return;

        var look = new Look
        {
            Liquid = liquid,
            Alpha = alpha > 1f ? 1f : alpha,
            Feather = MathF.Max(0.8f, ctx.ScreenScale * 0.9f),
            Overridden = DrawHelpers.ColorOverrideActive,
            Dye = DrawHelpers.ColorOverrideChroma,
            Uv = MeshDraw.WhiteUv(ctx.Time),
        };

        bool threaded = p.Tether > 0.001f;
        bool attached = threaded && Vector2.DistanceSquared(p.ThreadEnd, p.Position) < 0.25f;

        if (attached)
        {
            Pendant(dl, in p, in look);
            return;
        }
        if (threaded) Stub(dl, in p, in look);
        Bead(dl, in p, in look);
    }

    // =========================================================================
    // Profiles. Each fills s[] (distance along the axis from the start) and r[] (radius there).
    // =========================================================================

    /// <summary>A drop hanging from the surface by a neck: anchor -> flare -> neck -> bead.</summary>
    private static void Pendant(ImDrawListPtr dl, in ParticlePrimitive p, in Look look)
    {
        Vector2 a = p.Anchor;
        Vector2 toBead = p.Position - a;
        float len = toBead.Length();
        Vector2 d = len > 1e-3f ? toBead / len : Vector2.UnitY;

        float rb = p.Size;
        float rn = MathF.Max(MinRadius, rb * p.Tether);                 // neck radius
        float stretch = Math.Clamp(len / (rb * 4.5f), 0f, 1f);
        float hb = rb * (1f + 0.30f * stretch);                         // bead half-length: a hanging drop is pear-shaped, longer than wide
        float rTop = MathF.Min(rb * 0.95f, rn + rb * 0.42f);            // where it clings, the neck flares into a meniscus
        float fillet = rb * 1.1f;                                       // how generously neck and bead are blended

        Span<float> s = stackalloc float[MaxRows];
        Span<float> r = stackalloc float[MaxRows];
        int n = 0;

        // The neck proper: only exists once the bead has pulled clear of the surface.
        float neckEnd = len - hb;
        if (neckEnd > rb * 0.4f)
        {
            int rows = Math.Clamp((int)(neckEnd / (rb * 1.1f)) + 2, 3, 9);
            for (int k = 0; k < rows; k++)
            {
                float sk = neckEnd * k / rows;
                s[n] = sk; r[n] = Union(sk, len, hb, rb, rn, rTop, fillet); n++;
            }
        }
        else
        {
            // Still a bulge on the surface: start the bead's own profile at s = 0, where it meets it.
            s[n] = 0f; r[n] = Union(0f, len, hb, rb, rn, rTop, fillet); n++;
        }

        // The bead, sampled by angle so the rows cluster where the outline curves fastest.
        const int beadRows = 11;
        for (int k = 0; k < beadRows; k++)
        {
            float phi = MathF.PI * (k + 0.5f) / beadRows;
            float sk = len - hb * MathF.Cos(phi);
            if (sk <= s[n - 1] + 0.05f) continue;                       // behind the start of the profile
            s[n] = sk; r[n] = Union(sk, len, hb, rb, rn, rTop, fillet); n++;
        }
        s[n] = len + hb; r[n] = 0f; n++;                                // the pole

        Tube(dl, a, d, s, r, n, attachFade: MathF.Max(1.5f, rTop * 1.3f), in look);
        Glint(dl, p.Position, d, hb, rb, in look);
    }

    /// <summary>Radius of the pendant at distance s: the neck and the bead, blended into one fillet.</summary>
    private static float Union(float s, float len, float hb, float rb, float rn, float rTop, float fillet)
    {
        // Neck: thin, flaring toward the surface, widening into a shoulder just above the bead (a hanging drop is
        // pear-shaped, not a ball on a stick), and fading out across the lower half of the bead so it cannot blunt the bottom.
        float flare = rn + (rTop - rn) * MathF.Exp(-4.4f * s / MathF.Max(len, rb * 2f));
        float into = Math.Clamp((s - (len - hb * 4.6f)) / (hb * 4.6f), 0f, 1f);
        float shoulder = rb * 0.46f * into * into * into * (1f - rn / rb);
        float window = Math.Clamp((len + 0.35f * hb - s) / (0.7f * hb), 0f, 1f);
        float neck = (flare + shoulder) * window;

        float u = (s - len) / hb;
        float bead = u * u < 1f ? rb * MathF.Sqrt(1f - u * u) : 0f;

        return SmoothMax(neck, bead, fillet);
    }

    /// <summary>A free bead: a sphere that elongates into a teardrop along its direction of travel.</summary>
    private static void Bead(ImDrawListPtr dl, in ParticlePrimitive p, in Look look)
    {
        float rb = p.Size;
        float speed = p.Velocity.Length();
        Vector2 d = speed > 1e-2f ? p.Velocity / speed : Vector2.UnitY;

        float sigma = Math.Clamp(speed / 900f, 0f, 1.4f);
        float a = rb * (1f + 0.32f * sigma);                            // half-length of the bead along the axis
        float tail = rb * (0.35f + 1.9f * sigma);
        float sc = tail + a;                                            // axis distance from the tail tip to the bead's centre

        Vector2 start = p.Position - d * sc;

        Span<float> s = stackalloc float[MaxRows];
        Span<float> r = stackalloc float[MaxRows];
        int n = 0;

        // Tail: pointed, drawing out of the back of the bead.
        int tailRows = tail > rb * 0.5f ? Math.Clamp((int)(tail / (rb * 0.9f)) + 2, 3, 7) : 2;
        for (int k = 0; k < tailRows; k++)
        {
            float sk = (sc - a) * k / tailRows;
            s[n] = sk; r[n] = TeardropRadius(sk, sc, a, rb, tail); n++;
        }

        const int beadRows = 10;
        for (int k = 0; k < beadRows; k++)
        {
            float phi = MathF.PI * (k + 0.5f) / beadRows;
            float sk = sc - a * MathF.Cos(phi);
            if (sk <= s[n - 1] + 0.05f) continue;
            s[n] = sk; r[n] = TeardropRadius(sk, sc, a, rb, tail); n++;
        }
        s[n] = sc + a; r[n] = 0f; n++;

        Tube(dl, start, d, s, r, n, attachFade: 0f, in look);
        Glint(dl, p.Position, d, a, rb, in look);
    }

    private static float TeardropRadius(float s, float sc, float a, float rb, float tail)
    {
        float u = (s - sc) / a;
        float sphere = u * u < 1f ? rb * MathF.Sqrt(1f - u * u) : 0f;
        if (s >= sc) return sphere;
        float t = Math.Clamp(s / MathF.Max(sc - a * 0.35f, 1e-3f), 0f, 1f);
        float tailR = rb * 0.92f * MathF.Pow(t, 1.9f);
        return SmoothMax(tailR, sphere, rb * 0.5f);
    }

    /// <summary>The thread left on the surface after a drop lets go, recoiling up toward its anchor.</summary>
    private static void Stub(ImDrawListPtr dl, in ParticlePrimitive p, in Look look)
    {
        Vector2 toEnd = p.ThreadEnd - p.Anchor;
        float len = toEnd.Length();
        if (len < 1.2f) return;
        Vector2 d = toEnd / len;

        float rs = MathF.Max(MinRadius, p.Size * p.Tether);
        float rTop = rs * 1.7f;

        Span<float> s = stackalloc float[MaxRows];
        Span<float> r = stackalloc float[MaxRows];
        int rows = Math.Clamp((int)(len / (p.Size * 0.8f)) + 3, 4, 12);
        int n = 0;
        for (int k = 0; k < rows; k++)
        {
            float sk = len * k / rows;
            float flare = rs + (rTop - rs) * MathF.Exp(-4f * sk / len);
            float cap = MathF.Sqrt(Math.Clamp((len - sk) / MathF.Max(len * 0.3f, 0.5f), 0f, 1f));   // rounds off the free end
            s[n] = sk; r[n] = MathF.Max(MinRadius * 0.5f, flare * cap); n++;
        }
        s[n] = len; r[n] = 0f; n++;

        Tube(dl, p.Anchor, d, s, r, n, attachFade: MathF.Max(1.5f, rTop * 1.3f), in look);
    }

    // =========================================================================
    // Glint: the hard specular core a per-vertex lookup cannot hold.
    // =========================================================================

    private static readonly Vector2 KeyHalfXY = HalfXY(StudioLighting.Key);
    private static readonly Vector2 FillHalfXY = HalfXY(StudioLighting.Fill);

    /// <summary>Screen-plane part of the half vector between a light and the viewer: where on a ball that light's highlight sits.</summary>
    private static Vector2 HalfXY(Vector3 light)
    {
        Vector3 h = Vector3.Normalize(light + Vector3.UnitZ);
        return new Vector2(h.X, h.Y);
    }

    /// <summary>
    /// A small bright spot at the point of the bead whose normal faces the half vector, and a dimmer cool
    /// one where the fill light reflects. Placed analytically on the bead's ellipse (centre, axis, half-length
    /// along it, radius across it), so it stays crisp however few vertices the bead has. Without it a drop
    /// reads as a matte balloon; with it, as something wet.
    /// </summary>
    private static void Glint(ImDrawListPtr dl, Vector2 centre, Vector2 axis, float halfLen, float radius, in Look look)
    {
        if (radius < 1.1f) return;
        Vector2 perp = new(-axis.Y, axis.X);

        Vector2 OnBead(Vector2 xy, float inset) =>
            centre + axis * (Vector2.Dot(xy, axis) * halfLen * inset) + perp * (Vector2.Dot(xy, perp) * radius * inset);

        Span<float> rr = stackalloc float[3];
        Span<uint> cc = stackalloc uint[3];

        // Key: a hard core inside a soft halo.
        float g = MathF.Max(0.75f, radius * 0.30f);
        rr[0] = g * 0.55f; rr[1] = g * 0.95f; rr[2] = g * 1.7f;
        cc[0] = GlintColour(1.00f, 0.97f, 0.88f, 0.98f, in look);
        cc[1] = GlintColour(1.00f, 0.97f, 0.88f, 0.55f, in look);
        cc[2] = GlintColour(1.00f, 0.97f, 0.88f, 0.00f, in look);
        MeshDraw.Radial(dl, OnBead(KeyHalfXY, 0.93f), look.Uv, cc[0], 8, rr, cc, 0f, 1f, 0, 0f);

        // Fill: smaller, cooler, dimmer, on the other side.
        float f = MathF.Max(0.6f, radius * 0.16f);
        rr[0] = f * 0.6f; rr[1] = f * 1.1f; rr[2] = f * 1.9f;
        cc[0] = GlintColour(0.82f, 0.93f, 1.00f, 0.62f, in look);
        cc[1] = GlintColour(0.82f, 0.93f, 1.00f, 0.30f, in look);
        cc[2] = GlintColour(0.82f, 0.93f, 1.00f, 0.00f, in look);
        MeshDraw.Radial(dl, OnBead(FillHalfXY, 0.86f), look.Uv, cc[0], 8, rr, cc, 0f, 1f, 0, 0f);
    }

    private static uint GlintColour(float r, float g, float b, float a, in Look look)
    {
        uint rgb = FireColor.Pack(r, g, b);
        float alpha = a * look.Alpha;
        if (look.Overridden) return DrawHelpers.WithAlpha(rgb, alpha);
        return (rgb & 0x00FFFFFFu) | ((uint)(int)(255f * Math.Clamp(alpha, 0f, 1f)) << 24);
    }

    // =========================================================================
    // The mesh: a ribbon of rows, each a cross-section of the surface of revolution.
    // =========================================================================

    private static void Tube(ImDrawListPtr dl, Vector2 origin, Vector2 d, ReadOnlySpan<float> s, ReadOnlySpan<float> r,
                             int n, float attachFade, in Look look)
    {
        if (n < 2) return;
        Vector2 perp = new(-d.Y, d.X);

        int stride = Half + 3;                                          // the visible half, plus a soft-edge vertex each side
        if (n * stride > MeshDraw.MaxVerts) return;

        var outP = MeshDraw.P;
        var outC = MeshDraw.C;
        uint alphaBitsMax = (uint)(int)(255f * look.Alpha) << 24;

        for (int row = 0; row < n; row++)
        {
            Vector2 centre = origin + d * s[row];
            float rad = r[row];

            // Slope of the profile here, by central difference; clamped because it goes vertical at the poles.
            int i0 = row > 0 ? row - 1 : row, i1 = row < n - 1 ? row + 1 : row;
            float ds = s[i1] - s[i0];
            float slope = ds > 1e-4f ? Math.Clamp((r[i1] - r[i0]) / ds, -8f, 8f) : 0f;

            float fade = attachFade > 0f ? Smooth01(s[row] / attachFade) : 1f;
            int vb = row * stride;
            float drawR = MathF.Max(rad, MinRadius);

            for (int c = 0; c <= Half; c++)
            {
                float theta = MathF.PI * (c / (float)Half - 0.5f);      // -90 deg .. +90 deg across the visible half
                float sn = MathF.Sin(theta), cs = MathF.Cos(theta);

                // Normal of a surface of revolution: radial component minus the profile's slope along the axis.
                Vector2 nxy = perp * sn - d * slope;
                Vector3 nrm = Vector3.Normalize(new Vector3(nxy.X, nxy.Y, cs));
                Vector4 lit = look.Liquid.Sample(nrm.X, nrm.Y);

                outP[vb + 1 + c] = centre + perp * (sn * drawR);
                outC[vb + 1 + c] = Pack(lit, fade, in look, alphaBitsMax);
            }

            // Soft edges: a vertex beyond each silhouette, fully transparent, the colour of its neighbour.
            float edge = drawR + look.Feather;
            outP[vb] = centre - perp * edge;
            outP[vb + stride - 1] = centre + perp * edge;
            outC[vb] = outC[vb + 1] & 0x00FFFFFFu;
            outC[vb + stride - 1] = outC[vb + stride - 2] & 0x00FFFFFFu;
        }

        MeshDraw.Grid(dl, stride - 1, n - 1, look.Uv);
    }

    private static uint Pack(Vector4 lit, float fade, in Look look, uint alphaBitsMax)
    {
        Vector3 col = new(lit.X, lit.Y, lit.Z);
        if (look.Dye > 0f) col = DrawHelpers.WithSaturation(col, look.Dye);
        uint rgb = FireColor.Pack(col.X, col.Y, col.Z);
        float a = lit.W * fade * look.Alpha;
        if (look.Overridden) return DrawHelpers.WithAlpha(rgb, a);
        return (rgb & 0x00FFFFFFu) | ((uint)(int)(255f * Math.Clamp(a, 0f, 1f)) << 24);
    }

    // ---- small maths ----

    /// <summary>Polynomial smooth maximum: max(a, b) with the corner rounded over a width k.</summary>
    private static float SmoothMax(float a, float b, float k)
    {
        float h = MathF.Max(k - MathF.Abs(a - b), 0f) / k;
        return MathF.Max(a, b) + h * h * k * 0.25f;
    }

    private static float Smooth01(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

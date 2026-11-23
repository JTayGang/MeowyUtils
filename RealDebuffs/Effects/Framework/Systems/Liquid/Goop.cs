using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>Draws one viscous-liquid particle as lit geometry, for every ParticleGoop.</summary>
internal static class GoopDraw
{
    private const int Half = 6;  // vertices across the visible half of the tube, minus one
    private const int MaxRows = 30;  // profile samples: keeps Half+3 vertices per row under MeshDraw.MaxVerts
    private const float MinRadius = 0.45f;  // a thread thinner than this still draws as a hairline

    // Level of detail by pixels covered: most drops are a few pixels across, where a ten-row shaded mesh has far more vertices than pixels.
    private const float SpeckBelow = 1f;    // radius under this: the streak-and-glint fan, a sub-pixel pip
    private const float DotBelow = 2.5f;    // under this: a lit disc (a fan) if it is resting; moving ones keep their tail in the reduced tube
    private const float ReducedBelow = 4f;  // under this: the same tube with three columns and four bead rows; from here up, the full mesh
    private const int HalfLo = 2;           // columns across the visible half of the reduced tube, minus one

    /// <summary>ParticlePrimitive.Variant for a droplet too small to be worth a mesh: a streak with a glint.</summary>
    public const int Speck = 1;

    /// <summary>Variant for a drop in free fall: wobbles long and flat, rounds and flattens with speed, keeps the tail its liquid would (needs Age).</summary>
    public const int Free = 2;

    /// <summary>Everything about how this particle is coloured, resolved once.</summary>
    private struct Look
    {
        public LiquidMatcap Liquid;
        public LiquidPhysics Phys;
        public float Scale;
        public float Alpha;
        public float Feather;
        public bool  Overridden;
        public float Dye;
        public Vector2 Uv;
    }

    public static void Draw(ImDrawListPtr dl, in ParticlePrimitive p, Liquid liquid, in MaterialContext ctx)
    {
        float alpha = p.Brightness * ctx.Alpha;
        float rb = p.Size;
        if (alpha <= 0.01f || rb < (p.Variant == Speck ? 0.3f : 0.5f)) return;

        var look = new Look
        {
            Liquid = liquid.Matcap,
            Phys = liquid.Phys,
            Scale = MathF.Max(ctx.ScreenScale, 0.25f),
            Alpha = alpha > 1f ? 1f : alpha,
            Feather = MathF.Max(0.8f, ctx.ScreenScale * 0.9f),
            Overridden = ColorOverrideActive,
            Dye = ColorOverrideChroma,
            Uv = MeshDraw.WhiteUv(ctx.Time),
        };

        if (p.Variant == Speck) { SpeckDraw(dl, in p, in look); return; }

        bool threaded = p.Tether > 0.001f;
        bool attached = threaded && Vector2.DistanceSquared(p.ThreadEnd, p.Position) < 0.25f;

        if (attached)
        {
            Pendant(dl, in p, in look);
            return;
        }
        if (threaded) Stub(dl, in p, in look);
        if (rb < SpeckBelow) { SpeckDraw(dl, in p, in look); return; }
        if (rb < DotBelow && p.Velocity.LengthSquared() < 240f * 240f) { Dot(dl, p.Position, rb, in look); return; }  // too small for a mesh and not streaking: a lit disc, glint kept
        if (p.Variant == Free) FreeDrop(dl, in p, in look);
        else Bead(dl, in p, in look);
    }

    /// <summary>A resting drop of a couple of pixels: a nine-vertex disc shaded by the same table, edge fading out.</summary>
    private static void Dot(ImDrawListPtr dl, Vector2 at, float rb, in Look look)
    {
        const int n = 8;
        Span<Vector2> rim = stackalloc Vector2[n];
        Span<uint> col = stackalloc uint[n];
        float edge = rb + look.Feather * 0.4f;
        for (int k = 0; k < n; k++)
        {
            float a = MathF.Tau * k / n, cx = MathF.Cos(a), sy = MathF.Sin(a);
            rim[k] = at + new Vector2(cx, sy) * edge;
            uint c = Pack(look.Liquid.Sample(cx * 0.85f, sy * 0.85f), 1f, in look);
            col[k] = (c & 0x00FFFFFFu) | ((((c >> 24) * 3u) >> 2) << 24);
        }
        MeshDraw.Fan(dl, look.Uv, at, Pack(look.Liquid.Sample(0f, 0f), 1f, in look), rim, col);
        GlintLite(dl, at, Vector2.UnitY, rb, rb, in look);
    }

    /// <summary>A flying droplet at a few pixels: a soft motion-blur streak lit by the same table, with a glint when big enough.</summary>
    private static void SpeckDraw(ImDrawListPtr dl, in ParticlePrimitive p, in Look look)
    {
        float rb = p.Size, speed = p.Velocity.Length();
        Vector2 d = speed > 1e-2f ? p.Velocity / speed : Vector2.UnitY;
        Vector2 perp = new(-d.Y, d.X);
        float tail = MathF.Min(speed * (1f / 120f), rb * 9f) + rb * 0.6f;  // half a frame of travel
        float w = MathF.Max(rb, 0.6f) + look.Feather * 0.4f;

        uint body = Pack(look.Liquid.Sample(-0.30f, -0.40f), 1f, in look);
        uint edge = (body & 0x00FFFFFFu) | ((((body >> 24) * 3u) >> 2) << 24);
        uint clear = body & 0x00FFFFFFu;

        Span<Vector2> rim = stackalloc Vector2[4];
        Span<uint> col = stackalloc uint[4];
        rim[0] = p.Position - d * tail;         col[0] = clear;
        rim[1] = p.Position - perp * w;         col[1] = edge;
        rim[2] = p.Position + d * (rb * 1.2f);  col[2] = clear;
        rim[3] = p.Position + perp * w;         col[3] = edge;
        MeshDraw.Fan(dl, look.Uv, p.Position - d * (tail * 0.25f), body, rim, col);

        Glint(dl, p.Position, d, rb, rb, in look);
    }

    // Angles that never change are not worth a sine and a cosine per vertex, per row, per drop, per frame.
    private static readonly float[] HalfSin = HalfTable(Half, cos: false), HalfCos = HalfTable(Half, cos: true);
    private static readonly float[] HalfSinLo = HalfTable(HalfLo, cos: false), HalfCosLo = HalfTable(HalfLo, cos: true);
    private static float[] HalfTable(int half, bool cos)
    {
        var t = new float[half + 1];
        for (int c = 0; c <= half; c++) { float theta = MathF.PI * (c / (float)half - 0.5f); t[c] = cos ? MathF.Cos(theta) : MathF.Sin(theta); }
        return t;
    }
    private static readonly float[] PendantBeadCos = PhiCos(11), BeadCos = PhiCos(10);  // must match beadRows in Pendant and Bead
    private static readonly float[] PendantBeadCosLo = PhiCos(4), BeadCosLo = PhiCos(4);  // the reduced rung
    private static float[] PhiCos(int rows)
    {
        var t = new float[rows];
        for (int k = 0; k < rows; k++) { float phi = MathF.PI * (k + 0.5f) / rows; t[k] = MathF.Cos(phi); }
        return t;
    }

    // profiles: each fills s[] (distance along the axis) and r[] (radius there)

    /// <summary>A drop hanging from the surface by a neck: anchor -&gt; flare -&gt; neck -&gt; bead.</summary>
    private static void Pendant(ImDrawListPtr dl, in ParticlePrimitive p, in Look look)
    {
        Vector2 a = p.Anchor;
        Vector2 toBead = p.Position - a;
        float len = toBead.Length();
        Vector2 d = len > 1e-3f ? toBead / len : Vector2.UnitY;

        float rb = p.Size;
        bool lo = rb < ReducedBelow;
        float rn = MathF.Max(MinRadius, rb * p.Tether);  // neck radius
        float stretch = Math.Clamp(len / (rb * 4.5f), 0f, 1f);
        float hb = rb * (1f + 0.30f * stretch);  // bead half-length: a hanging drop is pear-shaped, longer than wide
        float rTop = MathF.Min(rb * 0.95f, rn + rb * 0.42f);  // where it clings, the neck flares into a meniscus
        float fillet = rb * 1.1f;  // how generously neck and bead are blended

        Span<float> s = stackalloc float[MaxRows];
        Span<float> r = stackalloc float[MaxRows];
        int n = 0;

        // The neck proper: only exists once the bead has pulled clear of the surface.
        float neckEnd = len - hb;
        if (neckEnd > rb * 0.4f)
        {
            int rows = lo ? Math.Clamp((int)(neckEnd / (rb * 1.1f)) + 2, 2, 3) : Math.Clamp((int)(neckEnd / (rb * 1.1f)) + 2, 3, 9);
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
        var pcos = lo ? PendantBeadCosLo : PendantBeadCos;
        for (int k = 0; k < pcos.Length; k++)
        {
            float sk = len - hb * pcos[k];
            if (sk <= s[n - 1] + 0.05f) continue;  // behind the start of the profile
            s[n] = sk; r[n] = Union(sk, len, hb, rb, rn, rTop, fillet); n++;
        }
        s[n] = len + hb; r[n] = 0f; n++;  // the pole

        Tube(dl, a, d, s, r, n, attachFade: MathF.Max(1.5f, rTop * 1.3f), in look, lo);
        if (lo) GlintLite(dl, p.Position, d, hb, rb, in look); else Glint(dl, p.Position, d, hb, rb, in look);
    }

    /// <summary>Radius of the pendant at distance s: the neck and the bead, blended into one fillet.</summary>
    private static float Union(float s, float len, float hb, float rb, float rn, float rTop, float fillet)
    {
        // Neck: thin, flaring into a shoulder above the bead (pear-shaped, not a ball on a stick).
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
        float a = rb * (1f + 0.32f * sigma);  // half-length of the bead along the axis
        float tail = rb * (0.35f + 1.9f * sigma);
        Teardrop(dl, p.Position, d, a, rb, tail, 1f, in look);
    }

    /// <summary>A drop in free fall: wobbles long to flat at its own pace (thick liquid soon stops), big fast ones squash, the release tail draws back in.</summary>
    private static void FreeDrop(ImDrawListPtr dl, in ParticlePrimitive p, in Look look)
    {
        float rb = p.Size, speed = p.Velocity.Length(), age = p.Age, scale = look.Scale;
        Vector2 d = speed > 1e-2f ? p.Velocity / speed : Vector2.UnitY;
        float h1 = Hash01(p.Seed * 7 + 1), h2 = Hash01(p.Seed * 7 + 2), h3 = Hash01(p.Seed * 7 + 3), h4 = Hash01(p.Seed * 7 + 4), h5 = Hash01(p.Seed * 7 + 5);
        var ph = look.Phys;
        float big = Smooth(rb / (6f * scale));

        float freq = Lerp(9f, 4f, big);  // Hz: small drops ring faster
        float ring = Lerp(1.2f, 0.07f, MathF.Pow(ph.Viscosity, 0.6f));  // seconds the ringing lasts
        float wobble = (0.10f + 0.40f * h1) * MathF.Exp(-age / ring) * MathF.Cos(MathF.Tau * (freq * age + h2));
        float fast = MathF.Min(1f, speed / (2400f * scale));
        float flat = 0.10f * Smooth(rb / (7f * scale)) * fast;
        float blur = 0.58f * MathF.Pow(h5, 1.4f) * fast * (1f - 0.35f * big);  // fast drops are drawn stretched along their travel, each by its own amount

        float q = Math.Clamp((1f + wobble) * (1f + blur) - flat, 0.74f, 1.9f);  // long axis over short; the volume stays that of a sphere of radius rb
        float b = rb / MathF.Cbrt(q), a = b * q;

        float st = ph.Stringiness;
        float keep = Smooth(0.55f, 1f, h3);  // zero for most; some drops stay teardrops, each with its own length of tail
        float tail = rb * ((0.5f + 2.2f * st) * (0.5f + h3) * MathF.Exp(-age / (0.07f + 0.25f * st)) + (1.1f * st + 1.7f * keep) * MathF.Min(1f, speed / (900f * scale)));

        var lk = look;
        lk.Alpha *= 0.84f + 0.16f * h4;
        Teardrop(dl, p.Position, d, a, b, tail, Smooth(tail / (1.3f * rb)), in lk);  // a short tail is thin too, so it fades out instead of leaving a nub
    }

    /// <summary>A bead of half-length a and radius rb along d, drawing out into a pointed tail of the given length behind it.</summary>
    private static void Teardrop(ImDrawListPtr dl, Vector2 at, Vector2 d, float a, float rb, float tail, float tailW, in Look look)
    {
        float sc = tail + a;  // axis distance from the tail tip to the bead's centre
        bool lo = rb < ReducedBelow;

        Vector2 start = at - d * sc;

        Span<float> s = stackalloc float[MaxRows];
        Span<float> r = stackalloc float[MaxRows];
        int n = 0;

        // Tail: pointed, drawing out of the back of the bead.
        int tailRows = tail > rb * 0.5f ? (lo ? 2 : Math.Clamp((int)(tail / (rb * 0.9f)) + 2, 3, 7)) : 2;
        for (int k = 0; k < tailRows; k++)
        {
            float sk = (sc - a) * k / tailRows;
            s[n] = sk; r[n] = TeardropRadius(sk, sc, a, rb, tailW); n++;
        }

        var bcos = lo ? BeadCosLo : BeadCos;
        for (int k = 0; k < bcos.Length; k++)
        {
            float sk = sc - a * bcos[k];
            if (sk <= s[n - 1] + 0.05f) continue;
            s[n] = sk; r[n] = TeardropRadius(sk, sc, a, rb, tailW); n++;
        }
        s[n] = sc + a; r[n] = 0f; n++;

        Tube(dl, start, d, s, r, n, attachFade: 0f, in look, lo);
        if (lo) GlintLite(dl, at, d, a, rb, in look); else Glint(dl, at, d, a, rb, in look);
    }

    private static float TeardropRadius(float s, float sc, float a, float rb, float tailW = 1f)
    {
        float u = (s - sc) / a;
        float sphere = u * u < 1f ? rb * MathF.Sqrt(1f - u * u) : 0f;
        if (s >= sc) return sphere;
        float t = Math.Clamp(s / MathF.Max(sc - a * 0.35f, 1e-3f), 0f, 1f);
        float tailR = rb * 0.92f * MathF.Pow(t, 1.9f) * tailW;
        return SmoothMax(tailR, sphere, rb * 0.5f);
    }

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
        bool lo = p.Size < ReducedBelow;
        int rows = lo ? Math.Clamp((int)(len / (p.Size * 0.8f)) + 3, 3, 5) : Math.Clamp((int)(len / (p.Size * 0.8f)) + 3, 4, 12);
        int n = 0;
        for (int k = 0; k < rows; k++)
        {
            float sk = len * k / rows;
            float flare = rs + (rTop - rs) * MathF.Exp(-4f * sk / len);
            float cap = MathF.Sqrt(Math.Clamp((len - sk) / MathF.Max(len * 0.3f, 0.5f), 0f, 1f));  // rounds off the free end
            s[n] = sk; r[n] = MathF.Max(MinRadius * 0.5f, flare * cap); n++;
        }
        s[n] = len; r[n] = 0f; n++;

        Tube(dl, p.Anchor, d, s, r, n, attachFade: MathF.Max(1.5f, rTop * 1.3f), in look, lo);
    }

    // glint: the hard specular core a per-vertex lookup cannot hold

    private static readonly Vector2 KeyHalfXY = HalfXY(StudioLighting.Key);
    private static readonly Vector2 FillHalfXY = HalfXY(StudioLighting.Fill);

    /// <summary>Screen-plane half vector between a light and the viewer: where that light's highlight sits on a ball.</summary>
    private static Vector2 HalfXY(Vector3 light)
    {
        Vector3 h = Vector3.Normalize(light + Vector3.UnitZ);
        return new Vector2(h.X, h.Y);
    }

    /// <summary>A bright spot where the bead's normal faces the half vector, plus a dimmer cool one for the fill light</summary>
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

    /// <summary>Key glint of a small drop: a hard bright core fading out, five vertices; the fill glint is sub-pixel here and omitted.</summary>
    private static void GlintLite(ImDrawListPtr dl, Vector2 centre, Vector2 axis, float halfLen, float radius, in Look look)
    {
        if (radius < 1.1f) return;
        Vector2 perp = new(-axis.Y, axis.X);
        Vector2 at = centre + axis * (Vector2.Dot(KeyHalfXY, axis) * halfLen * 0.93f) + perp * (Vector2.Dot(KeyHalfXY, perp) * radius * 0.93f);
        float g = MathF.Max(0.75f, radius * 0.30f) * 1.25f;
        uint core = GlintColour(1.00f, 0.97f, 0.88f, 0.98f, in look), clear = GlintColour(1.00f, 0.97f, 0.88f, 0.00f, in look);
        Span<Vector2> rim = stackalloc Vector2[4];
        Span<uint> col = stackalloc uint[4];
        rim[0] = at + new Vector2(g, 0f); rim[1] = at + new Vector2(0f, g); rim[2] = at - new Vector2(g, 0f); rim[3] = at - new Vector2(0f, g);
        col[0] = col[1] = col[2] = col[3] = clear;
        MeshDraw.Fan(dl, look.Uv, at, core, rim, col);
    }

    private static uint GlintColour(float r, float g, float b, float a, in Look look)
    {
        return Tint(DrawHelpers.Pack(r, g, b), a * look.Alpha, look.Overridden);
    }

    // the mesh: a ribbon of rows, each a cross-section of the surface of revolution

    private static void Tube(ImDrawListPtr dl, Vector2 origin, Vector2 d, ReadOnlySpan<float> s, ReadOnlySpan<float> r,
                             int n, float attachFade, in Look look, bool lo = false)
    {
        if (n < 2) return;
        Vector2 perp = new(-d.Y, d.X);

        int half = lo ? HalfLo : Half;
        float[] hsin = lo ? HalfSinLo : HalfSin, hcos = lo ? HalfCosLo : HalfCos;
        int stride = half + 3;  // the visible half, plus a soft-edge vertex each side
        if (n * stride > MeshDraw.MaxVerts) return;

        var outP = MeshDraw.P;
        var outC = MeshDraw.C;

        for (int row = 0; row < n; row++)
        {
            Vector2 centre = origin + d * s[row];
            float rad = r[row];

            // Slope of the profile here, by central difference; clamped because it goes vertical at the poles.
            int i0 = row > 0 ? row - 1 : row, i1 = row < n - 1 ? row + 1 : row;
            float ds = s[i1] - s[i0];
            float slope = ds > 1e-4f ? Math.Clamp((r[i1] - r[i0]) / ds, -8f, 8f) : 0f;

            float fade = attachFade > 0f ? Smooth(s[row] / attachFade) : 1f;
            int vb = row * stride;
            float drawR = MathF.Max(rad, MinRadius);

            for (int c = 0; c <= half; c++)
            {
                float sn = hsin[c], cs = hcos[c];  // -90 deg .. +90 deg across the visible half

                // Normal of a surface of revolution: radial component minus the profile's slope along the axis.
                Vector2 nxy = perp * sn - d * slope;
                Vector3 nrm = Vector3.Normalize(new Vector3(nxy.X, nxy.Y, cs));
                Vector4 lit = look.Liquid.Sample(nrm.X, nrm.Y);

                outP[vb + 1 + c] = centre + perp * (sn * drawR);
                outC[vb + 1 + c] = Pack(lit, fade, in look);
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

    private static uint Pack(Vector4 lit, float fade, in Look look)
    {
        Vector3 col = new(lit.X, lit.Y, lit.Z);
        if (look.Dye > 0f) col = BoostSaturation(col, look.Dye, SoftDye);
        return Tint(DrawHelpers.Pack(col.X, col.Y, col.Z), lit.W * fade * look.Alpha, look.Overridden);
    }

    // small maths

    /// <summary>Polynomial smooth maximum: max(a, b) with the corner rounded over a width k.</summary>
    private static float SmoothMax(float a, float b, float k)
    {
        float h = MathF.Max(k - MathF.Abs(a - b), 0f) / k;
        return MathF.Max(a, b) + h * h * k * 0.25f;
    }

}

/// <summary>The keyframed pendant a ceiling seep hangs (LiquidField): where in its cycle it lets go, and its shape at swell q.</summary>
internal static class PendantKeys
{
    public const float RestEnd = 0.10f;
    public const float PinchAt = 0.86f;

    /// <summary>The drop at q (0..1 through its swell): bead radius, how far it hangs, and the neck's thickness as a fraction of the bead.</summary>
    public static void Shape(float q, float rMax, float lMax, float stringiness, out float rb, out float len, out float neck)
    {
        rb = rMax * (0.40f + 0.60f * MathF.Sqrt(q));
        len = rb * 0.45f + lMax * MathF.Pow(q, 2.4f);
        neck = Lerp(0.66f, 0.10f + 0.10f * (1f - stringiness), MathF.Pow(q, 1.4f));
    }
}

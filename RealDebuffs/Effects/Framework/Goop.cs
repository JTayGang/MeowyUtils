using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>Draws one viscous-liquid particle as lit geometry, for every ParticleGoop.</summary>
internal static class GoopDraw
{
    private const int Half = 6;  // vertices across the visible half of the tube, minus one
    private const int MaxRows = 30;  // profile samples: keeps Half+3 vertices per row under MeshDraw.MaxVerts
    private const float MinRadius = 0.45f;  // a thread thinner than this still draws as a hairline

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

    // Angles that never change are not worth a sine and a cosine per vertex, per row, per drop, per frame.
    private static readonly float[] HalfSin = HalfTable(cos: false), HalfCos = HalfTable(cos: true);
    private static float[] HalfTable(bool cos)
    {
        var t = new float[Half + 1];
        for (int c = 0; c <= Half; c++) { float theta = MathF.PI * (c / (float)Half - 0.5f); t[c] = cos ? MathF.Cos(theta) : MathF.Sin(theta); }
        return t;
    }
    private static readonly float[] PendantBeadCos = PhiCos(11), BeadCos = PhiCos(10);  // must match beadRows in Pendant and Bead
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
            float sk = len - hb * PendantBeadCos[k];
            if (sk <= s[n - 1] + 0.05f) continue;  // behind the start of the profile
            s[n] = sk; r[n] = Union(sk, len, hb, rb, rn, rTop, fillet); n++;
        }
        s[n] = len + hb; r[n] = 0f; n++;  // the pole

        Tube(dl, a, d, s, r, n, attachFade: MathF.Max(1.5f, rTop * 1.3f), in look);
        Glint(dl, p.Position, d, hb, rb, in look);
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
        float sc = tail + a;  // axis distance from the tail tip to the bead's centre

        Vector2 start = p.Position - d * sc;

        Span<float> s = stackalloc float[MaxRows];
        Span<float> r = stackalloc float[MaxRows];
        int n = 0;

        // Tail: pointed, drawing out of the back of the bead.
        int tailRows = tail > rb * 0.5f ? Math.Clamp((int)(tail / (rb * 0.9f)) + 2, 3, 7) : 2;
        for (int k = 0; k < tailRows; k++)
        {
            float sk = (sc - a) * k / tailRows;
            s[n] = sk; r[n] = TeardropRadius(sk, sc, a, rb); n++;
        }

        const int beadRows = 10;
        for (int k = 0; k < beadRows; k++)
        {
            float sk = sc - a * BeadCos[k];
            if (sk <= s[n - 1] + 0.05f) continue;
            s[n] = sk; r[n] = TeardropRadius(sk, sc, a, rb); n++;
        }
        s[n] = sc + a; r[n] = 0f; n++;

        Tube(dl, start, d, s, r, n, attachFade: 0f, in look);
        Glint(dl, p.Position, d, a, rb, in look);
    }

    private static float TeardropRadius(float s, float sc, float a, float rb)
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
            float cap = MathF.Sqrt(Math.Clamp((len - sk) / MathF.Max(len * 0.3f, 0.5f), 0f, 1f));  // rounds off the free end
            s[n] = sk; r[n] = MathF.Max(MinRadius * 0.5f, flare * cap); n++;
        }
        s[n] = len; r[n] = 0f; n++;

        Tube(dl, p.Anchor, d, s, r, n, attachFade: MathF.Max(1.5f, rTop * 1.3f), in look);
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

    private static uint GlintColour(float r, float g, float b, float a, in Look look)
    {
        return DrawHelpers.Tint(DrawHelpers.Pack(r, g, b), a * look.Alpha, look.Overridden);
    }

    // the mesh: a ribbon of rows, each a cross-section of the surface of revolution

    private static void Tube(ImDrawListPtr dl, Vector2 origin, Vector2 d, ReadOnlySpan<float> s, ReadOnlySpan<float> r,
                             int n, float attachFade, in Look look)
    {
        if (n < 2) return;
        Vector2 perp = new(-d.Y, d.X);

        int stride = Half + 3;  // the visible half, plus a soft-edge vertex each side
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

            float fade = attachFade > 0f ? DrawHelpers.Smooth(s[row] / attachFade) : 1f;
            int vb = row * stride;
            float drawR = MathF.Max(rad, MinRadius);

            for (int c = 0; c <= Half; c++)
            {
                float sn = HalfSin[c], cs = HalfCos[c];  // -90 deg .. +90 deg across the visible half

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
        if (look.Dye > 0f) col = DrawHelpers.BoostSaturation(col, look.Dye, DrawHelpers.SoftDye);
        return DrawHelpers.Tint(DrawHelpers.Pack(col.X, col.Y, col.Z), lit.W * fade * look.Alpha, look.Overridden);
    }

    // small maths

    /// <summary>Polynomial smooth maximum: max(a, b) with the corner rounded over a width k.</summary>
    private static float SmoothMax(float a, float b, float k)
    {
        float h = MathF.Max(k - MathF.Abs(a - b), 0f) / k;
        return MathF.Max(a, b) + h * h * k * 0.25f;
    }

}

/// <summary>Viscous-drip simulation (a StrokeDripOptions emission opts in).</summary>
public static class GoopEmitter
{
    private const int MaxDrops = 192;
    private const int MaxPendants = 256;
    private const int MaxSitesPerStroke = 12;
    private const float MaxStep = 0.05f;  // a frame hitch must not make a drop teleport

    // Fractions of a cycle: the bead rests, swells and stretches until PinchAt, then lets go.
    private const float RestEnd = 0.10f;
    private const float PinchAt = 0.86f;

    private readonly record struct SiteKey(DebuffKind Owner, int StrokeSeed, int Index);

    private struct Site
    {
        public float Phase;  // 0..1 through the current cycle
        public float Cycle;  // seconds this cycle lasts
        public int   Cycles;  // completed so far; salts every per-cycle random
        public float LastSeen;
    }

    private struct Drop
    {
        public DebuffKind Owner;
        public Vector2 Pos, Vel, Gravity;
        public Vector2 Anchor, Pinch;  // where the string was fixed, and where it broke
        public float Born, Life;
        public float Size, Brightness;
        public float Tether;  // thickness of the string at the moment of release, fraction of Size
        public float RecoilTime;  // how long the string takes to snap back; 0 = no string
        public float Drag;
        public int   Seed;
        public int   HostSeed;  // the strand it came from, so it is drawn with that strand
        public string? Material;
        public Vector4? ColorOverride;
    }

    private static readonly Dictionary<SiteKey, Site> Sites = new();
    private static readonly List<SiteKey> Stale = new();
    private static readonly Drop[] Drops = new Drop[MaxDrops];
    private static int _dropCount;
    private static readonly ParticlePrimitive[] Pendants = new ParticlePrimitive[MaxPendants];
    private static int _pendantCount;
    private static float _lastPrune;

    /// <summary>Call once at the start of each frame: moves fallen drops and forgets sites whose strand is gone.</summary>
    public static void Advance(float time, float dt, Vector2 screen)
    {
        _pendantCount = 0;
        dt = MathF.Min(dt, MaxStep);

        int w = 0;
        float floor = screen.Y > 0f ? screen.Y + 80f : float.MaxValue;
        for (int i = 0; i < _dropCount; i++)
        {
            ref Drop d = ref Drops[i];
            if (time - d.Born >= d.Life) continue;
            d.Vel += d.Gravity * dt;
            d.Vel *= MathF.Exp(-d.Drag * dt);  // viscous drag: drops reach a terminal speed
            d.Pos += d.Vel * dt;
            if (d.Pos.Y > floor) continue;
            Drops[w++] = d;
        }
        _dropCount = w;

        if (time - _lastPrune > 0.5f)
        {
            _lastPrune = time;
            Stale.Clear();
            foreach (var kv in Sites)
                if (time - kv.Value.LastSeen > 0.6f) Stale.Add(kv.Key);
            for (int i = 0; i < Stale.Count; i++) Sites.Remove(Stale[i]);
        }
    }

    /// <summary>Grows this frame's drops along one stroke for one drip emission</summary>
    public static void Tend(in StrokePrimitive s, in StrokeEmission e, in StrokeDripOptions o,
                            IStrokeMaterial? strokeMaterial, string? particleMaterial,
                            float time, float dt, Vector2 screen)
    {
        dt = MathF.Min(dt, MaxStep);
        float px = DrawHelpers.PixelScale(screen);
        float shortSide = screen.X > 0f ? MathF.Min(screen.X, screen.Y) : 1080f;

        float visibleLen = s.Path.Length * s.Reveal;
        if (visibleLen < 24f * px) return;

        float depthScale = 1f - 0.35f * Math.Clamp(s.Depth, 0f, 1f);
        float spacing = MathF.Max(24f, o.SiteSpacingPx) * px;
        int count = Math.Min(MaxSitesPerStroke, (int)(visibleLen / spacing));

        // Index -1 is the tip; 0.. are along the strand.
        int first = s.Closed ? 0 : -1;
        for (int index = first; index < count; index++)
        {
            float arc;
            bool tip = index < 0;
            if (tip)
            {
                arc = visibleLen;
            }
            else
            {
                float jitter = DrawHelpers.HashRange(unchecked(s.Seed * 31 + index * 7919), -0.30f, 0.30f);
                arc = (index + 0.5f + jitter) * spacing;
                if (arc > visibleLen - 14f * px) continue;  // not grown out to here yet
            }

            TendSite(in s, in e, in o, strokeMaterial, particleMaterial, index, tip, arc,
                     time, dt, px, shortSide, depthScale, screen);
        }
    }

    private static void TendSite(in StrokePrimitive s, in StrokeEmission e, in StrokeDripOptions o,
                                 IStrokeMaterial? strokeMaterial, string? particleMaterial,
                                 int index, bool tip, float arc, float time, float dt, float px, float shortSide,
                                 float depthScale, Vector2 screen)
    {
        var key = new SiteKey(s.Owner, s.Seed, index);
        ref Site site = ref CollectionsMarshal.GetValueRefOrAddDefault(Sites, key, out bool exists);
        int siteSeed = unchecked(s.Seed * 131 + index * 977 + 17);
        if (!exists)
        {
            site.Cycle = DrawHelpers.HashRange(siteSeed, o.CycleSecondsMin, o.CycleSecondsMax);
            site.Phase = DrawHelpers.Hash01(siteSeed + 3) * PinchAt * 0.95f;  // out of step, so a strand never drips in unison
        }
        site.LastSeen = time;
        site.Phase += dt / site.Cycle;

        // where the drop hangs from
        s.Path.SampleAtArc(arc, out Vector2 pos, out Vector2 tan);
        float radius = strokeMaterial is not null ? strokeMaterial.RadiusAt(in s, arc, shortSide) : MathF.Max(1f, s.WidthHint * 0.5f);

        Vector2 anchor;
        float downhill;  // 0..1 how much this spot faces down; gates and fades the drop
        if (tip)
        {
            anchor = pos;
            downhill = tan.Y;  // the tip points along the tangent: hang from it only if that points down
        }
        else
        {
            Vector2 perp = new(-tan.Y, tan.X);
            if (perp.Y < 0f) perp = -perp;  // the side of the strand that faces the floor
            anchor = pos + perp * (radius * 0.78f);
            downhill = perp.Y;
        }
        float awake = DrawHelpers.Smooth((downhill - o.MinSlope) / 0.18f);

        // the size of this cycle's drop
        int cycleSeed = unchecked(siteSeed + site.Cycles * 7331);
        // A thicker strand carries more liquid: the drop grows with the skin it hangs from, by a fraction of the way.
        float thick = Math.Clamp(radius / (26f * px), 1f, 2.6f);
        float rMax = DrawHelpers.HashRange(cycleSeed + 1, e.SizeMin, e.SizeMax) * px * depthScale * (tip ? 1.25f : 1f) * (1f + 0.65f * (thick - 1f));
        float lMax = rMax * (1.2f + 5.4f * Math.Clamp(o.Stringiness, 0f, 1f));

        // the shape of the pendant drop at this point in its life
        float q = Math.Clamp((site.Phase - RestEnd) / (PinchAt - RestEnd), 0f, 1f);
        float rb = rMax * (0.40f + 0.60f * MathF.Sqrt(q));
        float len = rb * 0.45f + lMax * MathF.Pow(q, 2.4f);
        float neck = DrawHelpers.Lerp(0.66f, 0.10f + 0.10f * (1f - o.Stringiness), MathF.Pow(q, 1.4f));
        float swing = 0.12f * MathF.Sin(time * 1.9f + siteSeed * 0.37f) * q;
        Vector2 bead = anchor + new Vector2(MathF.Sin(swing), MathF.Cos(swing)) * len;

        bool onScreen = screen.X <= 0f || (anchor.X > -40f && anchor.X < screen.X + 40f && anchor.Y > -40f && anchor.Y < screen.Y + 40f);

        if (awake > 0.01f && onScreen && _pendantCount < MaxPendants)
        {
            Pendants[_pendantCount++] = new ParticlePrimitive
            {
                Owner = s.Owner,
                Position = bead,
                Anchor = anchor,
                ThreadEnd = bead,
                Tether = neck,
                Size = rb,
                AgeRatio = q,
                Brightness = s.Brightness * awake,
                Seed = cycleSeed,
                HasHost = true, HostSeed = s.Seed,
                Role = e.Role,
                MaterialName = particleMaterial,
                ColorOverride = s.ColorOverride,
            };
        }

        // let go
        if (site.Phase >= PinchAt + 0.0001f)
        {
            if (awake > 0.5f && onScreen) Release(in s, in e, in o, particleMaterial, anchor, bead, rb, neck, cycleSeed, time, px);
            site.Cycles++;
            site.Cycle = DrawHelpers.HashRange(unchecked(siteSeed + site.Cycles * 7331 + 1), o.CycleSecondsMin, o.CycleSecondsMax);
            site.Phase = 0f;
        }
    }

    private static void Release(in StrokePrimitive s, in StrokeEmission e, in StrokeDripOptions o, string? material,
                                Vector2 anchor, Vector2 bead, float rb, float neck, int seed, float time, float px)
    {
        if (_dropCount >= MaxDrops) return;

        float g = e.Gravity.Y != 0f ? e.Gravity.Y : 1500f;
        float v0 = DrawHelpers.HashRange(seed + 5, e.SpeedMin, e.SpeedMax) * px;
        float life = DrawHelpers.HashRange(seed + 6, e.LifespanMin, e.LifespanMax);

        Drops[_dropCount++] = new Drop
        {
            Owner = s.Owner,
            Pos = bead,
            Vel = new Vector2(DrawHelpers.HashRange(seed + 7, -10f, 10f) * px, v0),
            Gravity = new Vector2(e.Gravity.X, g) * px,
            Drag = 1.6f,
            Anchor = anchor,
            Pinch = bead,
            Born = time,
            Life = life,
            Size = rb,
            Brightness = s.Brightness,
            Tether = neck,
            RecoilTime = 0.15f + 0.12f * Math.Clamp(o.Stringiness, 0f, 1f),
            Seed = seed,
            HostSeed = s.Seed,
            Material = material,
            ColorOverride = s.ColorOverride,
        };

        // The string rarely breaks cleanly: sometimes a small droplet is left in the gap, falling a little behind.
        if (_dropCount < MaxDrops && DrawHelpers.Hash01(seed + 8) < o.SatelliteChance)
        {
            Vector2 at = Vector2.Lerp(anchor, bead, 0.62f);
            Drops[_dropCount++] = new Drop
            {
                Owner = s.Owner,
                Pos = at,
                Vel = new Vector2(DrawHelpers.HashRange(seed + 9, -12f, 12f) * px, v0 * 0.35f),
                Gravity = new Vector2(e.Gravity.X, g) * px,
                Drag = 1.6f,
                Born = time,
                Life = life,
                Size = rb * DrawHelpers.HashRange(seed + 10, 0.20f, 0.32f),
                Brightness = s.Brightness,
                Seed = seed + 99,
                HostSeed = s.Seed,
                Material = material,
                ColorOverride = s.ColorOverride,
            };
        }
    }

    /// <summary>Called once at the end of the frame: hands the scene this frame's hanging and falling liquid.</summary>
    public static void Emit(EffectScene scene, float time)
    {
        for (int i = 0; i < _pendantCount; i++)
            scene.AddParticleForOwner(Pendants[i], Pendants[i].Owner);

        for (int i = 0; i < _dropCount; i++)
        {
            ref readonly Drop d = ref Drops[i];
            float age = time - d.Born;
            float t01 = Math.Clamp(age / d.Life, 0f, 1f);

            var p = new ParticlePrimitive
            {
                Owner = d.Owner,
                Position = d.Pos,
                Velocity = d.Vel,
                Size = d.Size,
                AgeRatio = t01,
                Brightness = d.Brightness * ParticleEmitter.FadeFor(t01),
                Seed = d.Seed,
                HasHost = true, HostSeed = d.HostSeed,
                Role = PrimitiveRole.Goop,
                MaterialName = d.Material,
                ColorOverride = d.ColorOverride,
            };

            // The broken string snaps back up to where it was fixed, thinning as it goes.
            if (d.RecoilTime > 0f && age < d.RecoilTime)
            {
                float k = age / d.RecoilTime;
                float eased = 1f - (1f - k) * (1f - k);
                p.Anchor = d.Anchor;
                p.ThreadEnd = Vector2.Lerp(d.Pinch, d.Anchor, eased);
                p.Tether = d.Tether * (1f - 0.65f * k);
            }
            scene.AddParticleForOwner(p, d.Owner);
        }
    }

}

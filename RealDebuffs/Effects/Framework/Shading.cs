using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>The shared lighting rig: warm key (upper left/front), cold fill (lower right), cold rim; also the source of truth for shadow direction. View space: +Y down.</summary>
public static class StudioLighting
{
    public static readonly Vector3 Key  = Vector3.Normalize(new Vector3(-0.52f, -0.70f, 0.50f));
    public static readonly Vector3 Fill = Vector3.Normalize(new Vector3( 0.70f,  0.45f, 0.55f));
    public static readonly Vector3 Rim  = Vector3.Normalize(new Vector3( 0.80f, -0.30f, -0.52f));

    public static readonly Vector3 KeyColor  = new(1.00f, 0.90f, 0.76f);
    public static readonly Vector3 FillColor = new(0.34f, 0.40f, 0.52f);
    public static readonly Vector3 RimColor  = new(0.62f, 0.78f, 1.00f);

    /// <summary>Screen-space unit vector pointing the way shadows fall (directly away from the key light).</summary>
    public static readonly Vector2 ShadowDirection = Vector2.Normalize(new Vector2(-Key.X, -Key.Y));
}

/// <summary>How a surface answers light. Authored in linear space; the matcap converts to display space.</summary>
public readonly record struct SurfaceSpec(
    Vector3 Albedo,        // diffuse colour of the non-metallic part
    Vector3 F0,            // reflectance at normal incidence: the tint of the metal
    float   Metalness,     // 0 = pure diffuse (rust, stone), 1 = pure metal
    float   Roughness,     // 0 = mirror, 1 = chalk
    float   EnvStrength,   // how much of the surrounding scene shows in the reflection
    float   Ambient,       // flat fill so no normal is ever pure black
    float   Wrap = 0f,     // diffuse wrap: 0 = Lambert; ~0.4 lets light bleed past the terminator, which is what skin, wax and flesh do
    Vector3 Scatter = default);   // colour of that bleed, added where light fades out (a cheap subsurface glow); zero = none

public static class SurfacePresets
{
    /// <summary>Blackened forged iron: dark, cool, with a tight hard highlight and a bluish edge.</summary>
    public static readonly SurfaceSpec BlackIron = new(
        Albedo: new(0.030f, 0.030f, 0.034f), F0: new(0.25f, 0.255f, 0.275f),
        Metalness: 0.94f, Roughness: 0.44f, EnvStrength: 0.80f, Ambient: 0.006f);

    /// <summary>Worn steel: lighter, with a broader, brighter sheen where it has been polished by friction.</summary>
    public static readonly SurfaceSpec WornSteel = new(
        Albedo: new(0.050f, 0.050f, 0.054f), F0: new(0.40f, 0.41f, 0.43f),
        Metalness: 0.96f, Roughness: 0.38f, EnvStrength: 0.90f, Ambient: 0.008f);

    /// <summary>Iron gone warm and dull: a brown cast, rougher, less mirror.</summary>
    public static readonly SurfaceSpec AgedIron = new(
        Albedo: new(0.060f, 0.042f, 0.030f), F0: new(0.30f, 0.255f, 0.215f),
        Metalness: 0.82f, Roughness: 0.56f, EnvStrength: 0.62f, Ambient: 0.010f);

    /// <summary>Hemp: warm tan plant fibre. Fully diffuse, rough as felt, with only a faint broad sheen where it grazes the light.</summary>
    public static readonly SurfaceSpec Hemp = new(
        Albedo: new(0.40f, 0.27f, 0.13f), F0: new(0.05f, 0.045f, 0.04f),
        Metalness: 0.0f, Roughness: 0.93f, EnvStrength: 0.05f, Ambient: 0.014f);

    /// <summary>Manila: paler and yellower than hemp, a little silkier.</summary>
    public static readonly SurfaceSpec Manila = new(
        Albedo: new(0.52f, 0.40f, 0.21f), F0: new(0.06f, 0.055f, 0.045f),
        Metalness: 0.0f, Roughness: 0.88f, EnvStrength: 0.06f, Ambient: 0.016f);

    /// <summary>Tarred rope: near-black brown with a dull wet sheen. Reads as a dark rope against a dark scene.</summary>
    public static readonly SurfaceSpec Tarred = new(
        Albedo: new(0.085f, 0.060f, 0.040f), F0: new(0.07f, 0.065f, 0.060f),
        Metalness: 0.0f, Roughness: 0.62f, EnvStrength: 0.20f, Ambient: 0.008f);

    /// <summary>Parasite flesh: sickly olive-green satin (the gloss is a separate WetCoat, since slime comes and goes), wrapped diffuse and a yellow-green terminator glow so it reads as living tissue, not rubber.</summary>
    public static readonly SurfaceSpec ParasiteFlesh = new(
        Albedo: new(0.215f, 0.245f, 0.125f), F0: new(0.04f, 0.04f, 0.035f),
        Metalness: 0.0f, Roughness: 0.50f, EnvStrength: 0.22f, Ambient: 0.020f,
        Wrap: 0.42f, Scatter: new(0.060f, 0.082f, 0.020f));

    /// <summary>A clear liquid film over something else: no colour, only a hard highlight and a reflection of the surround; over a body's matcap it looks wet. F0 is deliberately high (highlight strength, not a real IOR).</summary>
    public static readonly SurfaceSpec WetCoat = new(
        Albedo: new(0f, 0f, 0f), F0: new(0.34f, 0.34f, 0.34f),
        Metalness: 0.0f, Roughness: 0.16f, EnvStrength: 0.95f, Ambient: 0f);

    /// <summary>Rust scale: pure diffuse, orange-brown, nearly matte. Blended over any of the above as patina.</summary>
    public static readonly SurfaceSpec Rust = new(
        Albedo: new(0.110f, 0.044f, 0.020f), F0: new(0.05f, 0.03f, 0.02f),
        Metalness: 0.04f, Roughness: 0.92f, EnvStrength: 0.04f, Ambient: 0.012f);
}

/// <summary>A "material capture": lit colour by facing direction baked to a small table, so shading a vertex is one bilinear lookup (display-space RGB).</summary>
public sealed class Matcap
{
    private const int N = 48;
    private readonly Vector4[] _rgb = new Vector4[N * N];   // w unused: one 16-byte load per texel

    public Matcap(in SurfaceSpec spec)
    {
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float nx = x / (N - 1f) * 2f - 1f;
            float ny = y / (N - 1f) * 2f - 1f;
            float r2 = nx * nx + ny * ny;
            if (r2 > 1f) { float k = 1f / MathF.Sqrt(r2); nx *= k; ny *= k; r2 = 1f; }
            var n = new Vector3(nx, ny, MathF.Sqrt(MathF.Max(0f, 1f - r2)));

            Vector3 c = Shade(n, in spec);
            _rgb[y * N + x] = new Vector4(c, 0f);
        }
    }

    /// <summary>Display-space colour for a unit view-space normal (only X and Y are needed; Z is implied).</summary>
    public Vector3 Sample(float nx, float ny)
    {
        float fx = (nx * 0.5f + 0.5f) * (N - 1);
        float fy = (ny * 0.5f + 0.5f) * (N - 1);
        fx = fx < 0f ? 0f : (fx > N - 1.001f ? N - 1.001f : fx);
        fy = fy < 0f ? 0f : (fy > N - 1.001f ? N - 1.001f : fy);

        int x0 = (int)fx, y0 = (int)fy;
        float tx = fx - x0, ty = fy - y0;
        int i00 = y0 * N + x0;

        float w00 = (1f - tx) * (1f - ty), w10 = tx * (1f - ty), w01 = (1f - tx) * ty, w11 = tx * ty;
        Vector4 c = _rgb[i00] * w00 + _rgb[i00 + 1] * w10 + _rgb[i00 + N] * w01 + _rgb[i00 + N + 1] * w11;
        return new Vector3(c.X, c.Y, c.Z);
    }

    private static Vector3 Shade(Vector3 n, in SurfaceSpec s)
    {
        Vector3 v = Vector3.UnitZ;

        float ndlKey  = MathF.Max(0f, Vector3.Dot(n, StudioLighting.Key));
        float ndlFill = MathF.Max(0f, Vector3.Dot(n, StudioLighting.Fill));
        float ndlRim  = MathF.Max(0f, Vector3.Dot(n, StudioLighting.Rim));

        // ---- diffuse ---- wrapped lighting spills past the terminator; the specular terms keep the unwrapped dots so wrapping doesn't also widen the highlight.
        float dKey = ndlKey, dFill = ndlFill;
        if (s.Wrap > 0f)
        {
            float inv = 1f / (1f + s.Wrap);
            dKey  = MathF.Max(0f, (Vector3.Dot(n, StudioLighting.Key)  + s.Wrap) * inv);
            dFill = MathF.Max(0f, (Vector3.Dot(n, StudioLighting.Fill) + s.Wrap) * inv);
        }
        Vector3 irradiance = StudioLighting.KeyColor * (1.30f * dKey)
                           + StudioLighting.FillColor * (0.22f * dFill)
                           + new Vector3(s.Ambient);
        Vector3 diffuse = s.Albedo * irradiance * (1f - s.Metalness);

        // Subsurface bleed: a glow that peaks where the key light just stops reaching, strongest toward the silhouette.
        if (s.Scatter != Vector3.Zero)
        {
            float terminator = MathF.Exp(-MathF.Pow(Vector3.Dot(n, StudioLighting.Key) / 0.42f, 2f));
            diffuse += s.Scatter * (terminator * (0.35f + 0.65f * (1f - n.Z)) * (1f - s.Metalness));
        }

        // ---- key highlight: tight lobe plus a broad sheen, Fresnel-weighted ----
        Vector3 h = Vector3.Normalize(StudioLighting.Key + v);
        float nh = MathF.Max(0f, Vector3.Dot(n, h));
        float vh = MathF.Max(0f, Vector3.Dot(v, h));
        float pTight = 12f + 520f * (1f - s.Roughness) * (1f - s.Roughness);
        float lobe = MathF.Pow(nh, pTight) * 1.7f + MathF.Pow(nh, MathF.Max(3f, pTight * 0.11f)) * 0.16f;
        float fres = MathF.Pow(1f - vh, 5f);
        Vector3 fSchlick = s.F0 + (Vector3.One - s.F0) * fres;
        Vector3 spec = fSchlick * StudioLighting.KeyColor * (lobe * ndlKey);

        // ---- environment reflection ----
        Vector3 r = 2f * n.Z * n - v;                       // view vector reflected about n
        // Rough metal reflects a blurred surround: blend the sharp environment toward its own average.
        float blur = s.Roughness * s.Roughness * 1.6f;
        blur = blur > 0.85f ? 0.85f : blur;
        Vector3 reflected = Vector3.Lerp(Environment(r), EnvAverage, blur) * s.EnvStrength;
        float grazing = MathF.Pow(1f - n.Z, 3f);            // reflections strengthen toward the silhouette
        Vector3 envF = s.F0 + (Vector3.One - s.F0) * (grazing * 0.55f);
        Vector3 refl = reflected * envF * (0.55f + 0.45f * s.Metalness);

        // ---- cold rim from behind: only where the surface turns away from the viewer ----
        float rim = MathF.Pow(1f - n.Z, 2.4f) * ndlRim;
        Vector3 rimLight = StudioLighting.RimColor * (0.30f * rim) * (0.25f + 0.75f * s.Metalness);

        Vector3 lin = diffuse + spec + refl + rimLight;
        return ToDisplay(lin);
    }

    internal static readonly Vector3 EnvAverage = new(0.050f, 0.055f, 0.068f);

    /// <summary>A mostly dark surround (black ground, cool sky, faint warm horizon, cold backlight): dark on purpose, so iron reads as mirror-like.</summary>
    internal static Vector3 Environment(Vector3 r)
    {
        float up = -r.Y;                                    // screen up is -Y
        float sky = DrawHelpers.Smooth(-0.35f, 0.85f, up);

        Vector3 ground = new(0.006f, 0.006f, 0.008f);
        Vector3 zenith = new(0.085f, 0.100f, 0.135f);
        Vector3 env = Vector3.Lerp(ground, zenith, sky);

        float band = MathF.Exp(-MathF.Pow((up + 0.02f) / 0.20f, 2f));
        env += new Vector3(0.38f, 0.25f, 0.15f) * (band * 0.30f * DrawHelpers.Smooth(-0.6f, 0.4f, r.Z));

        float back = MathF.Max(0f, Vector3.Dot(r, StudioLighting.Rim));
        env += StudioLighting.RimColor * (MathF.Pow(back, 7f) * 0.85f);

        float key = MathF.Max(0f, Vector3.Dot(r, StudioLighting.Key));
        env += StudioLighting.KeyColor * (MathF.Pow(key, 12f) * 0.55f);   // softbox glow in the reflection
        return env;
    }

    /// <summary>Soft-shoulder tone map, then linear to display (sRGB-ish).</summary>
    internal static Vector3 ToDisplay(Vector3 lin)
    {
        static float F(float x)
        {
            x = MathF.Max(0f, x);
            x = 1f - MathF.Exp(-1.55f * x);                // shoulders highlights instead of clipping them
            return MathF.Pow(x, 1f / 2.2f);
        }
        return new Vector3(F(lin.X), F(lin.Y), F(lin.Z));
    }
}

/// <summary>Shared by lit strand materials: far-strand haze, soft cast shadow, growing-tip glint. scale is the strand's size in px.</summary>
internal static class StrandShading
{
    /// <summary>Colour a strand hazes toward as its Depth approaches 1.</summary>
    public static readonly Vector3 DepthFog = new(0.105f, 0.125f, 0.165f);

    public static readonly uint ShadowTint = DrawHelpers.Pack(0.015f, 0.016f, 0.022f);
    public static readonly uint FlareWarm  = DrawHelpers.Pack(1.00f, 0.80f, 0.52f);

    /// <summary>Soft cast band under the strand, displaced away from the key light.</summary>
    public static void DrawShadow(ImDrawListPtr dl, StrandPath path, float scale, float depth, float alpha,
                                  in MaterialContext ctx, bool closed, float visibleLen)
    {
        float a = alpha * 0.40f * (1f - 0.45f * depth);
        if (a <= 0.004f) return;

        float total = path.Length;
        bool full = closed || visibleLen >= total;
        float s0 = closed ? 0f : (full ? -scale * 0.5f : 0f);
        float s1 = closed ? total : (full ? total + scale * 0.5f : visibleLen);
        int rows = Math.Clamp((int)((s1 - s0) / (scale * 0.5f)), 3, 40);
        float reach = scale * 0.60f * (1f - 0.55f * depth);
        Vector2 offset = StudioLighting.ShadowDirection * reach;
        float hw = scale * 0.34f;
        float soft = hw * (0.9f + 1.1f * depth);

        ReadOnlySpan<float> across = stackalloc float[5] { -1f, -0.55f, 0f, 0.55f, 1f };
        ReadOnlySpan<float> prof = stackalloc float[5] { 0f, 0.62f, 1f, 0.62f, 0f };

        int v = 0;
        for (int r = 0; r <= rows; r++)
        {
            float sArc = s0 + (s1 - s0) * r / rows;
            Vector2 p, t;
            if (closed) SampleLoop(path, sArc, total, (s1 - s0) / rows * 0.5f, out p, out t);
            else        path.SampleAtArc(sArc, out p, out t);
            Vector2 nrm = new(-t.Y, t.X);
            float edgeFade = closed ? 1f : MathF.Min(1f, MathF.Min(r, rows - r) / 1.5f);

            for (int c = 0; c < 5; c++)
            {
                float w = across[c];
                MeshDraw.P[v] = p + offset + nrm * (w * (hw + soft * 0.5f * MathF.Abs(w)));
                MeshDraw.C[v] = DrawHelpers.WithAlpha(ShadowTint, a * prof[c] * edgeFade);
                v++;
            }
        }
        MeshDraw.Grid(dl, 4, rows, MeshDraw.WhiteUv(ctx.Time));
    }

    /// <summary>Position and tangent at arc length s on a closed loop; the wrapping central difference hides the seam.</summary>
    private static void SampleLoop(StrandPath path, float s, float total, float h, out Vector2 p, out Vector2 t)
    {
        path.SampleAtArc(Wrap(s, total), out p, out _);
        path.SampleAtArc(Wrap(s + h, total), out Vector2 ahead, out _);
        path.SampleAtArc(Wrap(s - h, total), out Vector2 behind, out _);
        Vector2 d = ahead - behind;
        float len = d.Length();
        t = len > 1e-4f ? d / len : new Vector2(1f, 0f);
    }

    private static float Wrap(float s, float total)
    {
        s %= total;
        return s < 0f ? s + total : s;
    }

    /// <summary>A bright pip with a warm halo, for the tip of a strand that is still growing or flying.</summary>
    public static void DrawFlare(ImDrawListPtr dl, Vector2 tip, float bar, float k, in MaterialContext ctx)
    {
        if (k <= 0.004f) return;
        Span<float> rr = stackalloc float[2] { bar * 1.1f, bar * 3.0f };
        Span<uint> cc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(FlareWarm, 0.30f * k),
            DrawHelpers.WithAlpha(FlareWarm, 0f),
        };
        MeshDraw.Radial(dl, tip, MeshDraw.WhiteUv(ctx.Time), DrawHelpers.WithAlpha(0xFFFFFFFFu, 0.85f * k),
                        10, rr, cc, 0f, 1f, 0, 0f);
    }
}

/// <summary>How a viscous liquid answers light (slime, blood, sludge, venom), authored in linear space; a LiquidMatcap bakes it to display colour and opacity. Cues: translucent, richer where thick, a caustic crescent on the shadow side, a glossy highlight plus surround reflection.</summary>
public readonly record struct LiquidSpec(
    Vector3 Thin,
    Vector3 Deep,
    float Opacity,
    float EdgeOpacity,
    float Gloss,
    float Caustic,
    float Env);

/// <summary>Ready-made liquids. A new viscous effect starts here: add a preset, then a three-line ParticleGoop subclass; drip simulation, mesh and shading are shared.</summary>
public static class LiquidPresets
{
    /// <summary>Sickly yellow-green mucus: translucent, glossy, with a bright caustic.</summary>
    public static readonly LiquidSpec Slime = new(
        Thin: new(0.62f, 0.72f, 0.16f), Deep: new(0.045f, 0.105f, 0.012f),
        Opacity: 0.90f, EdgeOpacity: 0.55f, Gloss: 0.88f, Caustic: 1.15f, Env: 1.0f);
}

/// <summary>A LiquidSpec baked into a lookup by view-space normal (as Matcap does for opaque surfaces) with an extra opacity channel; straight, not premultiplied, display colour. Drop, neck and tail are surfaces of revolution with analytic normals, so this table is the whole shading model.</summary>
public sealed class LiquidMatcap
{
    private const int N = 64;
    private readonly Vector4[] _tex = new Vector4[N * N];   // rgb = display colour, w = opacity

    public LiquidMatcap(in LiquidSpec spec)
    {
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float nx = x / (N - 1f) * 2f - 1f;
            float ny = y / (N - 1f) * 2f - 1f;
            float r2 = nx * nx + ny * ny;
            if (r2 > 1f) { float k = 1f / MathF.Sqrt(r2); nx *= k; ny *= k; r2 = 1f; }
            var n = new Vector3(nx, ny, MathF.Sqrt(MathF.Max(0f, 1f - r2)));
            _tex[y * N + x] = Shade(n, in spec);
        }
    }

    /// <summary>Display colour (xyz) and opacity (w) for a unit view-space normal; only X and Y are needed.</summary>
    public Vector4 Sample(float nx, float ny)
    {
        float fx = (nx * 0.5f + 0.5f) * (N - 1);
        float fy = (ny * 0.5f + 0.5f) * (N - 1);
        fx = fx < 0f ? 0f : (fx > N - 1.001f ? N - 1.001f : fx);
        fy = fy < 0f ? 0f : (fy > N - 1.001f ? N - 1.001f : fy);

        int x0 = (int)fx, y0 = (int)fy;
        float tx = fx - x0, ty = fy - y0;
        int i = y0 * N + x0;
        return _tex[i] * ((1f - tx) * (1f - ty)) + _tex[i + 1] * (tx * (1f - ty))
             + _tex[i + N] * ((1f - tx) * ty)    + _tex[i + N + 1] * (tx * ty);
    }

    private static Vector4 Shade(Vector3 n, in LiquidSpec s)
    {
        float nz = n.Z;
        float rimness = 1f - nz;                              // 0 at the middle of a bead, 1 at its silhouette
        float thick = MathF.Pow(nz, 0.75f);                   // how much liquid the view ray crosses

        // ---- body: light that enters, is tinted by the liquid, and leaves toward the viewer ----
        Vector3 body = Vector3.Lerp(s.Thin, s.Deep, thick);
        float keyWrap = MathF.Max(0f, (Vector3.Dot(n, StudioLighting.Key) + 0.55f) / 1.55f);
        Vector3 light = StudioLighting.KeyColor * (0.28f + 0.85f * keyWrap) + StudioLighting.FillColor * 0.42f;
        body *= light;
        // A thin dark line at the silhouette: the liquid refracts the edge of the scene, which gives it definition against a bright background.
        body *= 1f - 0.34f * DrawHelpers.Smooth(0.80f, 1.0f, rimness);

        float opacity = DrawHelpers.Lerp(s.EdgeOpacity, s.Opacity, thick);

        // ---- light added on top of the body ----
        Vector3 add = Vector3.Zero;

        // Hard key highlight and a broader, dimmer sheen under it.
        Vector3 h = Vector3.Normalize(StudioLighting.Key + Vector3.UnitZ);
        float nh = MathF.Max(0f, Vector3.Dot(n, h));
        // Per-vertex shading can't carry a lobe narrower than the vertex spacing, so this one stays wide enough to survive interpolation; GoopDraw lays an analytic glint over it.
        float sharp = 30f + 240f * s.Gloss * s.Gloss;
        add += StudioLighting.KeyColor * (MathF.Pow(nh, sharp) * 1.5f + MathF.Pow(nh, 14f) * 0.20f);

        // A smaller, cooler window reflected from the fill side: the second highlight is what makes a drop read as wet rather than shiny.
        Vector3 hf = Vector3.Normalize(StudioLighting.Fill + Vector3.UnitZ);
        float nhf = MathF.Max(0f, Vector3.Dot(n, hf));
        add += new Vector3(0.80f, 0.92f, 1.0f) * (MathF.Pow(nhf, sharp * 0.5f) * 0.30f);

        // The surround, reflected at grazing angles (Schlick, dielectric).
        float fres = 0.04f + 0.96f * MathF.Pow(rimness, 4.5f);
        Vector3 r = n * (2f * nz) - Vector3.UnitZ;
        add += Matcap.Environment(r) * (s.Env * fres * 1.7f);

        // Caustic: the key light is bent by the bead and lands in a crescent on the side facing away from it, tinted by the body.
        float rl = MathF.Sqrt(n.X * n.X + n.Y * n.Y);
        if (rl > 1e-4f)
        {
            Vector2 away = new(-StudioLighting.Key.X, -StudioLighting.Key.Y);
            away /= away.Length();
            float facing = MathF.Max(0f, (n.X * away.X + n.Y * away.Y) / rl);
            float crescent = MathF.Pow(facing, 2.6f) * DrawHelpers.Smooth(0.28f, 0.92f, rimness) * (1f - 0.35f * DrawHelpers.Smooth(0.92f, 1f, rimness));
            add += (s.Thin * 1.9f + new Vector3(0.10f)) * (crescent * s.Caustic);
        }

        // ---- composite: overlay light also raises opacity, so highlights stay solid over a dark scene ----
        float lum = add.X * 0.30f + add.Y * 0.59f + add.Z * 0.11f;
        float alpha = opacity + (1f - opacity) * Math.Clamp(lum * 1.5f, 0f, 1f);
        Vector3 premult = body * opacity + add;
        Vector3 straight = premult / MathF.Max(alpha, 1e-3f);
        Vector3 display = Matcap.ToDisplay(straight);
        return new Vector4(display, Math.Clamp(alpha, 0f, 1f));
    }


}

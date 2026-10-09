using System.Numerics;
namespace RealDebuffs.Effects.Framework;

/// <summary>
/// How a viscous liquid answers light: slime, blood, sludge, venom. Authored in linear space like
/// <see cref="SurfaceSpec"/>; a <see cref="LiquidMatcap"/> bakes it to display colour and opacity.
///
/// A liquid is not an opaque surface. It is translucent (the world shows through its thin parts), it is
/// darker and richer where it is thick, it focuses the key light into a bright crescent on its shadow
/// side, and its surface is glossy enough to carry a hard highlight and a reflection of the surround.
/// Those four cues are what separate a wet drop from a painted blob, and each is a field here.
/// </summary>
/// <param name="Thin">Colour where the liquid is thin (toward the silhouette): lighter, more transmissive.</param>
/// <param name="Deep">Colour where the liquid is thick (the middle of a bead): the absorption colour.</param>
/// <param name="Opacity">How much of the scene the thickest part hides, 0..1.</param>
/// <param name="EdgeOpacity">How much the thin silhouette hides, 0..1, before reflections are added.</param>
/// <param name="Gloss">0..1: how sharp the highlight is. Thicker liquids hold a rounder, softer one.</param>
/// <param name="Caustic">Brightness of the crescent of focused light on the side away from the key.</param>
/// <param name="Env">Strength of the reflected surround at grazing angles.</param>
public readonly record struct LiquidSpec(
    Vector3 Thin,
    Vector3 Deep,
    float Opacity,
    float EdgeOpacity,
    float Gloss,
    float Caustic,
    float Env);

/// <summary>
/// Ready-made liquids. A new viscous effect (bleeding, sludge, poison) starts here: add a preset, then a
/// three-line <c>ParticleGoop</c> subclass that names it. Everything else (the drip simulation, the
/// drop-and-thread mesh, the shading) is shared.
/// </summary>
public static class LiquidPresets
{
    /// <summary>Sickly yellow-green mucus: translucent, glossy, with a bright caustic.</summary>
    public static readonly LiquidSpec Slime = new(
        Thin: new(0.62f, 0.72f, 0.16f), Deep: new(0.045f, 0.105f, 0.012f),
        Opacity: 0.90f, EdgeOpacity: 0.55f, Gloss: 0.88f, Caustic: 1.15f, Env: 1.0f);
}

/// <summary>
/// A <see cref="LiquidSpec"/> baked into a lookup by view-space normal, exactly as <see cref="Matcap"/> is
/// for opaque surfaces, with one extra channel: opacity. Colour is straight (not premultiplied) display
/// colour, so it drops into the same vertex-colour pipeline as every other mesh material.
///
/// The bead of a drop, the neck above it and the tail behind it are all surfaces of revolution, so each
/// vertex has an analytic normal and this table is the entire shading model: no per-pixel work, and the
/// highlight stays crisp because it is baked at higher resolution than a Matcap.
/// </summary>
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
        body *= 1f - 0.34f * Smooth(0.80f, 1.0f, rimness);

        float opacity = Lerp(s.EdgeOpacity, s.Opacity, thick);

        // ---- light added on top of the body ----
        Vector3 add = Vector3.Zero;

        // Hard key highlight and a broader, dimmer sheen under it.
        Vector3 h = Vector3.Normalize(StudioLighting.Key + Vector3.UnitZ);
        float nh = MathF.Max(0f, Vector3.Dot(n, h));
        // Per-vertex shading cannot carry a lobe narrower than the vertex spacing, so this one is kept wide
        // enough to survive interpolation; GoopDraw lays an analytic glint over it for the hard core.
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
            float crescent = MathF.Pow(facing, 2.6f) * Smooth(0.28f, 0.92f, rimness) * (1f - 0.35f * Smooth(0.92f, 1f, rimness));
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

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

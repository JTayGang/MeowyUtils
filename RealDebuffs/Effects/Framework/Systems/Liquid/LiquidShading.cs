namespace RealDebuffs.Effects.Framework;

/// How a liquid answers light (water, blood, slime, venom), authored in linear space; Viscosity is its thickness for LiquidPhysics
public readonly record struct LiquidSpec(
    Vector3 Thin,
    Vector3 Deep,
    float Opacity,
    float EdgeOpacity,
    float Gloss,
    float Caustic,
    float Env,
    float Sheen = 0.20f,
    float Window = 0.30f,
    float Viscosity = 1f);

/// Ready-made liquids.
public static class LiquidPresets
{
    /// Sickly yellow-green mucus: translucent, glossy, with a bright caustic.
    public static readonly LiquidSpec Slime = new(
        Thin: new(0.62f, 0.72f, 0.16f), Deep: new(0.045f, 0.105f, 0.012f),
        Opacity: 0.90f, EdgeOpacity: 0.55f, Gloss: 0.88f, Caustic: 1.15f, Env: 1.0f, Viscosity: 1f);

    /// Blood: arterial red where thin, nearly black where deep, almost opaque, with a hard wet highlight.
    public static readonly LiquidSpec Blood = new(
        Thin: new(0.55f, 0.008f, 0.012f), Deep: new(0.17f, 0.000f, 0.004f),
        Opacity: 0.97f, EdgeOpacity: 0.80f, Gloss: 0.92f, Caustic: 0.30f, Env: 0.12f, Sheen: 0.02f, Window: 0.12f, Viscosity: LiquidPhysics.BloodAt);

    /// Clear water: mostly see-through, so it reads by its hard highlights, bright refracted lower edge and dark rim.
    public static readonly LiquidSpec Water = new(
        Thin: new(0.36f, 0.66f, 0.82f), Deep: new(0.010f, 0.075f, 0.118f),
        Opacity: 0.32f, EdgeOpacity: 0.70f, Gloss: 0.98f, Caustic: 1.5f, Env: 1.5f, Sheen: 0.30f, Window: 0.55f, Viscosity: 0f);
}

/// A LiquidSpec baked into a lookup by view-space normal (like Matcap), with an opacity channel.
public sealed class LiquidMatcap
{
    const int N = 64;
    readonly Vector4[] _tex = new Vector4[N * N];  // rgb = display colour, w = opacity

    public LiquidMatcap(in LiquidSpec spec)
    {
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float nx = x / (N - 1f) * 2f - 1f;
            float ny = y / (N - 1f) * 2f - 1f;
            float r2 = nx * nx + ny * ny;
            if (r2 > 1f) { float k = 1f / Sqrt(r2); nx *= k; ny *= k; r2 = 1f; }
            var n = new Vector3(nx, ny, Sqrt(Max(0f, 1f - r2)));
            _tex[y * N + x] = Shade(n, in spec);
        }
    }

    /// Display colour (xyz) and opacity (w) for a unit view-space normal; only X and Y are needed.
    public Vector4 Sample(float nx, float ny)
    {
        float fx = (nx * 0.5f + 0.5f) * (N - 1);
        float fy = (ny * 0.5f + 0.5f) * (N - 1);
        fx = Math.Clamp(fx, 0f, N - 1.001f);
        fy = Math.Clamp(fy, 0f, N - 1.001f);

        int x0 = (int)fx, y0 = (int)fy;
        float tx = fx - x0, ty = fy - y0;
        int i = y0 * N + x0;
        return _tex[i] * ((1f - tx) * (1f - ty)) + _tex[i + 1] * (tx * (1f - ty))
             + _tex[i + N] * ((1f - tx) * ty)    + _tex[i + N + 1] * (tx * ty);
    }

    static Vector4 Shade(Vector3 n, in LiquidSpec s)
    {
        float nz = n.Z;
        float rimness = 1f - nz;  // 0 at the middle of a bead, 1 at its silhouette
        float thick = Pow(nz, 0.75f);  // how much liquid the view ray crosses

        // body: light that enters, is tinted by the liquid, and leaves toward the viewer
        Vector3 body = Vector3.Lerp(s.Thin, s.Deep, thick);
        float keyWrap = Max(0f, (Vector3.Dot(n, StudioLighting.Key) + 0.55f) / 1.55f);
        Vector3 light = StudioLighting.KeyColor * (0.28f + 0.85f * keyWrap) + StudioLighting.FillColor * 0.42f;
        body *= light;
        // A thin dark line at the silhouette: the liquid refracts the edge of the scene
        body *= 1f - 0.34f * Smooth(0.80f, 1.0f, rimness);

        float opacity = Lerp(s.EdgeOpacity, s.Opacity, thick);

        // light added on top of the body
        Vector3 add = Vector3.Zero;

        // Hard key highlight and a broader, dimmer sheen under it.
        Vector3 h = Vector3.Normalize(StudioLighting.Key + Vector3.UnitZ);
        float nh = Max(0f, Vector3.Dot(n, h));
        // Per-vertex shading can't carry a lobe narrower than the vertex spacing
        float sharp = 30f + 240f * s.Gloss * s.Gloss;
        add += StudioLighting.KeyColor * (Pow(nh, sharp) * 1.5f + Pow(nh, 14f) * s.Sheen);

        // A smaller, cooler window reflected from the fill side: the second highlight makes a drop read as wet, not shiny.
        Vector3 hf = Vector3.Normalize(StudioLighting.Fill + Vector3.UnitZ);
        float nhf = Max(0f, Vector3.Dot(n, hf));
        add += new Vector3(0.80f, 0.92f, 1.0f) * (Pow(nhf, sharp * 0.5f) * s.Window);

        // The surround, reflected at grazing angles (Schlick, dielectric).
        float fres = 0.04f + 0.96f * Pow(rimness, 4.5f);
        Vector3 r = n * (2f * nz) - Vector3.UnitZ;
        add += Matcap.Environment(r) * (s.Env * fres * 1.7f);

        // Caustic: the key light bent by the bead lands in a crescent on the side facing away from it, tinted by the body.
        float rl = Sqrt(n.X * n.X + n.Y * n.Y);
        if (rl > 1e-4f)
        {
            Vector2 away = new(-StudioLighting.Key.X, -StudioLighting.Key.Y);
            away /= away.Length();
            float facing = Max(0f, (n.X * away.X + n.Y * away.Y) / rl);
            float crescent = Pow(facing, 2.6f) * Smooth(0.28f, 0.92f, rimness) * (1f - 0.35f * Smooth(0.92f, 1f, rimness));
            add += (s.Thin * 1.9f + new Vector3(0.10f)) * (crescent * s.Caustic);
        }

        // composite: overlay light also raises opacity, so highlights stay solid over a dark scene
        float lum = add.X * 0.30f + add.Y * 0.59f + add.Z * 0.11f;
        float alpha = Lerp(opacity, 1f, Saturate(lum * 1.5f));
        Vector3 premult = body * opacity + add;
        Vector3 straight = premult / Max(alpha, 1e-3f);
        Vector3 display = Matcap.ToDisplay(straight);
        return new(display, Saturate(alpha));
    }

}

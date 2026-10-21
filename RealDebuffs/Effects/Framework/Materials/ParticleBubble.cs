using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A bubble of gas blistering the surface of a thick liquid: a thin domed film, clear in the middle and denser toward its rim, with the key light's window caught
/// in its upper left and a faint kick of light on the far side. Variant 0 is the blister, which swells over its life; variant 1 is its pop, a quick ring that
/// widens and fades. Size is the full-grown radius in px; the effect decides when one bursts and throws the spray itself.
/// </summary>
public sealed class ParticleBubble : IParticleMaterial
{
    public string Name => "particle.bubble";
    public string[] NaturalLanguageWords { get; } = { "bubble", "bubbles", "bubbling", "foam", "froth", "fizz" };

    // Authored in display colour. The film takes the liquid's tint; the light on it does not.
    private static readonly uint FilmThin  = DrawHelpers.Pack(0.52f, 0.92f, 0.14f);
    private static readonly uint FilmThick = DrawHelpers.Pack(0.20f, 0.52f, 0.05f);
    private static readonly uint Contact   = DrawHelpers.Pack(0.015f, 0.050f, 0.008f);
    private static readonly uint Window    = DrawHelpers.Pack(1.00f, 0.99f, 0.90f);
    private static readonly uint Kick      = DrawHelpers.Pack(0.70f, 1.00f, 0.55f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f || p.Size <= 0.6f) return;
        bool ov = DrawHelpers.ColorOverrideActive;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        Vector2 c = p.DrawPos;

        if (p.Variant == 1) { DrawPop(dl, c, p.Size, p.AgeRatio, alpha, ov, uv); return; }

        // Blisters swell, then tremble just before they go.
        float swell = 0.30f + 0.70f * DrawHelpers.Smooth(p.AgeRatio / 0.88f);
        float tremble = p.AgeRatio > 0.88f ? 1f + 0.05f * MathF.Sin(p.AgeRatio * 220f + p.Seed) : 1f;
        float r = p.Size * swell * tremble;
        if (r < 0.8f) return;
        float born = DrawHelpers.Saturate(p.AgeRatio / 0.08f);
        float a = alpha * born;

        Span<float> rr = stackalloc float[5];
        Span<uint> cc = stackalloc uint[5];

        // A fleck of foam is too small for a window and a shadow: a film, a rim and one glint say all there is to see, for a fifth of the geometry.
        if (r < 4.6f)
        {
            rr[0] = r * 0.55f; rr[1] = r * 0.95f; rr[2] = r * 1.12f;
            cc[0] = DrawHelpers.Tint(FilmThick, a * 0.30f, ov);
            cc[1] = DrawHelpers.Tint(FilmThin, a * 0.62f, ov);
            cc[2] = DrawHelpers.Tint(FilmThin, 0f, ov);
            MeshDraw.Radial(dl, c, uv, DrawHelpers.Tint(FilmThin, a * 0.12f, ov), 9, rr[..3], cc[..3], 0f, 1f, p.Seed, 0.04f);
            rr[0] = MathF.Max(0.5f, r * 0.20f); rr[1] = MathF.Max(0.9f, r * 0.42f);
            cc[0] = DrawHelpers.Tint(Window, a * 0.95f, ov); cc[1] = DrawHelpers.Tint(Window, 0f, ov);
            MeshDraw.Radial(dl, c + new Vector2(-0.34f, -0.36f) * r, uv, cc[0], 6, rr[..2], cc[..2], 0f, 1f, 0, 0f);
            return;
        }

        // The dark where the film meets the liquid, pooled on the side away from the light.
        Vector2 sh = c + new Vector2(0.10f, 0.13f) * r;
        rr[0] = r * 0.70f; rr[1] = r * 0.98f; rr[2] = r * 1.14f;
        cc[0] = DrawHelpers.Tint(Contact, 0f, ov); cc[1] = DrawHelpers.Tint(Contact, a * 0.50f, ov); cc[2] = DrawHelpers.Tint(Contact, 0f, ov);
        MeshDraw.Radial(dl, sh, uv, cc[0], 14, rr[..3], cc[..3], 0f, 1f, p.Seed, 0.06f);

        // The film: clear in the middle, denser toward the rim, with a bright thin line where the dome meets the liquid.
        rr[0] = r * 0.55f; rr[1] = r * 0.84f; rr[2] = r * 0.96f; rr[3] = r * 1.02f; rr[4] = r * 1.07f;
        cc[0] = DrawHelpers.Tint(FilmThin, a * 0.14f, ov);
        cc[1] = DrawHelpers.Tint(FilmThick, a * 0.30f, ov);
        cc[2] = DrawHelpers.Tint(FilmThick, a * 0.46f, ov);
        cc[3] = DrawHelpers.Tint(FilmThin, a * 0.70f, ov);
        cc[4] = DrawHelpers.Tint(FilmThin, 0f, ov);
        MeshDraw.Radial(dl, c, uv, DrawHelpers.Tint(FilmThin, a * 0.10f, ov), 18, rr, cc, 0f, 1f, p.Seed + 5, 0.05f);

        // The window of the key light, long and curved to the dome, high on the lit side.
        float wl = r * 0.46f, ww = MathF.Max(0.9f, r * 0.13f);
        Vector2 wc = c + new Vector2(-0.40f, -0.43f) * r;
        Vector2 along = Vector2.Normalize(new Vector2(1f, -1f)), across = new(-along.Y, along.X);
        Span<Vector2> rim = stackalloc Vector2[8];
        Span<uint> rimCol = stackalloc uint[8];
        for (int pass = 0; pass < 2; pass++)
        {
            float sc = pass == 0 ? 1.7f : 0.8f;
            for (int j = 0; j < 8; j++)
            {
                float ang = MathF.Tau * j / 8f;
                rim[j] = wc + along * (MathF.Cos(ang) * wl * sc) + across * (MathF.Sin(ang) * ww * sc);
                rimCol[j] = DrawHelpers.Tint(Window, pass == 0 ? 0f : a * 0.50f, ov);
            }
            MeshDraw.Fan(dl, uv, wc, DrawHelpers.Tint(Window, pass == 0 ? a * 0.30f : a * 0.92f, ov), rim, rimCol);
        }

        // Light that comes through the dome and lands on the far side.
        if (r > 3f)
        {
            rr[0] = r * 0.10f; rr[1] = r * 0.24f;
            cc[0] = DrawHelpers.Tint(Kick, a * 0.45f, ov); cc[1] = DrawHelpers.Tint(Kick, 0f, ov);
            MeshDraw.Radial(dl, c + new Vector2(0.45f, 0.52f) * r, uv, DrawHelpers.Tint(Kick, a * 0.55f, ov), 8, rr[..2], cc[..2], 0f, 1f, 0, 0f);
        }
    }

    /// <summary>The film tearing: a thin bright ring that races outward and thins, over a faint breath of mist.</summary>
    private static void DrawPop(ImDrawListPtr dl, Vector2 c, float size, float age, float alpha, bool ov, Vector2 uv)
    {
        float k = DrawHelpers.EaseOutCubic(age);
        float r = size * (1.0f + 0.9f * k);
        float fade = (1f - age) * (1f - age);
        float a = alpha * fade;
        if (a <= 0.004f) return;
        float w = MathF.Max(0.9f, size * 0.10f * (1f - 0.6f * age));

        Span<float> rr = stackalloc float[4];
        Span<uint> cc = stackalloc uint[4];
        rr[0] = r - w * 2.5f; rr[1] = r - w; rr[2] = r; rr[3] = r + w * 1.6f;
        cc[0] = DrawHelpers.Tint(FilmThin, 0f, ov);
        cc[1] = DrawHelpers.Tint(Window, a * 0.30f, ov);
        cc[2] = DrawHelpers.Tint(Window, a * 0.85f, ov);
        cc[3] = DrawHelpers.Tint(FilmThin, 0f, ov);
        MeshDraw.Radial(dl, c, uv, DrawHelpers.Tint(FilmThin, 0f, ov), 16, rr, cc, 0f, 1f, 0, 0f);
    }
}

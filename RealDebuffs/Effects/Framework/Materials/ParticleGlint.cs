using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A four-point specular sparkle: the flash of light off a facet. Brightness is the twinkle itself
/// (the effect drives it, so it can flare and die), which also scales the size so a fading glint
/// shrinks into its center instead of just dimming. Size is the long ray length in pixels; Seed
/// rotates it and varies the proportions.
/// </summary>
public sealed class ParticleGlint : IParticleMaterial
{
    public string Name => "particle.glint";
    public string[] NaturalLanguageWords { get; } = { "glint", "glints", "sparkle", "sparkles", "glitter" };

    private static readonly uint Cold = FireColor.Pack(0.72f, 0.88f, 1.00f);
    private static readonly uint Hot  = FireColor.Pack(1.00f, 1.00f, 1.00f);

    // Alternating long / short rays: tip, notch, tip, notch ...
    private const int Rays = 8;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.01f || p.Size <= 1f) return;

        float inten = Math.Min(1f, k);
        float R = p.Size * (0.30f + 0.70f * MathF.Sqrt(inten));
        float rot = DrawHelpers.Hash01(p.Seed + 1) * MathF.PI * 0.5f;
        float squash = 0.55f + 0.30f * DrawHelpers.Hash01(p.Seed + 2);      // short rays vary a little per glint
        Vector2 c = p.Position;

        uint centerCol = DrawHelpers.WithAlpha(Hot, inten);
        uint notchCol  = DrawHelpers.WithAlpha(Cold, inten * 0.55f);
        uint tipCol    = DrawHelpers.WithAlpha(Cold, 0f);

        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);

        // Soft bloom behind the star, so a small glint still reads as a flash of light, not a scratch.
        Span<float> hr = stackalloc float[1] { R * 0.60f };
        Span<uint> hc = stackalloc uint[1] { DrawHelpers.WithAlpha(Cold, 0f) };
        MeshDraw.Radial(dl, c, uv, DrawHelpers.WithAlpha(Cold, inten * 0.50f), 10, hr, hc, rot, squashY: 1f, seed: p.Seed, irregular: 0f);

        Span<Vector2> rim = stackalloc Vector2[Rays * 2];
        Span<uint> rimCol = stackalloc uint[Rays * 2];
        for (int j = 0; j < Rays; j++)
        {
            float ang = rot + j * (MathF.Tau / Rays);
            float tipR = (j & 1) == 0 ? R : R * 0.34f * squash;
            float nAng = ang + MathF.Tau / Rays * 0.5f;
            float notchR = R * 0.15f;
            rim[j * 2]     = c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * tipR;
            rim[j * 2 + 1] = c + new Vector2(MathF.Cos(nAng), MathF.Sin(nAng)) * notchR;
            rimCol[j * 2]     = tipCol;
            rimCol[j * 2 + 1] = notchCol;
        }
        MeshDraw.Fan(dl, uv, c, centerCol, rim, rimCol);

        dl.AddCircleFilled(c, MathF.Max(0.8f, R * 0.09f), DrawHelpers.WithAlpha(Hot, inten));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;
}

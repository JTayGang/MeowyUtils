using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A soft, cold, slow puff: condensation or breath hanging in freezing air. The same irregular radial
/// mesh as smoke but pale blue-white, very translucent, and it swells as it fades instead of rising.
/// Size is the final radius in pixels; AgeRatio runs 0..1 over its life.
/// </summary>
public sealed class ParticleMist : IParticleMaterial
{
    public string Name => "particle.mist";
    public string[] NaturalLanguageWords { get; } = { "vapor", "vapour", "breath" };

    private static readonly uint Cold = FireColor.Pack(0.74f, 0.86f, 0.98f);
    private const int Segs = 12;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.003f || p.Size <= 2f) return;

        float age = p.AgeRatio;
        float fade = MathF.Min(age / 0.25f, 1f) * (1f - FireNoise.Smooth((age - 0.45f) / 0.55f));   // in, hold, out
        float radius = p.Size * (0.55f + 0.45f * FireNoise.Smooth(age));
        float a = k * fade * 0.20f;
        if (a <= 0.002f || radius <= 2f) return;

        Span<float> rr = stackalloc float[2] { radius * 0.50f, radius };
        Span<uint> cc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(Cold, a * 0.60f),
            DrawHelpers.WithAlpha(Cold, 0f),
        };
        float rot = DrawHelpers.Hash01(p.Seed + 9) * MathF.Tau;
        MeshDraw.Radial(dl, p.Position + new Vector2(p.Sway, 0f), MeshDraw.WhiteUv(ctx.Time),
                        DrawHelpers.WithAlpha(Cold, a), Segs, rr, cc, rot, squashY: 0.80f, seed: p.Seed, irregular: 0.50f);
    }

    public ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;
}

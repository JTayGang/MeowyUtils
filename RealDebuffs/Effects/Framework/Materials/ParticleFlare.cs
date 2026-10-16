using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A soft burst of light with a white-hot heart and an exponential-looking falloff, like the glare where a discharge touches down or a bright bolt blooms in a lens. Size is its radius in px.
/// It has no envelope of its own beyond a short fade-in and fade-out at the ends of AgeRatio, so an effect can hold one steady (AgeRatio 0.5) and drive it with Brightness and Size, or let it live and die.
/// </summary>
public sealed class ParticleFlare : IParticleMaterial
{
    public string Name => "particle.flare";
    public string[] NaturalLanguageWords { get; } = { "flash", "flashes", "flare", "flares", "glare" };

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 1f) return;

        float life = DrawHelpers.Smooth(p.AgeRatio / 0.10f) * (1f - DrawHelpers.Smooth((p.AgeRatio - 0.25f) / 0.75f));
        k *= life;
        if (k <= 0.004f) return;

        float r = p.Size;
        Span<float> rr = stackalloc float[3] { r * 0.14f, r * 0.42f, r };
        Span<uint> cc = stackalloc uint[3]
        {
            DrawHelpers.WithAlpha(StormColor.Glow.Sample(0.92f), 0.60f * k),
            DrawHelpers.WithAlpha(StormColor.Glow.Sample(0.62f), 0.22f * k),
            DrawHelpers.WithAlpha(StormColor.Glow.Sample(0.45f), 0f),
        };
        MeshDraw.Radial(dl, p.DrawPos, MeshDraw.WhiteUv(ctx.Time), DrawHelpers.WithAlpha(StormColor.Glow.Sample(1.00f), 0.95f * k),
                        18, rr, cc, 0f, 1f, 0, 0f);
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    // Used as an emitter it just glows where it was dropped.
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Flash,
            DensityPer100px: 0.12f,
            SpeedMin: 0f, SpeedMax: 6f,
            LifespanMin: 0.25f, LifespanMax: 0.5f,
            SizeMin: 18f, SizeMax: 36f,
            SpreadRadians: MathF.PI,
            BiasVelocity: Vector2.Zero),
    };
}

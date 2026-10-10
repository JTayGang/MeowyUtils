using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Dust: powdery particulate that is lit by the scene, in two sizes (ParticlePrimitive.Variant):
///
///  - 0, PUFF: a soft billow thrown up by an impact. Two stacked radial gradients: a dim cool body
///    in shadow, and a smaller warm one offset toward the key light, so the cloud has a lit side
///    and reads as a volume rather than a gray disc. Its outline is irregular and it keeps
///    expanding as it thins out.
///  - 1, MOTE: a speck hanging in the air, so small it is nearly a point, that twinkles as it
///    drifts through the light. Dozens of these are what make a still scene feel like a room with
///    air in it.
///
/// Nothing here is chain-specific: it is the general "settled dirt in the air" material, used for
/// impacts, ambient atmosphere, and anything "made of dust".
/// </summary>
public sealed class ParticleDust : IParticleMaterial
{
    public string Name => "particle.dust";
    public string[] NaturalLanguageWords { get; } = { "dust", "dusty", "dirt", "grit" };

    private static readonly uint Lit    = FireColor.Pack(0.64f, 0.57f, 0.47f);   // sunlit powder
    private static readonly uint Shadow = FireColor.Pack(0.19f, 0.19f, 0.21f);   // the side facing away
    private static readonly uint Mote   = FireColor.Pack(1.00f, 0.92f, 0.78f);

    private const int Segs = 10;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.003f || p.Size <= 0.3f) return;

        Vector2 pos = p.Position + new Vector2(p.Sway, 0f);
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);

        if (p.Variant == 1)
        {
            DrawMote(dl, pos, uv, in p, ctx.Time, k);
            return;
        }

        float age = p.AgeRatio;
        float radius = p.Size * (0.55f + 1.10f * FireNoise.Smooth(age));    // billows outward
        if (radius <= 1f) return;

        // Thins out as it expands. Quick onset so a fresh impact is dense.
        float density = k * 0.46f * (1f - 0.60f * FireNoise.Smooth(age));
        float rot = DrawHelpers.Hash01(p.Seed + 9) * MathF.Tau;

        Span<float> rr = stackalloc float[2] { radius * 0.5f, radius };

        // Body: shadow-side colour, fills the whole puff.
        Span<uint> body = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(Shadow, density * 0.72f),
            DrawHelpers.WithAlpha(Shadow, 0f),
        };
        MeshDraw.Radial(dl, pos, uv, DrawHelpers.WithAlpha(Shadow, density),
                        Segs, rr, body, rot, squashY: 0.86f, seed: p.Seed, irregular: 0.55f);

        // Lit side: a smaller, warmer blob pulled toward the key light.
        Vector2 toLight = new(StudioLighting.Key.X, StudioLighting.Key.Y);
        Vector2 litPos = pos + toLight * (radius * 0.30f);
        Span<float> lr = stackalloc float[2] { radius * 0.34f, radius * 0.70f };
        Span<uint> lit = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(Lit, density * 0.60f),
            DrawHelpers.WithAlpha(Lit, 0f),
        };
        MeshDraw.Radial(dl, litPos, uv, DrawHelpers.WithAlpha(Lit, density * 0.85f),
                        Segs, lr, lit, rot + 1.3f, squashY: 0.88f, seed: p.Seed + 77, irregular: 0.45f);
    }

    private static void DrawMote(ImDrawListPtr dl, Vector2 pos, Vector2 uv, in ParticlePrimitive p, float time, float k)
    {
        // Twinkle: the speck brightens as it drifts through the beam and dims as it leaves it.
        float tw = 0.30f + 0.70f * MathF.Pow(0.5f + 0.5f * MathF.Sin(time * (1.3f + DrawHelpers.Hash01(p.Seed) * 2.2f) + p.Seed * 0.37f), 2f);
        float a = k * tw * 0.62f;
        if (a <= 0.004f) return;

        float r = p.Size;
        Span<float> rr = stackalloc float[1] { r * 2.6f };
        Span<uint> cc = stackalloc uint[1] { DrawHelpers.WithAlpha(Mote, 0f) };
        MeshDraw.Radial(dl, pos, uv, DrawHelpers.WithAlpha(Mote, a * 0.55f),
                        6, rr, cc, rotation: 0f, squashY: 1f, seed: p.Seed, irregular: 0f);
        dl.AddCircleFilled(pos, MathF.Max(0.6f, r * 0.55f), DrawHelpers.WithAlpha(Mote, a));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    // Dust sifting off another effect's strokes: slow, soft, falling away.
    private static readonly StrokeEmission[] EmissionSpecs =
    {
        new(Role: PrimitiveRole.Dust,
            DensityPer100px: 0.55f,
            SpeedMin: 4f, SpeedMax: 18f,
            LifespanMin: 1.8f, LifespanMax: 3.4f,
            SizeMin: 5f, SizeMax: 11f,
            SpreadRadians: 1.2f,
            BiasVelocity: new Vector2(0f, 6f),
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 22f)),
    };
}

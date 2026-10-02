using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Flake: a chip of scale, rust or paint that has come off a surface and is tumbling as it falls.
/// A flat plate spinning in the air does two things the eye reads instantly as "debris":
///
///  - it FORESHORTENS: the plate rotates about an in-plane axis, so its width swings between full
///    and a sliver, which is what makes a speck look three-dimensional;
///  - it GLINTS: brightness follows how squarely the plate faces the key light, so each flake
///    flashes once per tumble instead of shining steadily.
///
/// Colour is chosen per flake from the seed (mostly rust, some dark scale, a little bare bright
/// metal) so a shower is a mix rather than a stamp. Fast flakes trail a short streak.
///
/// Used by StrokeChain for rust shaken loose, but it is the general "chip" material: ice shards,
/// paint, bark, and shrapnel would all be this with a different palette.
/// </summary>
public sealed class ParticleFlake : IParticleMaterial
{
    public string Name => "particle.flake";
    public string[] NaturalLanguageWords { get; } = { "rust", "rusty", "debris", "scrap" };

    private static readonly uint Rust  = FireColor.Pack(0.58f, 0.24f, 0.08f);
    private static readonly uint Scale = FireColor.Pack(0.16f, 0.15f, 0.16f);
    private static readonly uint Metal = FireColor.Pack(0.78f, 0.74f, 0.68f);

    private const int Corners = 5;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.3f) return;

        int seed = p.Seed;
        float t = ctx.Time;
        float age = p.AgeRatio;

        // Tumble: spin in the screen plane plus a flip about an in-plane axis.
        float spin = DrawHelpers.Hash01(seed + 1) * MathF.Tau + t * DrawHelpers.HashRange(seed + 2, -9f, 9f);
        float flip = DrawHelpers.Hash01(seed + 3) * MathF.Tau + t * DrawHelpers.HashRange(seed + 4, 3f, 11f);
        float face = MathF.Cos(flip);                      // 1 = facing the viewer, 0 = edge-on
        float squash = 0.12f + 0.88f * MathF.Abs(face);

        // Glint: brightest when the plate catches the key light.
        float glint = MathF.Pow(MathF.Max(0f, MathF.Cos(flip - 0.7f)), 6f);

        float pick = DrawHelpers.Hash01(seed + 5);
        uint baseCol = pick < 0.62f ? Rust : (pick < 0.88f ? Scale : Metal);
        float tone = 0.55f + 0.45f * DrawHelpers.Hash01(seed + 6);
        uint lit = DrawHelpers.LerpColor(baseCol, Metal, glint * 0.65f);

        float size = p.Size * (1f - 0.25f * age);
        float cs = MathF.Cos(spin), sn = MathF.Sin(spin);
        Vector2 pos = p.Position + new Vector2(p.Sway, 0f);

        // Irregular pentagon, squashed along the plate's tilt axis, then rotated.
        Span<Vector2> pts = stackalloc Vector2[Corners];
        for (int i = 0; i < Corners; i++)
        {
            float a = i * (MathF.Tau / Corners) + DrawHelpers.HashRange(seed + 20 + i, -0.35f, 0.35f);
            float r = size * DrawHelpers.HashRange(seed + 30 + i, 0.55f, 1.15f);
            float lx = MathF.Cos(a) * r;
            float ly = MathF.Sin(a) * r * squash;
            pts[i] = pos + new Vector2(lx * cs - ly * sn, lx * sn + ly * cs);
        }

        // Streak behind a fast flake.
        float speed = p.Velocity.Length();
        if (speed > 160f)
        {
            Vector2 dir = p.Velocity / speed;
            float len = MathF.Min(speed * 0.028f, 26f);
            dl.AddLine(pos, pos - dir * len, DrawHelpers.WithAlpha(lit, k * 0.22f), MathF.Max(1f, size * 0.7f));
        }

        float a8 = k * (0.55f + 0.45f * tone);
        dl.AddConvexPolyFilled(ref pts[0], Corners, DrawHelpers.WithAlpha(lit, a8));

        // A hot fleck right at the glint: sells "caught the light".
        if (glint > 0.55f)
            dl.AddCircleFilled(pos, MathF.Max(0.6f, size * 0.38f), DrawHelpers.WithAlpha(0xFFFFFFFFu, k * 0.60f * glint));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    // Flakes shaken loose: little drop, mostly just falling away from the strand.
    private static readonly StrokeEmission[] EmissionSpecs =
    {
        new(Role: PrimitiveRole.Flake,
            DensityPer100px: 0.50f,
            SpeedMin: 6f, SpeedMax: 30f,
            LifespanMin: 1.0f, LifespanMax: 2.0f,
            SizeMin: 1.4f, SizeMax: 3.0f,
            SpreadRadians: 1.0f,
            BiasVelocity: new Vector2(0f, 10f),
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 520f)),
    };
}

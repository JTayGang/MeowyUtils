using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>A soft, irregular radial puff with no rim. Smoke, mist (also fog), dust and miasma are the same mesh with a different <see cref="Look"/>.</summary>
public sealed class ParticlePuff : IParticleMaterial
{
    /// <summary>Colour runs Warm to Cool over CoolSpan (0 = stays Warm); radius = Size*(GrowFrom + GrowBy*smooth(age)); FadeIn/FadeOutAt envelope (FadeOutAt 1 = none); Thin fades as it grows; Highlight adds a lit blob (toward the shared key light, or toward the bottom of the screen when LitFromBelow: smoke over a fire).</summary>
    public readonly record struct Look(
        uint Warm, uint Cool, float CoolSpan,
        float GrowFrom, float GrowBy,
        float FadeIn, float FadeOutAt,
        float Alpha, float Squash, float Irregular, float MinSize,
        uint Highlight = 0, float Thin = 0f, bool LitFromBelow = false);

    public static readonly ParticlePuff Smoke = new("particle.smoke", new[] { "smoke", "smog", "soot" },
        new Look(Warm: DrawHelpers.Pack(0.30f, 0.125f, 0.055f), Cool: DrawHelpers.Pack(0.055f, 0.050f, 0.055f), CoolSpan: 0.55f,
                 GrowFrom: 0.65f, GrowBy: 1.15f, FadeIn: 0f, FadeOutAt: 1f,
                 Alpha: 0.34f, Squash: 0.88f, Irregular: 0.55f, MinSize: 1f));

    public static readonly ParticlePuff Mist = new("particle.mist", new[] { "mist", "fog", "vapor", "vapour", "breath" },
        new Look(Warm: DrawHelpers.Pack(0.74f, 0.86f, 0.98f), Cool: 0, CoolSpan: 0f,
                 GrowFrom: 0.55f, GrowBy: 0.45f, FadeIn: 0.25f, FadeOutAt: 0.45f,
                 Alpha: 0.20f, Squash: 0.80f, Irregular: 0.50f, MinSize: 2f),
        new StrokeEmission(Role: PrimitiveRole.Mist,
            DensityPer100px: 0.5f,
            SpeedMin: 6f, SpeedMax: 14f,
            LifespanMin: 2.0f, LifespanMax: 4.0f,
            SizeMin: 12f, SizeMax: 24f,
            SpreadRadians: 1.4f,
            BiasVelocity: new Vector2(0f, -6f),
            PrimaryDirection: new Vector2(0f, -1f)));

    public static readonly ParticlePuff Dust = new("particle.dust", new[] { "dust", "dusty", "dirt", "grit" },
        new Look(Warm: DrawHelpers.Pack(0.19f, 0.19f, 0.21f), Cool: 0, CoolSpan: 0f,
                 GrowFrom: 0.55f, GrowBy: 1.10f, FadeIn: 0f, FadeOutAt: 1f,
                 Alpha: 0.46f, Squash: 0.86f, Irregular: 0.55f, MinSize: 1f,
                 Highlight: DrawHelpers.Pack(0.64f, 0.57f, 0.47f), Thin: 0.60f),
        new StrokeEmission(Role: PrimitiveRole.Dust,
            DensityPer100px: 0.55f,
            SpeedMin: 4f, SpeedMax: 18f,
            LifespanMin: 1.8f, LifespanMax: 3.4f,
            SizeMin: 5f, SizeMax: 11f,
            SpreadRadians: 1.2f,
            BiasVelocity: new Vector2(0f, 6f),
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 22f)));

    public static readonly ParticlePuff Miasma = new("particle.miasma", new[] { "miasma", "murk" },
        new Look(Warm: DrawHelpers.Pack(0.42f, 0.13f, 0.56f), Cool: DrawHelpers.Pack(0.07f, 0.015f, 0.12f), CoolSpan: 0.85f,
                 GrowFrom: 0.60f, GrowBy: 0.90f, FadeIn: 0.25f, FadeOutAt: 0.55f,
                 Alpha: 0.30f, Squash: 0.82f, Irregular: 0.55f, MinSize: 2f,
                 Highlight: DrawHelpers.Pack(0.85f, 0.30f, 0.80f), Thin: 0.35f),
        new StrokeEmission(Role: PrimitiveRole.Miasma,
            DensityPer100px: 0.4f,
            SpeedMin: 5f, SpeedMax: 14f,
            LifespanMin: 2.4f, LifespanMax: 4.2f,
            SizeMin: 14f, SizeMax: 28f,
            SpreadRadians: 1.4f,
            BiasVelocity: new Vector2(0f, -8f),
            PrimaryDirection: new Vector2(0f, -1f)));

    // A charged cloud deck: dark umber that the flashes inside it light to amber. The effect drives the flashes through Brightness.
    public static readonly ParticlePuff Thunderhead = new("particle.thunderhead", new[] { "thunderhead", "thundercloud", "stormcloud", "storm" },
        new Look(Warm: DrawHelpers.Pack(0.95f, 0.62f, 0.24f), Cool: DrawHelpers.Pack(0.075f, 0.055f, 0.045f), CoolSpan: 0.60f,
                 GrowFrom: 0.60f, GrowBy: 0.50f, FadeIn: 0.30f, FadeOutAt: 0.55f,
                 Alpha: 0.36f, Squash: 0.55f, Irregular: 0.35f, MinSize: 2f,
                 Highlight: DrawHelpers.Pack(1.00f, 0.86f, 0.56f), Thin: 0.12f),
        new StrokeEmission(Role: PrimitiveRole.Haze,
            DensityPer100px: 0.35f,
            SpeedMin: 4f, SpeedMax: 12f,
            LifespanMin: 2.4f, LifespanMax: 4.4f,
            SizeMin: 16f, SizeMax: 30f,
            SpreadRadians: 1.4f,
            BiasVelocity: new Vector2(0f, -6f),
            PrimaryDirection: new Vector2(0f, -1f)));

    // Smoke over a hellfire: ember-lit oxblood while it is young, soot once it has risen out of the light, its lit side toward the flames beneath it.
    public static readonly ParticlePuff Brimstone = new("particle.brimstone", new[] { "brimstone", "sulphur", "sulfur", "hellsmoke" },
        new Look(Warm: DrawHelpers.Pack(0.46f, 0.075f, 0.030f), Cool: DrawHelpers.Pack(0.030f, 0.020f, 0.024f), CoolSpan: 0.55f,
                 GrowFrom: 0.60f, GrowBy: 1.05f, FadeIn: 0.22f, FadeOutAt: 0.58f,
                 Alpha: 0.46f, Squash: 0.84f, Irregular: 0.55f, MinSize: 2f,
                 Highlight: DrawHelpers.Pack(0.95f, 0.28f, 0.05f), Thin: 0.28f, LitFromBelow: true),
        new StrokeEmission(Role: PrimitiveRole.Smoke,
            DensityPer100px: 0.45f,
            SpeedMin: 8f, SpeedMax: 22f,
            LifespanMin: 2.4f, LifespanMax: 4.2f,
            SizeMin: 14f, SizeMax: 28f,
            SpreadRadians: 1.3f,
            BiasVelocity: new Vector2(0f, -12f),
            PrimaryDirection: new Vector2(0f, -1f)));

    public static readonly ParticlePuff[] All = { Smoke, Mist, Dust, Miasma, Thunderhead, Brimstone };

    private const int Segs = 12;

    private readonly Look _look;
    private readonly StrokeEmission[] _emissions;

    private ParticlePuff(string name, string[] words, in Look look, params StrokeEmission[] emissions)
    {
        Name = name; NaturalLanguageWords = words; _look = look; _emissions = emissions;
    }

    public string Name { get; }
    public string[] NaturalLanguageWords { get; }
    public ReadOnlySpan<StrokeEmission> Emissions => _emissions;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        ref readonly Look L = ref _look;
        if (k <= 0.003f || p.Size <= L.MinSize) return;

        float age = p.AgeRatio;
        float radius = p.Size * (L.GrowFrom + L.GrowBy * DrawHelpers.Smooth(age));
        float fade = (L.FadeIn > 0f ? MathF.Min(age / L.FadeIn, 1f) : 1f)
                   * (L.FadeOutAt < 1f ? 1f - DrawHelpers.Smooth((age - L.FadeOutAt) / (1f - L.FadeOutAt)) : 1f);
        float a = k * fade * L.Alpha * (1f - L.Thin * DrawHelpers.Smooth(age));
        if (a <= 0.002f || radius <= L.MinSize) return;

        uint body = L.CoolSpan > 0f ? DrawHelpers.LerpColor(L.Warm, L.Cool, DrawHelpers.Smooth(age / L.CoolSpan)) : L.Warm;
        Vector2 pos = p.DrawPos;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        float rot = DrawHelpers.Hash01(p.Seed + 9) * MathF.Tau;

        Blob(dl, pos, uv, body, a, 1f, radius, rot, L.Squash, p.Seed, L.Irregular);

        if (L.Highlight != 0)
        {
            Vector2 toLight = L.LitFromBelow ? new Vector2(0f, 0.85f) : new Vector2(StudioLighting.Key.X, StudioLighting.Key.Y);
            Blob(dl, pos + toLight * (radius * 0.30f), uv, L.Highlight, a, 0.85f, radius * 0.70f, rot + 1.3f, L.Squash, p.Seed + 77, L.Irregular - 0.1f);
        }
    }

    // Centre at alpha * centre, mid ring at 0.65 of that, outer ring transparent.
    private static void Blob(ImDrawListPtr dl, Vector2 pos, Vector2 uv, uint color, float a, float centre,
                             float radius, float rot, float squash, int seed, float irregular)
    {
        Span<float> rr = stackalloc float[2] { radius * 0.5f, radius };
        Span<uint> cc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(color, a * centre * 0.65f),
            DrawHelpers.WithAlpha(color, 0f),
        };
        MeshDraw.Radial(dl, pos, uv, DrawHelpers.WithAlpha(color, a * centre), Segs, rr, cc, rot, squash, seed, irregular);
    }
}

using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>A soft puff with no rim: smoke, mist, dust and miasma are the same mesh with different Looks; a Look with a Ramp is drawn as a lit, noise-shaped billow instead.</summary>
public sealed class ParticlePuff : IParticleMaterial
{
    /// <summary>Colour runs Warm to Cool over CoolSpan (0 = stays Warm); radius = Size*(GrowFrom + GrowBy*smooth(age)). A Ramp (shadow to highlight) makes it a billow, lit from the middle of the screen; its Variant, if set, is the squash in percent and its Rotation the way it stretches.</summary>
    internal readonly record struct Look(
        uint Warm, uint Cool, float CoolSpan,
        float GrowFrom, float GrowBy,
        float FadeIn, float FadeOutAt,
        float Alpha, float Squash, float Irregular, float MinSize,
        uint Highlight = 0, float Thin = 0f, bool LitFromBelow = false, Palette? Ramp = null);

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

    // A charged cloud deck: dark umber lit to amber by flashes the effect drives via Brightness.
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

    // Smoke over a hellfire: ember-lit oxblood while it is young, soot once it has risen out of the light
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

    // The dark that comes with a haunting: ink with a cold sheen where something pale catches it
    public static readonly ParticlePuff Gloom = new("particle.gloom", new[] { "gloom", "gloaming", "umbra" },
        new Look(Warm: DrawHelpers.Pack(0.16f, 0.24f, 0.26f), Cool: DrawHelpers.Pack(0.012f, 0.018f, 0.024f), CoolSpan: 0.80f,
                 GrowFrom: 0.60f, GrowBy: 0.60f, FadeIn: 0.25f, FadeOutAt: 0.55f,
                 Alpha: 0.42f, Squash: 0.80f, Irregular: 0.55f, MinSize: 2f,
                 Highlight: DrawHelpers.Pack(0.55f, 0.72f, 0.70f), Thin: 0.30f),
        new StrokeEmission(Role: PrimitiveRole.Miasma,
            DensityPer100px: 0.30f,
            SpeedMin: 4f, SpeedMax: 12f,
            LifespanMin: 2.6f, LifespanMax: 4.4f,
            SizeMin: 16f, SizeMax: 30f,
            SpreadRadians: 1.4f,
            BiasVelocity: new Vector2(0f, -6f),
            PrimaryDirection: new Vector2(0f, -1f)));

    // Perfumed haze: a billow lit from within, plum in its folds and peach at its rims
    public static readonly ParticlePuff Rosefog = new("particle.rosefog", new[] { "rosefog", "haze", "perfume", "incense", "blush" },
        new Look(Warm: DrawHelpers.Pack(0.72f, 0.26f, 0.47f), Cool: 0, CoolSpan: 0f,
                 GrowFrom: 0.80f, GrowBy: 0.28f, FadeIn: 0.20f, FadeOutAt: 0.72f,
                 Alpha: 0.50f, Squash: 0.74f, Irregular: 0.30f, MinSize: 6f, Ramp: CharmColor.Fog),
        new StrokeEmission(Role: PrimitiveRole.Mist,
            DensityPer100px: 0.30f,
            SpeedMin: 5f, SpeedMax: 12f,
            LifespanMin: 3.0f, LifespanMax: 5.0f,
            SizeMin: 40f, SizeMax: 80f,
            SpreadRadians: 1.4f,
            BiasVelocity: new Vector2(0f, -6f),
            PrimaryDirection: new Vector2(0f, -1f)));

    public static readonly ParticlePuff[] All = { Smoke, Mist, Dust, Miasma, Thunderhead, Brimstone, Gloom, Rosefog };

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

        if (L.Ramp is { } ramp)
        {
            Billow(dl, in p, in ctx, ramp, radius, a, p.Variant > 0 ? p.Variant * 0.01f : L.Squash, L.Irregular, ctx.Time);
            return;
        }

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

    // The billow: a polar mesh whose every vertex is shaded from a warped noise field, lit from the screen's middle.
    private const int BillowRings = 7, BillowSegs = 28;
    private static readonly MeshBuilder Mb = new();
    private static readonly (float C, float S)[] Dir = BuildDirs();

    private static (float, float)[] BuildDirs()
    {
        var d = new (float, float)[BillowSegs];
        for (int j = 0; j < BillowSegs; j++) d[j] = (MathF.Cos(MathF.Tau * j / BillowSegs), MathF.Sin(MathF.Tau * j / BillowSegs));
        return d;
    }

    private static void Billow(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx, Palette ramp,
                               float radius, float a, float squash, float irregular, float time)
    {
        Vector2 c = p.DrawPos;
        float cosR = MathF.Cos(p.Rotation), sinR = MathF.Sin(p.Rotation);

        // The turn of the pattern: it slowly swirls about the billow's heart, one way or the other.
        float swirl = DrawHelpers.Hash01(p.Seed + 12) * MathF.Tau + time * DrawHelpers.HashRange(p.Seed + 13, -0.07f, 0.07f);
        float cosP = MathF.Cos(swirl), sinP = MathF.Sin(swirl);
        float ox = DrawHelpers.HashRange(p.Seed + 14, 0f, 200f) + time * 0.035f, oy = DrawHelpers.HashRange(p.Seed + 15, 0f, 200f);
        float freq = DrawHelpers.HashRange(p.Seed + 16, 0.95f, 1.45f);

        // Light arrives from the middle of the screen: its direction in the billow's own frame, then in the noise's.
        Vector2 toLight = new(ctx.ScreenW * 0.5f - c.X, ctx.ScreenH * 0.5f - c.Y);
        float tl = toLight.Length();
        toLight = tl > 1f ? toLight / tl : new Vector2(0f, -1f);

        // A billow right at the light is lit all round, not from one side.
        float side = DrawHelpers.Saturate(tl / (radius * 1.2f));
        float lux = (toLight.X * cosR + toLight.Y * sinR), luy = (-toLight.X * sinR + toLight.Y * cosR) / squash;
        float ll = MathF.Sqrt(lux * lux + luy * luy);
        lux /= ll; luy /= ll;
        float lnx = (cosP * lux - sinP * luy) * 0.70f * side, lny = (sinP * lux + cosP * luy) * 0.70f * side;

        Mb.Clear();
        Mb.Reserve(1 + BillowRings * BillowSegs, BillowSegs * 3 + (BillowRings - 1) * BillowSegs * 6);
        for (int r = -1; r < BillowRings; r++)
        {
            float rr = r < 0 ? 0f : (r + 1f) / BillowRings;
            for (int j = 0; j < (r < 0 ? 1 : BillowSegs); j++)
            {
                float ux = r < 0 ? 0f : Dir[j].C * rr, uy = r < 0 ? 0f : Dir[j].S * rr;
                float nx = (cosP * ux - sinP * uy) * freq + ox, ny = (sinP * ux + cosP * uy) * freq + oy;

                // Domain warp, so the folds curl rather than blob.
                float qx = nx + 0.6f * Noise.Value(nx * 0.55f + 3.1f, ny * 0.55f), qy = ny + 0.6f * Noise.Value(nx * 0.55f, ny * 0.55f + 7.7f);
                float d0 = 0.70f + 0.80f * Noise.Fbm(qx, qy, 2);
                float d1 = 0.70f + 0.80f * Noise.Fbm(qx + lnx, qy + lny, 2);

                float edge = 1f - DrawHelpers.Smooth(0.28f, 1f, rr);
                float dens = DrawHelpers.Saturate(d0);
                float core = DrawHelpers.Smooth(0.75f, 0.05f, rr);
                float lit = DrawHelpers.Saturate(0.46f + 0.95f * (d0 - d1) + 0.20f * side * (ux * lux + uy * luy) - 0.26f * core * dens);

                float wob = 1f + irregular * 0.5f * (d0 - 0.5f);
                float sx = ux * wob, sy = uy * wob * squash;
                Vector2 pos = c + new Vector2(sx * cosR - sy * sinR, sx * sinR + sy * cosR) * radius;
                // Thin fog at a rim stays rosy instead of washing out to white.
                float thick = edge * MathF.Pow(dens, 1.3f);
                Mb.VertexReserved(pos, DrawHelpers.WithAlpha(ramp.Sample(lit * (0.72f + 0.28f * DrawHelpers.Saturate(thick * 1.8f))), a * thick));
            }
        }

        for (int j = 0; j < BillowSegs; j++) Mb.TriReserved(0, 1 + j, 1 + (j + 1) % BillowSegs);
        for (int r = 0; r < BillowRings - 1; r++)
        {
            int a0 = 1 + r * BillowSegs, a1 = a0 + BillowSegs;
            for (int j = 0; j < BillowSegs; j++)
            {
                int jn = (j + 1) % BillowSegs;
                Mb.QuadReserved(a0 + j, a0 + jn, a1 + j, a1 + jn);
            }
        }
        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
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

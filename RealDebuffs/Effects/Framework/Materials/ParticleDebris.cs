using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>Spin, flip and glint of a tumbling bit: the plate foreshortens as it flips and flashes once per turn.</summary>
internal readonly struct Tumble
{
    public readonly float Spin, Squash, Glint;

    public Tumble(int seed, float time, float spinRate, float flipLo, float flipHi, float squashMin, float glintShift, float glintPower)
    {
        Spin = DrawHelpers.Hash01(seed + 1) * MathF.Tau + time * DrawHelpers.HashRange(seed + 2, -spinRate, spinRate);
        float flip = DrawHelpers.Hash01(seed + 3) * MathF.Tau + time * DrawHelpers.HashRange(seed + 4, flipLo, flipHi);
        Squash = squashMin + (1f - squashMin) * MathF.Abs(MathF.Cos(flip));
        Glint = MathF.Pow(MathF.Max(0f, MathF.Cos(flip - glintShift)), glintPower);
    }
}

/// <summary>A chip of rust, scale or paint tumbling as it falls: foreshortened pentagon that glints, with a streak when fast.</summary>
public sealed class ParticleFlake : IParticleMaterial
{
    public string Name => "particle.flake";
    public string[] NaturalLanguageWords { get; } = { "rust", "rusty", "debris", "scrap" };

    private static readonly uint Rust  = DrawHelpers.Pack(0.58f, 0.24f, 0.08f);
    private static readonly uint Scale = DrawHelpers.Pack(0.16f, 0.15f, 0.16f);
    private static readonly uint Metal = DrawHelpers.Pack(0.78f, 0.74f, 0.68f);

    private const int Corners = 5;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.3f) return;

        int seed = p.Seed;
        var tumble = new Tumble(seed, ctx.Time, spinRate: 9f, flipLo: 3f, flipHi: 11f, squashMin: 0.12f, glintShift: 0.7f, glintPower: 6f);

        float pick = DrawHelpers.Hash01(seed + 5);
        uint baseCol = pick < 0.62f ? Rust : (pick < 0.88f ? Scale : Metal);
        float tone = 0.55f + 0.45f * DrawHelpers.Hash01(seed + 6);
        uint lit = DrawHelpers.LerpColor(baseCol, Metal, tumble.Glint * 0.65f);

        float size = p.Size * (1f - 0.25f * p.AgeRatio);
        float cs = MathF.Cos(tumble.Spin), sn = MathF.Sin(tumble.Spin);
        Vector2 pos = p.DrawPos;

        // Irregular pentagon, squashed along the tilt axis, then rotated.
        Span<Vector2> pts = stackalloc Vector2[Corners];
        for (int i = 0; i < Corners; i++)
        {
            float a = i * (MathF.Tau / Corners) + DrawHelpers.HashRange(seed + 20 + i, -0.35f, 0.35f);
            float r = size * DrawHelpers.HashRange(seed + 30 + i, 0.55f, 1.15f);
            float lx = MathF.Cos(a) * r;
            float ly = MathF.Sin(a) * r * tumble.Squash;
            pts[i] = pos + new Vector2(lx * cs - ly * sn, lx * sn + ly * cs);
        }

        float speed = p.Velocity.Length();
        if (speed > 160f)
        {
            float len = MathF.Min(speed * 0.028f, 26f);
            dl.AddLine(pos, pos - p.Velocity / speed * len, DrawHelpers.WithAlpha(lit, k * 0.22f), MathF.Max(1f, size * 0.7f));
        }

        dl.AddConvexPolyFilled(ref pts[0], Corners, DrawHelpers.WithAlpha(lit, k * (0.55f + 0.45f * tone)));

        if (tumble.Glint > 0.55f)
            dl.AddCircleFilled(pos, MathF.Max(0.6f, size * 0.38f), DrawHelpers.WithAlpha(0xFFFFFFFFu, k * 0.60f * tumble.Glint));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    private static readonly StrokeEmission[] Shed =
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

/// <summary>A loose filament of hemp, wool or hair drifting down: a hair-thin three-segment curl that glints when it turns to the light.</summary>
public sealed class ParticleFibre : IParticleMaterial
{
    public string Name => "particle.fibre";
    public string[] NaturalLanguageWords { get; } = { "fibre", "fibres", "fiber", "fibers", "lint", "fluff" };

    private static readonly uint Lit   = DrawHelpers.Pack(0.86f, 0.74f, 0.54f);
    private static readonly uint Dull  = DrawHelpers.Pack(0.52f, 0.42f, 0.28f);
    private static readonly uint Glint = DrawHelpers.Pack(1.00f, 0.94f, 0.80f);

    private const int Segments = 3;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.5f) return;

        int seed = p.Seed;
        var tumble = new Tumble(seed, ctx.Time, spinRate: 2.2f, flipLo: 1.5f, flipHi: 5f, squashMin: 0.30f, glintShift: 0.9f, glintPower: 8f);

        uint col = DrawHelpers.LerpColor(DrawHelpers.Hash01(seed + 5) < 0.6f ? Lit : Dull, Glint, tumble.Glint * 0.7f);

        // A chromatic colour override keeps each colour's own saturation, so match the rope's dye instead of staying muted hemp.
        float dye = DrawHelpers.ColorOverrideChroma;
        if (dye > 0f) col = Dyed(col, dye);

        float step = p.Size * (1f - 0.20f * p.AgeRatio) / Segments;
        float bend = DrawHelpers.HashRange(seed + 6, -1.1f, 1.1f);
        float width = MathF.Max(0.9f, ctx.ScreenScale * 1.05f);

        float heading = tumble.Spin;
        Vector2 prev = p.DrawPos;
        for (int i = 0; i < Segments; i++)
        {
            heading += bend / Segments;
            Vector2 next = prev + new Vector2(MathF.Cos(heading), MathF.Sin(heading) * tumble.Squash) * step;
            dl.AddLine(prev, next, DrawHelpers.WithAlpha(col, k * 0.80f * (1f - 0.28f * i)), width);
            prev = next;
        }
    }

    private static uint Dyed(uint packed, float saturation)
    {
        var c = new Vector3((packed & 255) / 255f, ((packed >> 8) & 255) / 255f, ((packed >> 16) & 255) / 255f);
        c = DrawHelpers.WithSaturation(c, saturation);
        return DrawHelpers.Pack(c.X, c.Y, c.Z);
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Fibre,
            DensityPer100px: 0.55f,
            SpeedMin: 3f, SpeedMax: 16f,
            LifespanMin: 1.4f, LifespanMax: 2.8f,
            SizeMin: 7f, SizeMax: 15f,
            SpreadRadians: 1.4f,
            BiasVelocity: new Vector2(0f, 5f),
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 14f)),
    };
}

/// <summary>A light, thin falling droplet: a bead stretched along its velocity. A thick one that hangs and lets go is ParticleSlime/GoopEmitter.</summary>
public sealed class ParticleDrip : IParticleMaterial
{
    public string Name => "particle.drip";
    public string[] NaturalLanguageWords { get; } = { "drip", "drips", "droplet", "droplets" };

    private static readonly uint Body      = DrawHelpers.Pack(0.55f, 0.75f, 0.20f);
    private static readonly uint Highlight = DrawHelpers.Pack(0.92f, 1.00f, 0.55f);
    private static readonly uint Shadow    = DrawHelpers.Pack(0.10f, 0.15f, 0.03f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.3f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        float size = p.Size * DrawHelpers.LifeSize(p.AgeRatio, riseEnd: 0.10f, riseFrom: 0.55f, fallStart: 0.10f, floor: 0.20f);
        if (size <= 0.3f) return;

        float speed = p.Velocity.Length();
        Vector2 dir = speed > 1f ? p.Velocity / speed : new Vector2(0f, 1f);

        float stretch = MathF.Min(size * 3.5f, size * (1.2f + speed * 0.02f));
        float halfLen = MathF.Max(0f, stretch * 0.5f - size);
        Vector2 head = p.Position + dir * halfLen;
        Vector2 tail = p.Position - dir * halfLen;

        dl.AddLine(tail, head, DrawHelpers.WithAlpha(Shadow, alpha * 0.55f), size * 2.2f);
        dl.AddLine(tail, head, DrawHelpers.WithAlpha(Body, alpha * 0.90f), size * 1.6f);
        dl.AddCircleFilled(head, size,         DrawHelpers.WithAlpha(Body, alpha * 0.95f));
        dl.AddCircleFilled(tail, size * 0.75f, DrawHelpers.WithAlpha(Body, alpha * 0.75f));
        dl.AddCircleFilled(head, size * 0.45f, DrawHelpers.WithAlpha(Highlight, alpha * 0.85f));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    // Half the drips fall under gravity, half run along the strand.
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Drip,
            DensityPer100px: 0.4f,
            SpeedMin: 8f, SpeedMax: 22f,
            LifespanMin: 1.6f, LifespanMax: 2.6f,
            SizeMin: 2.0f, SizeMax: 4.5f,
            SpreadRadians: 0.18f,
            BiasVelocity: Vector2.Zero,
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 420f),
            Flow: new StrokeFlowOptions(
                Share: 0.5f,
                SpeedMin: 55f, SpeedMax: 110f,
                WobbleAmplitude: 1.8f,
                WobbleFrequencyHz: 0.7f,
                ObstacleSpacingPx: 45f,
                LateralOffsetFrac: 0.45f)),
    };
}

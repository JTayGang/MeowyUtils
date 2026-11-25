namespace RealDebuffs.Effects.Framework.Materials;

/// Spin, flip and glint of a tumbling bit: the plate foreshortens as it flips and flashes once per turn.
internal readonly struct Tumble
{
    public readonly float Spin, Squash, Glint;

    public Tumble(int seed, float time, float spinRate, float flipLo, float flipHi, float squashMin, float glintShift, float glintPower)
    {
        Spin = Hash01(seed + 1) * Tau + time * HashRange(seed + 2, -spinRate, spinRate);
        float flip = Hash01(seed + 3) * Tau + time * HashRange(seed + 4, flipLo, flipHi);
        Squash = Lerp(squashMin, 1f, Abs(Cos(flip)));
        Glint = Pow(Max(0f, Cos(flip - glintShift)), glintPower);
    }
}

/// A tumbling chip of rust, scale or paint: a foreshortened glinting pentagon, streaked when fast.
public sealed class ParticleFlake : IParticleMaterial
{
    public string Name => "particle.flake";
    public string[] NaturalLanguageWords { get; } = { "rust", "rusty", "debris", "scrap" };

    static readonly uint Rust  = Pack(0.58f, 0.24f, 0.08f);
    static readonly uint Scale = Pack(0.16f, 0.15f, 0.16f);
    static readonly uint Metal = Pack(0.78f, 0.74f, 0.68f);

    const int Corners = 5;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.3f) return;

        int seed = p.Seed;
        var tumble = new Tumble(seed, ctx.Time, spinRate: 9f, flipLo: 3f, flipHi: 11f, squashMin: 0.12f, glintShift: 0.7f, glintPower: 6f);

        float pick = Hash01(seed + 5);
        uint baseCol = pick < 0.62f ? Rust : (pick < 0.88f ? Scale : Metal);
        float tone = 0.55f + 0.45f * Hash01(seed + 6);
        uint lit = LerpColor(baseCol, Metal, tumble.Glint * 0.65f);

        float size = p.Size * (1f - 0.25f * p.AgeRatio);
        float cs = Cos(tumble.Spin), sn = Sin(tumble.Spin);
        Vector2 pos = p.DrawPos;

        // Irregular pentagon, squashed along the tilt axis, then rotated.
        Span<Vector2> pts = stackalloc Vector2[Corners];
        for (int i = 0; i < Corners; i++)
        {
            float a = i * (Tau / Corners) + HashRange(seed + 20 + i, -0.35f, 0.35f);
            float r = size * HashRange(seed + 30 + i, 0.55f, 1.15f);
            float lx = Cos(a) * r;
            float ly = Sin(a) * r * tumble.Squash;
            pts[i] = pos + new Vector2(lx * cs - ly * sn, lx * sn + ly * cs);
        }

        float speed = p.Velocity.Length();
        if (speed > 160f)
        {
            float len = Min(speed * 0.028f, 26f);
            dl.AddLine(pos, pos - p.Velocity / speed * len, WithAlpha(lit, k * 0.22f), Max(1f, size * 0.7f));
        }

        dl.AddConvexPolyFilled(ref pts[0], Corners, WithAlpha(lit, k * (0.55f + 0.45f * tone)));

        if (tumble.Glint > 0.55f)
            dl.AddCircleFilled(pos, Max(0.6f, size * 0.38f), WithAlpha(0xFFFFFFFFu, k * 0.60f * tumble.Glint));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    static readonly StrokeEmission[] Shed =
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

/// A loose filament of hemp, wool or hair drifting down: a hair-thin three-segment curl that glints in the light.
public sealed class ParticleFibre : IParticleMaterial
{
    public string Name => "particle.fibre";
    public string[] NaturalLanguageWords { get; } = { "fibre", "fibres", "fiber", "fibers", "lint", "fluff" };

    static readonly uint Lit   = Pack(0.86f, 0.74f, 0.54f);
    static readonly uint Dull  = Pack(0.52f, 0.42f, 0.28f);
    static readonly uint Glint = Pack(1.00f, 0.94f, 0.80f);

    const int Segments = 3;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.5f) return;

        int seed = p.Seed;
        var tumble = new Tumble(seed, ctx.Time, spinRate: 2.2f, flipLo: 1.5f, flipHi: 5f, squashMin: 0.30f, glintShift: 0.9f, glintPower: 8f);

        uint col = LerpColor(Hash01(seed + 5) < 0.6f ? Lit : Dull, Glint, tumble.Glint * 0.7f);

        // A chromatic override keeps each colour's saturation, so it matches the rope's dye instead of muted hemp.
        float dye = ColorOverrideChroma;
        if (dye > 0f) col = Dyed(col, dye);

        float step = p.Size * (1f - 0.20f * p.AgeRatio) / Segments;
        float bend = HashRange(seed + 6, -1.1f, 1.1f);
        float width = Max(0.9f, ctx.ScreenScale * 1.05f);

        float heading = tumble.Spin;
        Vector2 prev = p.DrawPos;
        for (int i = 0; i < Segments; i++)
        {
            heading += bend / Segments;
            Vector2 next = prev + new Vector2(Cos(heading), Sin(heading) * tumble.Squash) * step;
            dl.AddLine(prev, next, WithAlpha(col, k * 0.80f * (1f - 0.28f * i)), width);
            prev = next;
        }
    }

    static uint Dyed(uint packed, float saturation)
    {
        var c = new Vector3((packed & 255) / 255f, ((packed >> 8) & 255) / 255f, ((packed >> 16) & 255) / 255f);
        c = WithSaturation(c, saturation);
        return Pack(c);
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    static readonly StrokeEmission[] Shed =
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

/// Base for liquid particles (water, blood, slime): a GoopDraw drop shaded by its LiquidSpec, dripping as its viscosity dictates.
public abstract class ParticleGoop : IParticleMaterial, ICoatMatter, IBatchedParticleMaterial
{
    static readonly StrokeEmission[] Coat = { StrokeEmission.Coated(PrimitiveRole.Goop) };
    LiquidMatter? _matter;

    protected ParticleGoop(in LiquidSpec look)
    {
        Liquid = new(in look);
    }

    public abstract string Name { get; }
    public virtual string[] NaturalLanguageWords => [];

    public Liquid Liquid { get; }

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx) =>
        GoopDraw.Draw(dl, in p, Liquid, in ctx);

    public ReadOnlySpan<StrokeEmission> Emissions => Coat;

    public CoatMatter Matter => _matter ??= new LiquidMatter(Name, Liquid);

    public bool DrawsWithHost => true;
}

/// Thick, translucent, sickly-green mucus: hangs in long strings, lets go, falls as a glossy teardrop.
public sealed class ParticleSlime : ParticleGoop
{
    public ParticleSlime() : base(LiquidPresets.Slime) { }

    public override string Name => "particle.slime";
    public override string[] NaturalLanguageWords { get; } =
        { "slime", "slimy", "ooze", "oozing", "goo", "goop", "mucus", "gunk" };
}

/// Blood: runnier and darker than slime, so it hangs in shorter necks.
public sealed class ParticleBlood : ParticleGoop
{
    public ParticleBlood() : base(LiquidPresets.Blood) { }

    public override string Name => "particle.blood";
    public override string[] NaturalLanguageWords { get; } =
        { "blood", "bloody", "bloodied", "gore", "gory" };
}

/// Water: clear, quick to let go, falling as a bright bead.
public sealed class ParticleWater : ParticleGoop
{
    public ParticleWater() : base(LiquidPresets.Water) { }

    public override string Name => "particle.water";
    public override string[] NaturalLanguageWords { get; } =
        { "water", "watery", "aqua", "brine", "seawater" };
}

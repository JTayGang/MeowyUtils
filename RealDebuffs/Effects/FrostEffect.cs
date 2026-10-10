using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Frost: the screen freezing over. Condensation fogs the glass, then ice nucleates at a few points on
/// the border and creeps inward as a fractal front, thick and milky behind it, with fern crystals
/// growing along it. Everything that sits on the ice (crystals, glints, mist) is placed from
/// the same growth field, so it appears exactly where and when the front reaches it.
///
/// A wind blows the whole time: snow streaks across the screen in gusts and drifts in lulls, and the mist
/// is carried along with it, so the freeze reads as cold and windy as well as frosted.
///
/// Layers, bottom to top: frost cover (haze + ferns, one region material), intro flash, crystals,
/// mist, glints, blowing snow.
/// </summary>
public sealed class FrostEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Frost;
    public string DisplayName => "Frostbite / Deep Freeze";
    public string Description => "Icy blue creeps in from the edges.";
    public int DrawOrder => 3;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Frostbite"] = 0.55f,
        ["Deep Freeze"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "frost", "frozen", "freezing", "chill", "icy", "ice"
    };

    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Particle", PrimitiveRole.Snowflake),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Particle", PrimitiveRole.Snowflake, "Frost flowers", "particle.snowflake"),
        new("Particle", PrimitiveRole.Glint,     "Glints",        "particle.glint"),
        new("Particle", PrimitiveRole.Mist,      "Mist",          "particle.mist"),
        new("Particle", PrimitiveRole.Snow,      "Blowing snow",  "particle.snow"),
        new("Region",   PrimitiveRole.MainStroke, "Frost cover",  "region.frost-cover", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke, "Intro flash",  "region.flat-fill", "FlatFill"),
    };

    // ---- palette ----
    private static readonly uint Flash = DrawHelpers.ToU32(0.70f, 0.86f, 1.00f, 1f);

    // ---- state ----
    private readonly CastTracker _cast = new();
    private readonly FrostField _field = new();
    private Vector2 _builtFor;

    // The mist is the only emitter-driven particle. Spawn callbacks are built once (they read
    // _field/_screen/_px/_windDir), so nothing is allocated per frame.
    private readonly ParticleEmitter _mist = new(maxParticles: 18, seedSalt: 0xF05701);
    private readonly Func<int, Vector2> _mistPos, _mistVel;

    // The wind: a fixed direction per cast (it can blow either way), a strength that gusts, and the snow it carries.
    private readonly WindField _snow = new();
    private Vector2 _windDir = new(1f, 0.18f);
    private float _gustSeed;
    private const float WindSpeed = 560f;      // px/s at 1080p in a full gust (near flakes go ~1.4x, far ~0.4x)

    private Vector2 _screen;
    private float _px = 1f;

    public FrostEffect()
    {
        _mistPos = seed =>
        {
            var spots = _field.MistSpots;
            if (spots.Length == 0) return _screen * 0.5f;
            var jitter = new Vector2(DrawHelpers.HashRange(seed + 7, -40f, 40f), DrawHelpers.HashRange(seed + 8, -40f, 40f));
            return spots[(int)((uint)seed % (uint)spots.Length)] + jitter * _px;
        };
        // Rises a little and is carried downwind.
        _mistVel = seed => (new Vector2(DrawHelpers.HashRange(seed + 3, -7f, 7f), DrawHelpers.HashRange(seed + 4, -16f, -4f))
                            + _windDir * DrawHelpers.HashRange(seed + 9, 22f, 52f)) * _px;
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        // Fresh application (or a resized screen): lay out a new freeze.
        if (_cast.Begin(time) || screenSize != _builtFor)
        {
            _builtFor = screenSize;
            _field.Build(unchecked((int)(_cast.Start * 1000f)), screenSize);
            _mist.Clear();

            int seed = unchecked((int)(_cast.Start * 1000f));
            _windDir = Vector2.Normalize(new Vector2(DrawHelpers.Hash01(seed + 21) < 0.5f ? -1f : 1f, 0.18f));
            _gustSeed = DrawHelpers.Hash01(seed + 22) * MathF.Tau;
            int flakes = Math.Clamp((int)(380f * MathF.Sqrt(screenSize.X * screenSize.Y / (1920f * 1080f))), 200, 560);
            _snow.Build(flakes, seed + 23, screenSize);
        }

        _screen = screenSize;
        _px = MathF.Min(screenSize.X, screenSize.Y) / 1080f;

        float age = time - _cast.Start;
        float thaw = MathF.Min(1f, alpha);                       // the front retreats as the effect fades out
        float prog = FrostField.ProgressAt(age) * MathF.Pow(thaw, 0.6f);

        _field.Age = age;
        _field.Progress = prog;
        _field.Fog = prog * 1.35f + 0.08f;
        _field.Alpha = alpha;

        // ---- the cover and the intro flash ----
        scene.AddRegion(new RegionPrimitive
        {
            Min = Vector2.Zero, Max = screenSize,
            Bottom = true,                                       // marks it an edge region -> the "Frost cover" slot
            Alpha = 1f,
            ColorOverride = colorOverride,
            State = _field,
        });

        float introFlash = age < 0.30f ? MathF.Exp(-age / 0.07f) : 0f;
        if (introFlash > 0.01f)
        {
            scene.AddRegion(new RegionPrimitive
            {
                Min = Vector2.Zero, Max = screenSize,
                Tint = Flash,
                Alpha = introFlash * 0.34f * alpha,
                ColorOverride = colorOverride,
            });
        }

        // ---- crystals on the glass (the hero slot) ----
        foreach (var f in _field.Flowers)
        {
            float growth = Math.Clamp((age - f.Start) / 0.9f, 0f, 1f);
            if (growth <= 0f) continue;
            scene.AddParticle(new ParticlePrimitive
            {
                Position = f.Pos, Size = f.Size,
                AgeRatio = growth < 1f ? 0.20f * growth : 0.45f,   // grows in, then holds (no tumble)
                Brightness = alpha * (0.55f + 0.40f * growth),
                Seed = f.Seed, Role = PrimitiveRole.Snowflake, ColorOverride = colorOverride,
            });
        }

        // ---- mist, hanging just inside the front ----
        if (age > 0.4f && _field.MistSpots.Length > 0)
        {
            _mist.Update(time, dt, spawnIntervalMin: 0.30f, spawnIntervalMax: 0.75f,
                         spawnPos: _mistPos, spawnVelocity: _mistVel,
                         lifespanMin: 3.5f, lifespanMax: 5.5f, sizeMin: 70f * _px, sizeMax: 140f * _px);
        }
        _mist.Emit(scene, time, PrimitiveRole.Mist, brightnessMul: alpha * Math.Clamp(age * 0.6f, 0f, 1f), colorOverride);

        // ---- glints: nucleation flashes first, then the steady twinkle ----
        foreach (var site in _field.Growth.Sites)
        {
            float t = age - FrostField.AgeFor(site.Delay * 0.9f);
            if (t < 0f || t > 0.8f) continue;
            scene.AddParticle(new ParticlePrimitive
            {
                Position = site.Pos + site.Inward * (14f * _px), Size = 78f * _px,
                Brightness = alpha * MathF.Exp(-t / 0.20f),
                Seed = (int)(site.Pos.X * 7f + site.Pos.Y), Role = PrimitiveRole.Glint, ColorOverride = colorOverride,
            });
        }
        foreach (var g in _field.Glints)
        {
            float since = age - g.Start;
            if (since < 0f) continue;
            float twinkle = MathF.Pow(MathF.Max(0f, MathF.Sin(age * g.Rate * 6.2832f + g.Phase)), g.Sharp);
            float born = since < 0.3f ? 0.9f * (1f - since / 0.3f) : 0f;      // a flash as the ice forms under it
            float inten = MathF.Max(twinkle, born);
            if (inten < 0.06f) continue;
            scene.AddParticle(new ParticlePrimitive
            {
                Position = g.Pos, Size = g.Size, Brightness = alpha * inten,
                Seed = g.Seed, Role = PrimitiveRole.Glint, ColorOverride = colorOverride,
            });
        }

        // ---- the wind, and the snow it carries ----
        // The storm builds as the glass freezes, and drops away as it thaws. A gust drives the flakes into
        // streaks and brings more of them out; a lull leaves them drifting.
        float gust = WindField.Gust(age, _gustSeed);
        float build = Math.Clamp((age - 0.4f) / 3.5f, 0f, 1f);
        float density = build * MathF.Pow(thaw, 0.6f) * (0.70f + 0.30f * gust);
        _snow.Update(time, dt, screenSize, _px, _windDir * (WindSpeed * _px), gust);
        // Snow is white by nature, so it takes the override's hue but stays pale (see IceColor.Pastel).
        _snow.Emit(scene, screenSize, _px, PrimitiveRole.Snow, density, brightness: alpha * (0.80f + 0.20f * gust), IceColor.Pastel(colorOverride));
    }
}

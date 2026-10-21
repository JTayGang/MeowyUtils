using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Frost: condensation, then ice creeping in as a fractal front; ferns, glints and mist are placed from one growth field; wind-blown snow.</summary>
public sealed class FrostEffect : ISceneEffect, IHasSwappableSlots
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

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Particle", PrimitiveRole.Snowflake, "Frost flowers", "particle.snowflake", Hero: true),
        new("Particle", PrimitiveRole.Glint,     "Glints",        "particle.glint"),
        new("Particle", PrimitiveRole.Mist,      "Mist",          "particle.mist"),
        new("Particle", PrimitiveRole.Snow,      "Blowing snow",  "particle.snow"),
        new("Region",   PrimitiveRole.MainStroke, "Frost cover",  "region.frost-cover", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke, "Intro flash",  "region.flat-fill", "FlatFill"),
    };

    // palette
    private static readonly uint Flash = DrawHelpers.Pack(0.70f, 0.86f, 1.00f);

    // state
    private readonly CastTracker _cast = new();
    private readonly FrostField _field = new();

    // Mist is the only emitter-driven particle; spawn callbacks are built once, so nothing allocates per frame.
    private readonly ParticleEmitter _mist = new(maxParticles: 18, seedSalt: 0xF05701);
    private readonly Func<int, Vector2> _mistPos, _mistVel;

    // The wind: a fixed direction per cast (it can blow either way), a strength that gusts, and the snow it carries.
    private readonly WindField _snow = new();
    private Vector2 _windDir = new(1f, 0.18f);
    private float _gustSeed;
    private const float WindSpeed = 560f;  // px/s at 1080p in a full gust (near flakes go ~1.4x, far ~0.4x)

    private CastFrame _f;

    public FrostEffect()
    {
        _mistPos = seed =>
        {
            var spots = _field.MistSpots;
            if (spots.Length == 0) return _f.Size * 0.5f;
            var jitter = new Vector2(DrawHelpers.HashRange(seed + 7, -40f, 40f), DrawHelpers.HashRange(seed + 8, -40f, 40f));
            return spots[(int)((uint)seed % (uint)spots.Length)] + jitter * _f.Px;
        };
        // Rises a little and is carried downwind.
        _mistVel = seed => (new Vector2(DrawHelpers.HashRange(seed + 3, -7f, 7f), DrawHelpers.HashRange(seed + 4, -16f, -4f))
                            + _windDir * DrawHelpers.HashRange(seed + 9, 22f, 52f)) * _f.Px;
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        var f = _f = _cast.Frame(screenSize, alpha, time, dt, colorOverride);

        // Fresh application (or a resized screen): lay out a new freeze.
        if (f.Fresh || f.Resized)
        {
            _field.Build(f.Seed, f.Size);
            _mist.Clear();

            int seed = f.Seed;
            _windDir = Vector2.Normalize(new Vector2(DrawHelpers.Hash01(seed + 21) < 0.5f ? -1f : 1f, 0.18f));
            _gustSeed = DrawHelpers.Hash01(seed + 22) * MathF.Tau;
            int flakes = Math.Clamp((int)(380f * MathF.Sqrt(f.Size.X * f.Size.Y / (1920f * 1080f))), 200, 560);
            _snow.Build(flakes, seed + 23, f.Size);
        }

        float thaw = MathF.Min(1f, f.Alpha);  // the front retreats as the effect fades out
        float prog = FrostField.ProgressAt(f.Age) * MathF.Pow(thaw, 0.6f);

        _field.Age = f.Age;
        _field.Progress = prog;
        _field.Fog = prog * 1.35f + 0.08f;
        _field.Alpha = f.Alpha;

        // the cover and the intro flash
        scene.AddRegion(new RegionPrimitive
        {
            Min = Vector2.Zero, Max = f.Size,
            Bottom = true,  // marks it an edge region -> the "Frost cover" slot
            Alpha = 1f,
            ColorOverride = f.Color,
            State = _field,
        });

        float introFlash = f.Age < 0.30f ? MathF.Exp(-f.Age / 0.07f) : 0f;
        if (introFlash > 0.01f)
        {
            scene.AddRegion(new RegionPrimitive
            {
                Min = Vector2.Zero, Max = f.Size,
                Tint = Flash,
                Alpha = introFlash * 0.34f * f.Alpha,
                ColorOverride = f.Color,
            });
        }

        // crystals on the glass (the hero slot)
        foreach (var fl in _field.Flowers)
        {
            float growth = Math.Clamp((f.Age - fl.Start) / 0.9f, 0f, 1f);
            if (growth <= 0f) continue;
            scene.AddParticle(new ParticlePrimitive
            {
                Position = fl.Pos, Size = fl.Size,
                AgeRatio = growth < 1f ? 0.20f * growth : 0.45f,  // grows in, then holds (no tumble)
                Brightness = f.Alpha * (0.55f + 0.40f * growth),
                Seed = fl.Seed, Role = PrimitiveRole.Snowflake, ColorOverride = f.Color,
            });
        }

        // mist, hanging just inside the front
        if (f.Age > 0.4f && _field.MistSpots.Length > 0)
        {
            _mist.Update(f.Time, f.Dt, spawnIntervalMin: 0.30f, spawnIntervalMax: 0.75f,
                         spawnPos: _mistPos, spawnVelocity: _mistVel,
                         lifespanMin: 3.5f, lifespanMax: 5.5f, sizeMin: 70f * f.Px, sizeMax: 140f * f.Px);
        }
        _mist.Emit(scene, f.Time, PrimitiveRole.Mist, brightnessMul: f.Alpha * Math.Clamp(f.Age * 0.6f, 0f, 1f), f.Color);

        // glints: nucleation flashes first, then the steady twinkle
        foreach (var site in _field.Growth.Sites)
        {
            float t = f.Age - FrostField.AgeFor(site.Delay * 0.9f);
            if (t < 0f || t > 0.8f) continue;
            scene.AddParticle(new ParticlePrimitive
            {
                Position = site.Pos + site.Inward * (14f * f.Px), Size = 78f * f.Px,
                Brightness = f.Alpha * MathF.Exp(-t / 0.20f),
                Seed = (int)(site.Pos.X * 7f + site.Pos.Y), Role = PrimitiveRole.Glint, ColorOverride = f.Color,
            });
        }
        foreach (var g in _field.Glints)
        {
            float since = f.Age - g.Start;
            if (since < 0f) continue;
            float twinkle = MathF.Pow(MathF.Max(0f, MathF.Sin(f.Age * g.Rate * 6.2832f + g.Phase)), g.Sharp);
            float born = since < 0.3f ? 0.9f * (1f - since / 0.3f) : 0f;  // a flash as the ice forms under it
            float inten = MathF.Max(twinkle, born);
            if (inten < 0.06f) continue;
            scene.AddParticle(new ParticlePrimitive
            {
                Position = g.Pos, Size = g.Size, Brightness = f.Alpha * inten,
                Seed = g.Seed, Role = PrimitiveRole.Glint, ColorOverride = f.Color,
            });
        }

        // wind and snow: builds as the glass freezes, drops away as it thaws; gusts streak the flakes
        float gust = WindField.Gust(f.Age, _gustSeed);
        float build = Math.Clamp((f.Age - 0.4f) / 3.5f, 0f, 1f);
        float density = build * MathF.Pow(thaw, 0.6f) * (0.70f + 0.30f * gust);
        _snow.Update(f.Time, f.Dt, f.Size, f.Px, _windDir * (WindSpeed * f.Px), gust);
        // Snow is white by nature, so it takes the override's hue but stays pale (see IceColor.Pastel).
        _snow.Emit(scene, f.Size, f.Px, PrimitiveRole.Snow, density, brightness: f.Alpha * (0.80f + 0.20f * gust), IceColor.Pastel(f.Color));
    }
}

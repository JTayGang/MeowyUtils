using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Frost: the cold closing in. Four particle fields (crystalline snowflakes, fine white specks,
/// tumbling ice shards, and creeping fog) drift across the screen, backed by a cold blue vignette.
///
/// INTRO (0.0 - ~1.1s): a pale blue-white flash the moment the debuff lands, plus a burst of
/// particles from every edge - the "cold snap". Emission rates run at 4x and ease back to steady
/// over the intro window; ice shards use a longer, flatter intro so the crystalline layer arrives
/// just after the initial flurry rather than competing with it.
///
/// STEADY STATE: snowflakes fall slowly from the top with wide lateral drift; snow specks fall
/// faster and are denser; ice crystals tumble slowly across the field with a slight upward bias;
/// fog drifts in from all four edges and hangs.
///
/// HERO ITEMS: four roles, each independently swappable.
///  - Role.Snowflake  (particle.snowflake):   the crystalline flakes.
///  - Role.Snow       (particle.snow):        the fine white specks.
///  - Role.Fog        (particle.fog):         the creeping cold mist.
///  - Role.IceCrystal (particle.ice-crystal): the angular tumbling shards.
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
        new("Particle", PrimitiveRole.Snowflake,  "Snowflakes",   "particle.snowflake"),
        new("Particle", PrimitiveRole.Snow,       "Snow specks",  "particle.snow"),
        new("Particle", PrimitiveRole.Fog,        "Fog",          "particle.fog"),
        new("Particle", PrimitiveRole.IceCrystal, "Ice crystals", "particle.ice-crystal"),
        new("Region",   PrimitiveRole.MainStroke, "Intro flash",  "region.flat-fill", "FlatFill"),
    };

    // ---- palette ----
    private static readonly uint DeepCold = DrawHelpers.ToU32(0.06f, 0.16f, 0.32f, 1f); // vignette
    private static readonly uint Flash    = DrawHelpers.ToU32(0.88f, 0.96f, 1.00f, 1f); // intro flash

    // ---- timing ----
    private const float IntroDuration     = 1.10f;

    // ---- emitters ----
    private readonly ParticleEmitter _snowflakes  = new(maxParticles: 45, seedSalt: 0xF00500);
    private readonly ParticleEmitter _snow        = new(maxParticles: 220, seedSalt: 0xF00501);
    private readonly ParticleEmitter _fog = new(maxParticles: 240, seedSalt: 0xF00502);
    private readonly ParticleEmitter _iceCrystals = new(maxParticles: 24, seedSalt: 0xF00503);

    // ---- state ----
    private readonly CastTracker _cast = new();

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        // Fresh application: reset the intro and drop any particles from the previous cast.
        if (_cast.Begin(time))
        {
            _snowflakes.Clear();
            _snow.Clear();
            _fog.Clear();
            _iceCrystals.Clear();
        }

        float age = time - _cast.Start;
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);

        // Intro curve: emission boost of 4x easing to 1x over IntroDuration, plus a brief flash.
        float introT = Math.Clamp(age / IntroDuration, 0f, 1f);
        float introBoost = 1f + 3f * (1f - DrawHelpers.EaseOutCubic(introT));
        float introFlash = age < 0.35f ? MathF.Exp(-age / 0.12f) : 0f;

        // Ice shards use a flatter intro curve so they don't compete with the initial snow
        // flurry; they build in slightly after the first wave and settle more slowly.
        float crystalIntro = age < 0.5f ? 0.5f + 2.0f * (age / 0.5f) : 2.5f - 1.5f * Math.Clamp((age - 0.5f) / 1.5f, 0f, 1f);

        // ---- 1. cold blue vignette ----
        float breath = 0.5f + 0.5f * DrawHelpers.Pulse(time, 5.5f);
        scene.RequestVignette(DeepCold, 0.18f, alpha * (0.55f + 0.15f * breath), priority: 40, colorOverride);

        // ---- 2. intro flash: pale blue full-screen tint that fades out fast ----
        if (introFlash > 0.01f)
        {
            scene.AddRegion(new RegionPrimitive
            {
                Min = Vector2.Zero,
                Max = screenSize,
                Tint = Flash,
                Alpha = introFlash * 0.42f * alpha,
                ColorOverride = colorOverride,
            });
        }

        // ---- 3. snowflakes: large crystals, slow fall, wide lateral drift ----
        _snowflakes.Update(
            time, dt,
            spawnIntervalMin: 0.16f / introBoost, spawnIntervalMax: 0.40f / introBoost,
            spawnPos: seed => new Vector2(
                DrawHelpers.HashRange(seed, -0.02f, 1.02f) * screenSize.X,
                -20f),
            spawnVelocity: seed => new Vector2(
                DrawHelpers.HashRange(seed + 10, -28f, 28f),
                DrawHelpers.HashRange(seed + 11, 35f, 85f)),
            lifespanMin: 6.0f, lifespanMax: 11.0f,
            sizeMin: shortSide * 0.011f, sizeMax: shortSide * 0.024f);

        _snowflakes.Emit(scene, time, PrimitiveRole.Snowflake,
                         brightnessMul: alpha, colorOverride, swayPerParticle: 8f);

        // ---- 4. snow: small fast specks, mostly from the top ----
        _snow.Update(
            time, dt,
            spawnIntervalMin: 0.015f / introBoost, spawnIntervalMax: 0.045f / introBoost,
            spawnPos: seed =>
            {
                if (DrawHelpers.Hash01(seed) < 0.70f)
                {
                    return new Vector2(
                        DrawHelpers.HashRange(seed + 10, -0.02f, 1.02f) * screenSize.X,
                        -10f);
                }
                bool left = DrawHelpers.Hash01(seed + 11) < 0.5f;
                return new Vector2(
                    left ? -10f : screenSize.X + 10f,
                    DrawHelpers.HashRange(seed + 12, 0f, 0.55f) * screenSize.Y);
            },
            spawnVelocity: seed => new Vector2(
                DrawHelpers.HashRange(seed + 20, -45f, 45f),
                DrawHelpers.HashRange(seed + 21, 120f, 280f)),
            lifespanMin: 3.0f, lifespanMax: 6.5f,
            sizeMin: shortSide * 0.0015f, sizeMax: shortSide * 0.0045f);

        _snow.Emit(scene, time, PrimitiveRole.Snow,
                   brightnessMul: alpha, colorOverride, swayPerParticle: 5f);

        // ---- 5. fog: a field of independent soft blobs creeping in from the edges ----
        // Spawn is heavily edge-biased: pick a random perimeter edge, a random position along it, then
        // offset inward by a distance drawn from pow(hash, 2.4) - which clusters most spawns close to
        // the edge with a long tail that occasionally reaches further in. That's the "hugging the
        // screen edges" behavior; uniform spawn had every particle filling the middle of the screen.
        //
        // Velocity has a small inward component so particles drift toward the center rather than
        // sitting statically on the edge; lifespans are short enough (3-6.5s) that they die before
        // reaching the middle, so the visual density stays concentrated near the borders.
        _fog.Update(
            time, dt,
            spawnIntervalMin: 0.015f, spawnIntervalMax: 0.045f,
            spawnPos: seed =>
            {
                int edge = (int)(DrawHelpers.Hash01(seed) * 4f);
                float along = DrawHelpers.HashRange(seed + 10, -0.05f, 1.05f);

                // pow(h, 2.4): ~40% of spawns land within ~10% of the edge, ~80% within ~30%.
                // The remaining tail reaches deeper in, so the field still has some volume - but the
                // density gradient is strongly toward the borders.
                float inwardMax = shortSide * 0.30f;
                float inward = MathF.Pow(DrawHelpers.Hash01(seed + 11), 2.4f) * inwardMax;

                return edge switch
                {
                    0 => new Vector2(along * screenSize.X, -6f + inward),                     // top
                    1 => new Vector2(screenSize.X + 6f - inward, along * screenSize.Y),        // right
                    2 => new Vector2(along * screenSize.X, screenSize.Y + 6f - inward),        // bottom
                    _ => new Vector2(-6f + inward, along * screenSize.Y),                     // left
                };
            },
            spawnVelocity: seed => new Vector2(
                DrawHelpers.HashRange(seed + 20, -10f, 10f),
                DrawHelpers.HashRange(seed + 21, -6f, 2f)),   // slight upward / inward creep
            lifespanMin: 3.0f, lifespanMax: 6.5f,
            sizeMin: shortSide * 0.05f, sizeMax: shortSide * 0.10f);

        _fog.Emit(scene, time, PrimitiveRole.Fog,
                  brightnessMul: alpha, colorOverride, swayPerParticle: 20f);

        // ---- 6. ice crystals: angular shards tumbling slowly across the field ----
        // Spawned from just above the top so they cross the whole screen before despawning; a
        // small upward bias in the velocity slows their descent relative to the snow so the
        // crystalline layer hangs in the air rather than raining down with everything else.
        _iceCrystals.Update(
            time, dt,
            spawnIntervalMin: 0.35f / crystalIntro, spawnIntervalMax: 0.75f / crystalIntro,
            spawnPos: seed => new Vector2(
                DrawHelpers.HashRange(seed, -0.02f, 1.02f) * screenSize.X,
                -30f),
            spawnVelocity: seed => new Vector2(
                DrawHelpers.HashRange(seed + 10, -22f, 22f),
                DrawHelpers.HashRange(seed + 11, 18f, 48f)),
            lifespanMin: 4.0f, lifespanMax: 8.0f,
            sizeMin: shortSide * 0.005f, sizeMax: shortSide * 0.012f);

        _iceCrystals.Emit(scene, time, PrimitiveRole.IceCrystal,
                          brightnessMul: alpha, colorOverride, swayPerParticle: 6f);
    }
}
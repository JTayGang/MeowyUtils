using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Frost: the cold closing in. Three particle fields (crystalline snowflakes, small white specks,
/// and creeping fog) drift across the screen, backed by a cold blue vignette.
///
/// INTRO (0.0 - ~0.9s): a pale blue-white flash the moment the debuff lands, plus a burst of
/// particles from every edge - the "cold snap". Emission rates run at 4x and ease back to steady
/// over the intro window.
///
/// STEADY STATE: snowflakes fall slowly from the top with wide lateral drift; snow specks fall
/// faster and are denser; fog drifts in from all four edges and hangs.
///
/// HERO ITEMS: three roles, each independently swappable.
///  - Role.Snowflake (particle.snowflake): the crystalline flakes.
///  - Role.Snow      (particle.snow):      the fine white specks.
///  - Role.Fog       (particle.fog):       the creeping cold mist.
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

    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Particle", PrimitiveRole.Snowflake),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Particle", PrimitiveRole.Snowflake,  "Snowflakes",  "particle.snowflake"),
        new("Particle", PrimitiveRole.Snow,       "Snow specks", "particle.snow"),
        new("Particle", PrimitiveRole.Fog,        "Fog",         "particle.fog"),
        new("Region",   PrimitiveRole.MainStroke, "Intro flash", "region.flat-fill", "FlatFill"),
    };

    // ---- palette ----
    private static readonly uint DeepCold = DrawHelpers.ToU32(0.06f, 0.16f, 0.32f, 1f); // vignette
    private static readonly uint Flash    = DrawHelpers.ToU32(0.88f, 0.96f, 1.00f, 1f); // intro flash

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;
    private const float IntroDuration     = 0.90f;

    // ---- emitters ----
    private readonly ParticleEmitter _snowflakes = new(maxParticles: 50,  seedSalt: 0xF00500);
    private readonly ParticleEmitter _snow       = new(maxParticles: 220, seedSalt: 0xF00501);
    private readonly ParticleEmitter _fog        = new(maxParticles: 30,  seedSalt: 0xF00502);

    // ---- state ----
    private Vector2 _screenSize;
    private float _castStart = -1f;
    private float _lastDrawTime = -100f;

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, Vector4? colorOverride)
    {
        _screenSize = screenSize;

        // Fresh application: reset the intro and drop any particles from the previous cast.
        if (time - _lastDrawTime > NewCastGapSeconds)
        {
            _castStart = time;
            _snowflakes.Clear();
            _snow.Clear();
            _fog.Clear();
        }
        _lastDrawTime = time;

        float age = time - _castStart;
        float dt = ImGui.GetIO().DeltaTime;
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);

        // Intro curve: emission boost of 4x easing to 1x over IntroDuration, plus a brief flash.
        float introT = Math.Clamp(age / IntroDuration, 0f, 1f);
        float introBoost = 1f + 3f * (1f - DrawHelpers.EaseOutCubic(introT));
        float introFlash = age < 0.35f ? MathF.Exp(-age / 0.12f) : 0f;

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
            spawnIntervalMin: 0.14f / introBoost, spawnIntervalMax: 0.34f / introBoost,
            spawnPos: seed => new Vector2(
                DrawHelpers.HashRange(seed, -0.02f, 1.02f) * screenSize.X,
                -20f),
            spawnVelocity: seed => new Vector2(
                DrawHelpers.HashRange(seed + 10, -28f, 28f),
                DrawHelpers.HashRange(seed + 11, 35f, 85f)),
            lifespanMin: 6.0f, lifespanMax: 11.0f,
            sizeMin: shortSide * 0.010f, sizeMax: shortSide * 0.020f);

        _snowflakes.Emit(scene, time, PrimitiveRole.Snowflake,
                         brightnessMul: alpha, colorOverride, swayPerParticle: 8f);

        // ---- 4. snow: small fast specks, mostly from the top ----
        _snow.Update(
            time, dt,
            spawnIntervalMin: 0.015f / introBoost, spawnIntervalMax: 0.045f / introBoost,
            spawnPos: seed =>
            {
                // 70% from the top edge, 30% from the upper sides.
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

        // ---- 5. fog: large soft blobs drifting in from the edges ----
        _fog.Update(
            time, dt,
            spawnIntervalMin: 0.30f, spawnIntervalMax: 0.80f,
            spawnPos: seed =>
            {
                int edge = (int)(DrawHelpers.Hash01(seed) * 4f);
                float along = DrawHelpers.HashRange(seed + 10, 0f, 1f);
                const float margin = 40f;
                return edge switch
                {
                    0 => new Vector2(along * screenSize.X, -margin),
                    1 => new Vector2(screenSize.X + margin, along * screenSize.Y),
                    2 => new Vector2(along * screenSize.X, screenSize.Y + margin),
                    _ => new Vector2(-margin, along * screenSize.Y),
                };
            },
            spawnVelocity: seed => new Vector2(
                DrawHelpers.HashRange(seed + 20, -14f, 14f),
                DrawHelpers.HashRange(seed + 21, -6f, 18f)),
            lifespanMin: 5.0f, lifespanMax: 10.0f,
            sizeMin: shortSide * 0.10f, sizeMax: shortSide * 0.20f);

        _fog.Emit(scene, time, PrimitiveRole.Fog,
                  brightnessMul: alpha, colorOverride, swayPerParticle: 12f);
    }
}
using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Blind: near-total black wash plus heavy vignette (priority 100, so it owns the vignette). No hue, so colour overrides do nothing.</summary>
public sealed class BlindEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Blind;
    public string DisplayName => "Blind";
    public string Description => "Screen darkens with a heavy vignette.";
    public int DrawOrder => 0;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Blind"] = 1.0f,
    };

    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Region", PrimitiveRole.MainStroke),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Region", PrimitiveRole.MainStroke, "Screen wash", "region.flat-fill", "FlatFill"),
    };

    private const uint Black = 0xFF000000u;

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        float breathe = 0.92f + 0.08f * DrawHelpers.Pulse(time, 2.6f);
        float a = alpha * breathe;

        // Flat wash: full-screen rectangle with no edge flags, so the renderer picks region.flat-fill.
        scene.AddRegion(new RegionPrimitive
        {
            Min = Vector2.Zero,
            Max = screenSize,
            Tint = Black,
            Alpha = a * 0.55f,
            ColorOverride = colorOverride,
        });

        // Heavy vignette (the renderer draws vignette, regions, strokes, particles in that order).
        scene.RequestVignette(Black, 0.22f, a, priority: 100, colorOverride);
    }
}
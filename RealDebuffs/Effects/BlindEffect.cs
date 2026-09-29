using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Your eyes just stopped working. A flat near-total black wash over the whole screen plus a
/// heavy black vignette at the edges, both breathing slightly so it doesn't read as a static
/// overlay. Vignette priority 100 (highest): if Blind is up, it owns the vignette, and anything
/// else that wanted one this frame yields.
///
/// Note: the black tint has no hue (Value = 0 in HSV terms), so a tooltip color override has no
/// visible effect on Blind.
/// </summary>
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

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, Vector4? colorOverride)
    {
        float breathe = 0.92f + 0.08f * DrawHelpers.Pulse(time, 2.6f);
        float a = alpha * breathe;

        // Flat wash: full-screen rectangle, no edge flags → the renderer picks region.flat-fill.
        // The region's alpha is this frame's effect alpha (fade × strength) baked in; the renderer
        // multiplies global intensity on top, matching how every effect was scaled before.
        scene.AddRegion(new RegionPrimitive
        {
            Min = Vector2.Zero,
            Max = screenSize,
            Tint = Black,
            Alpha = a * 0.55f,
            ColorOverride = colorOverride,
        });

        // Heavy vignette layered on top of the flat wash by the renderer (vignette draws first,
        // then regions, then strokes, then particles - so Blind's region and its vignette both
        // land in the right order without any explicit coordination here).
        scene.RequestVignette(Black, 0.22f, a, priority: 100, colorOverride);
    }
}
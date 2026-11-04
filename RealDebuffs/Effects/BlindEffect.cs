using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Blind: a black-out beat, then a near-black wash and heavy vignette (priority 100, so it owns the vignette).</summary>
public sealed class BlindEffect : SceneEffect, IHasSwappableSlots
{
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        Slot.Region(RegionKind.FlatFill, "Screen wash", "region.flat-fill", hero: true),
    };

    private const uint Black = 0xFF000000u;

    public BlindEffect() : base(DebuffKind.Blind, "Blind", "The lights cut out, then the screen stays dark with a heavy vignette.", 0, "Blind", envelope: true) { }

    protected override void Emit(EffectScene scene, in CastFrame f)
    {
        float a = f.Alpha * (0.92f + 0.08f * DrawHelpers.Pulse(f.Time, 2.6f));

        // The lights cut out: a hard black beat on the cast that settles into tunnel vision.
        float cut = MathF.Exp(-f.Age / 0.30f);
        scene.Wash(f.Size, Black, MathF.Min(1f, a * 0.55f + 0.40f * cut * f.Gate), f.Color);
        scene.RequestVignette(Black, 0.22f + 0.16f * MathF.Exp(-f.Age / 0.45f), a, priority: 100, f.Color);
    }
}

using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>Base for viscous-liquid particles (slime, blood, sludge, venom): draws a drop with neck and string (GoopDraw), shaded by its LiquidSpec, and declares the drip emission. To add a liquid, subclass: pick a LiquidPresets entry, name it, add a few words.</summary>
public abstract class ParticleGoop : IParticleMaterial
{
    private readonly LiquidMatcap _liquid;
    private readonly StrokeEmission[] _emissions;

    protected ParticleGoop(in LiquidSpec liquid, StrokeEmission[] emissions)
    {
        _liquid = new LiquidMatcap(in liquid);
        _emissions = emissions;
    }

    public abstract string Name { get; }
    public virtual string[] NaturalLanguageWords => Array.Empty<string>();

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx) =>
        GoopDraw.Draw(dl, in p, _liquid, in ctx);

    public ReadOnlySpan<StrokeEmission> Emissions => _emissions;
}

/// <summary>Thick, translucent, sickly-green mucus: hangs in long stretching strings, lets go, falls as a glossy teardrop. Disease's drips; also "made of slime".</summary>
public sealed class ParticleSlime : ParticleGoop
{
    public ParticleSlime() : base(LiquidPresets.Slime, Spec) { }

    public override string Name => "particle.slime";
    public override string[] NaturalLanguageWords { get; } =
        { "slime", "slimy", "ooze", "oozing", "goo", "goop", "mucus", "gunk" };

    /// <summary>Thick and slow: long cycles, long necks, drops that hang for a while before they let go.</summary>
    private static readonly StrokeEmission[] Spec =
    {
        new(
            Role: PrimitiveRole.Goop,
            DensityPer100px: 0f,                       // ignored: a drip block spaces its own sites
            SpeedMin: 60f, SpeedMax: 130f,             // the drop's speed the instant it lets go
            LifespanMin: 2.4f, LifespanMax: 3.2f,
            SizeMin: 3.4f, SizeMax: 5.6f,
            SpreadRadians: 0f,
            BiasVelocity: Vector2.Zero,
            Gravity: new Vector2(0f, 1500f),
            Drip: new StrokeDripOptions(
                SiteSpacingPx: 150f,
                CycleSecondsMin: 3.0f, CycleSecondsMax: 5.4f,
                MinSlope: 0.42f,
                Stringiness: 0.85f,
                SatelliteChance: 0.55f)),
    };
}

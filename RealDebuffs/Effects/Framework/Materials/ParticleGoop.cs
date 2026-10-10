using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Base for every viscous-liquid particle: slime, blood, sludge, venom. It draws a drop with its neck and
/// trailing string (see <see cref="GoopDraw"/>), shaded as the <see cref="LiquidSpec"/> it was built with,
/// and declares the drip emission that lets any strand ("chains with slime") grow drops.
///
/// To add a liquid, subclass this: pick or add a <see cref="LiquidPresets"/> entry, give the material a
/// name and a few words, and (optionally) tune the drip emission. That is the whole job; the physics of a
/// drop forming, stretching and letting go lives in <see cref="GoopEmitter"/> and is shared by all of them.
/// </summary>
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

using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A soft ice glint: light catching a crystal, not a graphic crystal shape. The visible
/// structure is a faint diamond outline under a bright center pip and a brighter long-axis
/// line - the whole thing reads as a momentary cold sparkle rather than as a floating object.
///
/// The earlier version drew a solid triangle with a hard edge stroke, which read as a graphic
/// navigation marker against the soft particle field. The fix is three things at once: much
/// lower per-layer alpha (0.10-0.22, versus 0.12-0.85 before), no hard single-alpha outline
/// (soft multi-layer glow instead), and a shape whose brightness is concentrated in the very
/// center rather than along the edges - light glinting off ice, rather than an ice shape.
/// </summary>
public sealed class ParticleIceCrystal : IParticleMaterial
{
    public string Name => "particle.ice-crystal";
    public string[] NaturalLanguageWords { get; } =
        { "ice", "crystal", "crystals", "shard", "shards", "icicle", "icicles" };

    private static readonly uint Glow = DrawHelpers.ToU32(0.55f, 0.78f, 1.00f, 1f);
    private static readonly uint Core = DrawHelpers.ToU32(0.92f, 0.98f, 1.00f, 1f);
    private static readonly uint Hot  = DrawHelpers.ToU32(1.00f, 1.00f, 1.00f, 1f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.5f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        // Glint envelope: quick in, brief hold, slow out. A glint shouldn't linger - it appears,
        // it catches the light, it fades. The quicker fade-out than other particles is deliberate.
        float sizeT;
        if      (p.AgeRatio < 0.20f) sizeT = 0.30f + 0.70f * (p.AgeRatio / 0.20f);
        else if (p.AgeRatio < 0.55f) sizeT = 1f;
        else                          sizeT = MathF.Max(0.05f, 1f - (p.AgeRatio - 0.55f) / 0.45f);

        float size = p.Size * sizeT;
        if (size <= 0.5f) return;

        // Slow rotation - glints feel almost still, not tumbling.
        float rot = (p.Seed & 0xFF) * 0.0246f + p.AgeRatio * 0.8f;

        var pos = p.Position + new Vector2(p.Sway, 0f);

        // Soft glow is the majority of what's visible. Two stages (wide+faint, tighter+slightly
        // brighter) give the glint volume without a hard edge anywhere.
        dl.AddCircleFilled(pos, size * 1.6f, DrawHelpers.WithAlpha(Glow, alpha * 0.10f));
        dl.AddCircleFilled(pos, size * 0.9f, DrawHelpers.WithAlpha(Glow, alpha * 0.15f));

        // Diamond axes (long vertical, short horizontal, rotated by the seed).
        Vector2 longDir  = new(MathF.Cos(rot), MathF.Sin(rot));
        Vector2 shortDir = new(-longDir.Y, longDir.X);
        float longR  = size * 1.3f;
        float shortR = size * 0.55f;

        Vector2 top    = pos + longDir  * longR;
        Vector2 bottom = pos - longDir  * longR;
        Vector2 left   = pos + shortDir * shortR;
        Vector2 right  = pos - shortDir * shortR;

        // Very faint diamond outline - barely visible, exists only to hint at structure. Low
        // alpha + thin width is the difference between "a hint of crystal" and "a triangle".
        uint outline = DrawHelpers.WithAlpha(Core, alpha * 0.22f);
        dl.AddLine(top,    right,  outline, 0.55f);
        dl.AddLine(right,  bottom, outline, 0.55f);
        dl.AddLine(bottom, left,   outline, 0.55f);
        dl.AddLine(left,   top,    outline, 0.55f);

        // Bright long axis - the glint itself. This is what the eye actually latches onto.
        dl.AddLine(top, bottom, DrawHelpers.WithAlpha(Core, alpha * 0.60f), 0.85f);
        dl.AddLine(left, right, DrawHelpers.WithAlpha(Core, alpha * 0.35f), 0.55f);

        // Bright center pip - the point where the glint "hits." Concentrating the brightness
        // here, rather than along the outline, is what makes it read as light rather than shape.
        dl.AddCircleFilled(pos, MathF.Max(0.7f, size * 0.18f), DrawHelpers.WithAlpha(Hot, alpha * 0.90f));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    private static readonly StrokeEmission[] EmissionSpecs =
    {
        new(Role: PrimitiveRole.IceCrystal,
            DensityPer100px: 0.6f,
            SpeedMin: 20f, SpeedMax: 55f,
            LifespanMin: 1.2f, LifespanMax: 2.4f,
            SizeMin: 6f, SizeMax: 12f,
            SpreadRadians: 1.2f,
            BiasVelocity: new Vector2(0f, 6f),
            PrimaryDirection: new Vector2(0f, -1f)),
    };
}
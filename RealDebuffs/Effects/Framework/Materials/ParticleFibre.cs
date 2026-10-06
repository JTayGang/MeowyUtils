using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Fibre: a loose filament of hemp, wool, hair or thread that has worked free and is drifting down.
/// A hair-thin curl of three short segments, tumbling slowly, that brightens briefly as it turns to
/// catch the key light. It is deliberately nearly weightless: no streak, no halo, so a few of them
/// read as lint in the air rather than as sparks.
///
/// Used by StrokeRope for what a rope sheds and what a landing or a snap shakes out of it, but it is
/// the general "fine loose strand" material: cobweb, hair, straw and frayed cloth are this with a
/// different palette.
/// </summary>
public sealed class ParticleFibre : IParticleMaterial
{
    public string Name => "particle.fibre";
    public string[] NaturalLanguageWords { get; } = { "fibre", "fibres", "fiber", "fibers", "lint", "fluff" };

    private static readonly uint Lit   = FireColor.Pack(0.86f, 0.74f, 0.54f);
    private static readonly uint Dull  = FireColor.Pack(0.52f, 0.42f, 0.28f);
    private static readonly uint Glint = FireColor.Pack(1.00f, 0.94f, 0.80f);

    private const int Segments = 3;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.5f) return;

        int seed = p.Seed;
        float t = ctx.Time;

        // Tumble: a slow spin in the screen plane, and a flip that foreshortens the curl.
        float spin = DrawHelpers.Hash01(seed + 1) * MathF.Tau + t * DrawHelpers.HashRange(seed + 2, -2.2f, 2.2f);
        float flip = DrawHelpers.Hash01(seed + 3) * MathF.Tau + t * DrawHelpers.HashRange(seed + 4, 1.5f, 5.0f);
        float face = MathF.Cos(flip);
        float squash = 0.30f + 0.70f * MathF.Abs(face);

        // Glints once per flip, when the filament turns square to the key light.
        float glint = MathF.Pow(MathF.Max(0f, MathF.Cos(flip - 0.9f)), 8f);
        uint col = DrawHelpers.LerpColor(DrawHelpers.Hash01(seed + 5) < 0.6f ? Lit : Dull, Glint, glint * 0.7f);

        float len = p.Size * (1f - 0.20f * p.AgeRatio);
        float step = len / Segments;
        float bend = DrawHelpers.HashRange(seed + 6, -1.1f, 1.1f);
        float width = MathF.Max(0.9f, ctx.ScreenScale * 1.05f);

        Vector2 pos = p.Position + new Vector2(p.Sway, 0f);
        float heading = spin;
        Vector2 prev = pos;
        for (int i = 0; i < Segments; i++)
        {
            heading += bend / Segments;
            // The squash flattens the curl along its tilt axis, so the filament reads as 3D.
            Vector2 dir = new(MathF.Cos(heading), MathF.Sin(heading) * squash);
            Vector2 next = prev + dir * step;

            float taper = 1f - 0.28f * i;
            dl.AddLine(prev, next, DrawHelpers.WithAlpha(col, k * 0.80f * taper), width);
            prev = next;
        }
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    // Fibres working loose of another effect's strokes: sparse, slow, hanging in the air.
    private static readonly StrokeEmission[] EmissionSpecs =
    {
        new(Role: PrimitiveRole.Fibre,
            DensityPer100px: 0.55f,
            SpeedMin: 3f, SpeedMax: 16f,
            LifespanMin: 1.4f, LifespanMax: 2.8f,
            SizeMin: 7f, SizeMax: 15f,
            SpreadRadians: 1.4f,
            BiasVelocity: new Vector2(0f, 5f),
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 14f)),
    };
}

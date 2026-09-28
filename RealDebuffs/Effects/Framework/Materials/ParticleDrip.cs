using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A slow-falling droplet: a small stretched bead with a thin taper behind it, reading as a
/// heavier-than-water glob that gravity is pulling down. Velocity-stretched rather than round,
/// so it reads as falling even at small sizes.
/// </summary>
public sealed class ParticleDrip : IParticleMaterial
{
    public string Name => "particle.drip";

    private static readonly uint Body      = DrawHelpers.ToU32(0.55f, 0.75f, 0.20f, 1f);
    private static readonly uint Highlight = DrawHelpers.ToU32(0.92f, 1.00f, 0.55f, 1f);
    private static readonly uint Shadow    = DrawHelpers.ToU32(0.10f, 0.15f, 0.03f, 1f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.3f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        float sizeT = p.AgeRatio < 0.10f
            ? 0.55f + 0.45f * (p.AgeRatio / 0.10f)
            : MathF.Max(0.20f, 1f - (p.AgeRatio - 0.10f) / 0.90f);

        float size = p.Size * sizeT;
        if (size <= 0.3f) return;

        // Stretch along velocity: a falling droplet is a teardrop, not a sphere.
        Vector2 vel = p.Velocity;
        float speed = vel.Length();
        Vector2 dir = speed > 1f ? vel / speed : new Vector2(0f, 1f);
        Vector2 perp = new(-dir.Y, dir.X);

        // Stretch scales with speed, capped so a fast drip doesn't become a noodle.
        float stretch = MathF.Min(size * 3.5f, size * (1.2f + speed * 0.02f));
        float halfLen = stretch * 0.5f - size;

        Vector2 head = p.Position + dir * (halfLen > 0f ? halfLen : 0f);
        Vector2 tail = p.Position - dir * (halfLen > 0f ? halfLen : 0f);

        // Body: an elongated lozenge drawn as two lines + two caps.
        dl.AddLine(tail, head, DrawHelpers.WithAlpha(Shadow, alpha * 0.55f), size * 2.2f);
        dl.AddLine(tail, head, DrawHelpers.WithAlpha(Body, alpha * 0.90f), size * 1.6f);
        dl.AddCircleFilled(head, size,        DrawHelpers.WithAlpha(Body, alpha * 0.95f));
        dl.AddCircleFilled(tail, size * 0.75f, DrawHelpers.WithAlpha(Body, alpha * 0.75f));
        dl.AddCircleFilled(head, size * 0.45f, DrawHelpers.WithAlpha(Highlight, alpha * 0.85f));

        _ = perp;
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    /// <summary>
    /// The canonical drip emission when this material is used as a stroke emitter ("chains with
    /// drips", or any future "... with drips" phrase). Half the spawns fall under gravity, half
    /// run along the strand catching on generic obstacles. Matches StrokeParasite.Combined so a
    /// drip behaves the same regardless of which strand is shedding it.
    /// </summary>
    private static readonly StrokeEmission[] EmissionSpecs =
    {
        new(
            Role: PrimitiveRole.Drip,
            DensityPer100px: 0.4f,
            SpeedMin: 8f, SpeedMax: 22f,
            LifespanMin: 1.6f, LifespanMax: 2.6f,
            SizeMin: 2.0f, SizeMax: 4.5f,
            SpreadRadians: 0.18f,
            BiasVelocity: Vector2.Zero,
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 420f),
            Flow: new StrokeFlowOptions(
                Share: 0.5f,
                SpeedMin: 55f, SpeedMax: 110f,
                WobbleAmplitude: 1.8f,
                WobbleFrequencyHz: 0.7f,
                ObstacleSpacingPx: 45f,   // matches StrokeParasite.SuckerSpacing
                LateralOffsetFrac: 0.45f)),
    };
}
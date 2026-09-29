using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A drifting crystalline snowflake: six arms radiating from the center, each carrying three
/// tapering branch pairs and a small forked tip, connected by a faint inner hexagon at the
/// arm-midpoint radius. Drawn with thin layered lines (a soft blue glow under a near-white
/// core) so the flake reads as ice rather than as a bright stick figure. Rotation is derived
/// from the particle's seed (each flake has its own starting angle) and advances slowly with
/// AgeRatio, so flakes tumble as they fall without needing per-particle state.
///
/// The extra detail (three branch pairs, forked tips, inner hexagon) is what pushes this from
/// "asterisk" to "snowflake" at 20-30px. Below ~12px the branches start to blur into the arms
/// and it degrades gracefully into a bright crystalline speck, which is fine.
/// </summary>
public sealed class ParticleSnowflake : IParticleMaterial
{
    public string Name => "particle.snowflake";
    public string[] NaturalLanguageWords { get; } = { "snowflake", "snowflakes" };

    private static readonly uint Glow = DrawHelpers.ToU32(0.62f, 0.82f, 1.00f, 1f);
    private static readonly uint Core = DrawHelpers.ToU32(0.94f, 0.98f, 1.00f, 1f);
    private static readonly uint Hot  = DrawHelpers.ToU32(1.00f, 1.00f, 1.00f, 1f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.5f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        float sizeT;
        if      (p.AgeRatio < 0.20f) sizeT = 0.40f + 0.60f * (p.AgeRatio / 0.20f);
        else if (p.AgeRatio < 0.75f) sizeT = 1f;
        else                          sizeT = MathF.Max(0.10f, 1f - (p.AgeRatio - 0.75f) / 0.25f);

        float size = p.Size * sizeT;
        if (size <= 0.5f) return;

        // Per-flake starting rotation from seed, plus a slightly faster tumble so the extra
        // branch detail is legible over the flake's lifetime.
        float rot = (p.Seed & 0xFF) * 0.0246f + p.AgeRatio * 2.2f;

        var pos = p.Position + new Vector2(p.Sway, 0f);

        // Two-stage halo: wide faint wash under a tighter brighter glow. Both together give the
        // flake a real "glowing in the cold" presence that a single small halo doesn't.
        dl.AddCircleFilled(pos, size * 0.95f, DrawHelpers.WithAlpha(Glow, alpha * 0.08f));
        dl.AddCircleFilled(pos, size * 0.55f, DrawHelpers.WithAlpha(Glow, alpha * 0.12f));

        // Inner facet hexagon: connects the arm-midpoints at low alpha. Deliberately subtle -
        // it exists to break the "six straight spokes" silhouette into something that reads as
        // crystalline without turning the flake into a solid shape.
        float hexR = size * 0.55f;
        for (int i = 0; i < 6; i++)
        {
            float a1 = rot + i * (MathF.PI / 3f);
            float a2 = rot + (i + 1) * (MathF.PI / 3f);
            Vector2 v1 = pos + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * hexR;
            Vector2 v2 = pos + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * hexR;
            dl.AddLine(v1, v2, DrawHelpers.WithAlpha(Glow, alpha * 0.18f), 0.7f);
        }

        // Six arms, offset 60° apart.
        for (int arm = 0; arm < 6; arm++)
        {
            float ang = rot + arm * (MathF.PI / 3f);
            Vector2 dir = new(MathF.Cos(ang), MathF.Sin(ang));
            Vector2 tip = pos + dir * size;

            // Arm: soft glow under a bright core.
            dl.AddLine(pos, tip, DrawHelpers.WithAlpha(Glow, alpha * 0.42f), 1.9f);
            dl.AddLine(pos, tip, DrawHelpers.WithAlpha(Core, alpha * 0.90f), 0.9f);

            // Three branch pairs per arm, tapering toward the tip.
            for (int b = 0; b < 3; b++)
            {
                float t = 0.32f + 0.22f * b;
                Vector2 at = Vector2.Lerp(pos, tip, t);
                float branchLen = size * (0.30f - 0.06f * b);

                for (int side = -1; side <= 1; side += 2)
                {
                    float bang = ang + side * 1.0f;
                    Vector2 bdir = new(MathF.Cos(bang), MathF.Sin(bang));
                    Vector2 btip = at + bdir * branchLen;

                    dl.AddLine(at, btip, DrawHelpers.WithAlpha(Glow, alpha * 0.28f), 1.2f);
                    dl.AddLine(at, btip, DrawHelpers.WithAlpha(Core, alpha * 0.72f), 0.55f);
                }
            }

            // Tiny forked tip: a small V splitting off the end of the arm. Cheap, and it's
            // exactly the shape that says "snowflake" rather than "asterisk".
            Vector2 forkBase = pos + dir * (size * 0.92f);
            for (int side = -1; side <= 1; side += 2)
            {
                float fang = ang + side * 0.5f;
                Vector2 fdir = new(MathF.Cos(fang), MathF.Sin(fang));
                Vector2 ftip = forkBase + fdir * (size * 0.14f);
                dl.AddLine(forkBase, ftip, DrawHelpers.WithAlpha(Core, alpha * 0.65f), 0.5f);
            }
        }

        // Bright center pip so the flake has a visible anchor.
        dl.AddCircleFilled(pos, MathF.Max(0.8f, size * 0.10f), DrawHelpers.WithAlpha(Hot, alpha * 0.95f));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    private static readonly StrokeEmission[] EmissionSpecs =
    {
        new(
        Role: PrimitiveRole.Snowflake,
        DensityPer100px: 1.2f,
        SpeedMin: 12f, SpeedMax: 30f,
        LifespanMin: 1.4f, LifespanMax: 2.6f,
        SizeMin: 4f, SizeMax: 8f,
        SpreadRadians: 0.9f,
        BiasVelocity: new Vector2(0f, 8f),
        PrimaryDirection: new Vector2(0f, 1f)),
    };
}
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A drifting crystalline snowflake: six arms radiating from the center, each carrying two
/// tapering branch pairs. Drawn with thin layered lines (a soft blue glow under a near-white
/// core) so the flake reads as ice rather than as a bright stick figure. Rotation is derived
/// from the particle's seed (each flake has its own starting angle) and advances slowly with
/// AgeRatio, so flakes tumble as they fall without ever needing per-particle state beyond what
/// the primitive already carries.
///
/// The whole flake scales with the primitive's Size; at typical sizes (~15-25px) the branch
/// pattern reads as a proper snowflake. At much smaller sizes it collapses to a sparkle, which
/// is a fine degradation - small snowflakes are indistinguishable from snow specks anyway.
/// </summary>
public sealed class ParticleSnowflake : IParticleMaterial
{
    public string Name => "particle.snowflake";

    private static readonly uint Glow = DrawHelpers.ToU32(0.62f, 0.82f, 1.00f, 1f); // pale blue haze
    private static readonly uint Core = DrawHelpers.ToU32(0.94f, 0.98f, 1.00f, 1f); // near-white ice
    private static readonly uint Hot  = DrawHelpers.ToU32(1.00f, 1.00f, 1.00f, 1f); // tiny bright center

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.5f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        // Grow-in / hold / shrink-out size envelope.
        float sizeT;
        if      (p.AgeRatio < 0.20f) sizeT = 0.40f + 0.60f * (p.AgeRatio / 0.20f);
        else if (p.AgeRatio < 0.75f) sizeT = 1f;
        else                          sizeT = MathF.Max(0.10f, 1f - (p.AgeRatio - 0.75f) / 0.25f);

        float size = p.Size * sizeT;
        if (size <= 0.5f) return;

        // Per-flake starting rotation (from seed) plus a slow continuous tumble over its life.
        float rot = (p.Seed & 0xFF) * 0.0246f + p.AgeRatio * 1.6f;

        var pos = p.Position + new Vector2(p.Sway, 0f);

        // Soft halo behind the whole flake.
        dl.AddCircleFilled(pos, size * 0.75f, DrawHelpers.WithAlpha(Glow, alpha * 0.14f));

        // Six arms, offset 60° apart.
        for (int arm = 0; arm < 6; arm++)
        {
            float ang = rot + arm * (MathF.PI / 3f);
            Vector2 dir = new(MathF.Cos(ang), MathF.Sin(ang));
            Vector2 tip = pos + dir * size;

            // Arm: soft glow under a bright core.
            dl.AddLine(pos, tip, DrawHelpers.WithAlpha(Glow, alpha * 0.42f), 1.9f);
            dl.AddLine(pos, tip, DrawHelpers.WithAlpha(Core, alpha * 0.90f), 0.9f);

            // Two branch pairs per arm, tapering as they go out.
            for (int b = 0; b < 2; b++)
            {
                float t = 0.42f + 0.28f * b;
                Vector2 at = Vector2.Lerp(pos, tip, t);
                float branchLen = size * (0.32f - 0.07f * b);

                for (int side = -1; side <= 1; side += 2)
                {
                    float bang = ang + side * 1.05f;
                    Vector2 bdir = new(MathF.Cos(bang), MathF.Sin(bang));
                    Vector2 btip = at + bdir * branchLen;

                    dl.AddLine(at, btip, DrawHelpers.WithAlpha(Glow, alpha * 0.30f), 1.3f);
                    dl.AddLine(at, btip, DrawHelpers.WithAlpha(Core, alpha * 0.78f), 0.6f);
                }
            }
        }

        // Bright center pip so the flake has a visible anchor.
        dl.AddCircleFilled(pos, MathF.Max(0.7f, size * 0.09f), DrawHelpers.WithAlpha(Hot, alpha * 0.95f));
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
        PrimaryDirection: new Vector2(0f, 1f)), // fall gently
    };
}
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>Snow speck: dot with pale halo, a glint cross on ~1/4, and a comet streak when wind-blown.</summary>
public sealed class ParticleSnow : IParticleMaterial
{
    public string Name => "particle.snow";
    public string[] NaturalLanguageWords { get; } = { "snow", "frost", "hoarfrost", "rime" };

    private const float StreakFromSpeed = 100f;   // px/s at 1080p; chain-shed snow tops out near 57, so it never streaks
    private const float StreakSeconds = 0.07f;    // seconds of travel drawn behind a speck
    private const float StreakMinSizes = 0.6f;    // streak must be this many specks long before the comet replaces the dot

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.3f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        float size = p.Size * DrawHelpers.LifeSize(p.AgeRatio, riseEnd: 0.15f, riseFrom: 0.30f, fallStart: 0.80f, floor: 0.10f);
        if (size <= 0.3f) return;

        Vector2 pos = p.DrawPos;

        float speed = p.Velocity.Length();
        float streak = MathF.Min(MathF.Max(0f, speed - StreakFromSpeed * ctx.ScreenScale) * StreakSeconds, size * 14f);
        if (streak > size * StreakMinSizes)
        {
            DrawComet(dl, pos, p.Velocity / speed, size, streak, alpha, ctx);
            return;
        }

        dl.AddCircleFilled(pos, size * 2.2f, DrawHelpers.WithAlpha(IceColor.Cold, alpha * 0.22f));
        dl.AddCircleFilled(pos, size,        DrawHelpers.WithAlpha(IceColor.Core, alpha * 0.92f));

        // Seed-based, so a speck keeps its glint for life.
        if ((p.Seed & 3) == 0)
        {
            float r = size * 2.6f;
            uint glint = DrawHelpers.WithAlpha(IceColor.Core, alpha * 0.55f);
            dl.AddLine(pos + new Vector2(-r, 0f), pos + new Vector2(r, 0f), glint, 0.7f);
            dl.AddLine(pos + new Vector2(0f, -r), pos + new Vector2(0f, r), glint, 0.7f);
        }
    }

    // A teardrop (bright head, tail fading to nothing) as one fan.
    private static void DrawComet(ImDrawListPtr dl, Vector2 pos, Vector2 dir, float size, float streak, float alpha, in MaterialContext ctx)
    {
        Vector2 nrm = new(-dir.Y, dir.X);
        Vector2 mid = pos - dir * (streak * 0.5f);

        uint head = DrawHelpers.WithAlpha(IceColor.Core, alpha * 0.95f);
        uint side = DrawHelpers.WithAlpha(IceColor.Core, alpha * 0.80f);
        uint back = DrawHelpers.WithAlpha(IceColor.Cold, alpha * 0.30f);
        uint end  = DrawHelpers.WithAlpha(IceColor.Cold, 0f);

        Span<Vector2> rim = stackalloc Vector2[6]
        {
            pos - dir * streak,
            mid - nrm * (size * 0.8f),
            pos - nrm * size,
            pos + dir * size,
            pos + nrm * size,
            mid + nrm * (size * 0.8f),
        };
        Span<uint> rimCol = stackalloc uint[6] { end, back, side, head, side, back };
        MeshDraw.Fan(dl, MeshDraw.WhiteUv(ctx.Time), pos, head, rim, rimCol);
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Snow,
            DensityPer100px: 4f,
            SpeedMin: 18f, SpeedMax: 45f,
            LifespanMin: 0.8f, LifespanMax: 1.4f,
            SizeMin: 1f, SizeMax: 2.4f,
            SpreadRadians: 0.7f,
            BiasVelocity: new Vector2(0f, 12f),
            PrimaryDirection: new Vector2(0f, 1f),
            RenderMaterial: "particle.snow"),

        new(Role: PrimitiveRole.Snowflake,
            DensityPer100px: 0.6f,
            SpeedMin: 10f, SpeedMax: 25f,
            LifespanMin: 1.6f, LifespanMax: 2.8f,
            SizeMin: 5f, SizeMax: 9f,
            SpreadRadians: 0.9f,
            BiasVelocity: new Vector2(0f, 6f),
            PrimaryDirection: new Vector2(0f, 1f),
            RenderMaterial: "particle.snowflake"),
    };
}

/// <summary>Six-armed crystal: three branch pairs and a forked tip per arm over a faint hexagon; turns slowly with age.</summary>
public sealed class ParticleSnowflake : IParticleMaterial
{
    public string Name => "particle.snowflake";
    public string[] NaturalLanguageWords { get; } = { "snowflake", "snowflakes" };

    // Saturation just above 0.05, so a colour override tints it pale rather than treating it as grey and fully saturating it.
    private static readonly uint Frosted = DrawHelpers.Pack(0.94f, 0.98f, 1.00f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.5f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        float size = p.Size * DrawHelpers.LifeSize(p.AgeRatio, riseEnd: 0.20f, riseFrom: 0.40f, fallStart: 0.75f, floor: 0.10f);
        if (size <= 0.5f) return;

        float rot = (p.Seed & 0xFF) * 0.0246f + p.AgeRatio * 2.2f;
        Vector2 pos = p.DrawPos;

        dl.AddCircleFilled(pos, size * 0.95f, DrawHelpers.WithAlpha(IceColor.Cold, alpha * 0.08f));
        dl.AddCircleFilled(pos, size * 0.55f, DrawHelpers.WithAlpha(IceColor.Cold, alpha * 0.12f));

        // Inner hexagon: breaks up the six-spoke silhouette.
        float hexR = size * 0.55f;
        for (int i = 0; i < 6; i++)
        {
            float a1 = rot + i * (MathF.PI / 3f);
            float a2 = rot + (i + 1) * (MathF.PI / 3f);
            Vector2 v1 = pos + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * hexR;
            Vector2 v2 = pos + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * hexR;
            dl.AddLine(v1, v2, DrawHelpers.WithAlpha(IceColor.Cold, alpha * 0.18f), 0.7f);
        }

        for (int arm = 0; arm < 6; arm++)
        {
            float ang = rot + arm * (MathF.PI / 3f);
            Vector2 dir = new(MathF.Cos(ang), MathF.Sin(ang));
            Vector2 tip = pos + dir * size;

            dl.AddLine(pos, tip, DrawHelpers.WithAlpha(IceColor.Cold, alpha * 0.42f), 1.9f);
            dl.AddLine(pos, tip, DrawHelpers.WithAlpha(Frosted, alpha * 0.90f), 0.9f);

            for (int b = 0; b < 3; b++)
            {
                Vector2 at = Vector2.Lerp(pos, tip, 0.32f + 0.22f * b);
                float branchLen = size * (0.30f - 0.06f * b);

                for (int side = -1; side <= 1; side += 2)
                {
                    float bang = ang + side * 1.0f;
                    Vector2 btip = at + new Vector2(MathF.Cos(bang), MathF.Sin(bang)) * branchLen;
                    dl.AddLine(at, btip, DrawHelpers.WithAlpha(IceColor.Cold, alpha * 0.28f), 1.2f);
                    dl.AddLine(at, btip, DrawHelpers.WithAlpha(Frosted, alpha * 0.72f), 0.55f);
                }
            }

            // Forked tip: the detail that reads as snowflake rather than asterisk.
            Vector2 forkBase = pos + dir * (size * 0.92f);
            for (int side = -1; side <= 1; side += 2)
            {
                float fang = ang + side * 0.5f;
                Vector2 ftip = forkBase + new Vector2(MathF.Cos(fang), MathF.Sin(fang)) * (size * 0.14f);
                dl.AddLine(forkBase, ftip, DrawHelpers.WithAlpha(Frosted, alpha * 0.65f), 0.5f);
            }
        }

        dl.AddCircleFilled(pos, MathF.Max(0.8f, size * 0.10f), DrawHelpers.WithAlpha(IceColor.White, alpha * 0.95f));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Snowflake,
            DensityPer100px: 1.2f,
            SpeedMin: 12f, SpeedMax: 30f,
            LifespanMin: 1.4f, LifespanMax: 2.6f,
            SizeMin: 4f, SizeMax: 8f,
            SpreadRadians: 0.9f,
            BiasVelocity: new Vector2(0f, 8f),
            PrimaryDirection: new Vector2(0f, 1f)),
    };
}

/// <summary>Four-point sparkle. Brightness is the twinkle and scales size. Size is the long ray; Seed rotates it.</summary>
public sealed class ParticleGlint : IParticleMaterial
{
    public string Name => "particle.glint";
    public string[] NaturalLanguageWords { get; } = { "glint", "glints", "sparkle", "sparkles", "glitter" };

    private const int Rays = 8;   // alternating long / short: tip, notch, tip, notch ...

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.01f || p.Size <= 1f) return;

        float inten = Math.Min(1f, k);
        float R = p.Size * (0.30f + 0.70f * MathF.Sqrt(inten));
        float rot = DrawHelpers.Hash01(p.Seed + 1) * MathF.PI * 0.5f;
        float squash = 0.55f + 0.30f * DrawHelpers.Hash01(p.Seed + 2);
        Vector2 c = p.Position;

        uint centerCol = DrawHelpers.WithAlpha(IceColor.White, inten);
        uint notchCol  = DrawHelpers.WithAlpha(IceColor.Cold, inten * 0.55f);
        uint tipCol    = DrawHelpers.WithAlpha(IceColor.Cold, 0f);

        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);

        // Soft bloom behind the star, so a small glint reads as a flash rather than a scratch.
        Span<float> hr = stackalloc float[1] { R * 0.60f };
        Span<uint> hc = stackalloc uint[1] { DrawHelpers.WithAlpha(IceColor.Cold, 0f) };
        MeshDraw.Radial(dl, c, uv, DrawHelpers.WithAlpha(IceColor.Cold, inten * 0.50f), 10, hr, hc, rot, squashY: 1f, seed: p.Seed, irregular: 0f);

        Span<Vector2> rim = stackalloc Vector2[Rays * 2];
        Span<uint> rimCol = stackalloc uint[Rays * 2];
        for (int j = 0; j < Rays; j++)
        {
            float ang = rot + j * (MathF.Tau / Rays);
            float tipR = (j & 1) == 0 ? R : R * 0.34f * squash;
            float nAng = ang + MathF.Tau / Rays * 0.5f;
            rim[j * 2]     = c + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * tipR;
            rim[j * 2 + 1] = c + new Vector2(MathF.Cos(nAng), MathF.Sin(nAng)) * (R * 0.15f);
            rimCol[j * 2]     = tipCol;
            rimCol[j * 2 + 1] = notchCol;
        }
        MeshDraw.Fan(dl, uv, c, centerCol, rim, rimCol);

        dl.AddCircleFilled(c, MathF.Max(0.8f, R * 0.09f), DrawHelpers.WithAlpha(IceColor.White, inten));
    }
}

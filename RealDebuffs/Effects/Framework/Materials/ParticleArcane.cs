using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A glowing rune. Size is the glyph's half-height in px, Rotation turns it, and its letter comes from Seed (stable for life).
/// Variant bit 0 = inscribed: tighter, steadier light for a rune set into a ring; clear = a free-floating rune with more bloom.
/// Variant above 1 forces a letter: (Variant >> 1) - 1 picks the glyph.
/// </summary>
public sealed class ParticleRune : IParticleMaterial
{
    public string Name => "particle.rune";
    public string[] NaturalLanguageWords { get; } = { "rune", "runes", "glyph", "glyphs" };

    private const int MaxPts = 24;
    private static readonly MeshBuilder Mb = new();
    private static readonly Vector2[] Pts = new Vector2[MaxPts];
    private static readonly Vector2[] Nrm = new Vector2[MaxPts];
    private static readonly float[] Reach = new float[MaxPts];

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 2f) return;

        float size = p.Size * DrawHelpers.LifeSize(p.AgeRatio, riseEnd: 0.14f, riseFrom: 0.35f, fallStart: 0.82f, floor: 0.12f);
        if (size <= 2f) return;

        bool inscribed = (p.Variant & 1) != 0;
        int forced = p.Variant >> 1;
        int glyph = forced > 0 ? forced - 1 : RuneGlyphs.Pick(p.Seed);

        // A slow private flicker, so a ring of runes never pulses in unison.
        float seedPhase = DrawHelpers.Hash01(p.Seed + 4) * MathF.Tau;
        float flicker = inscribed ? 0.93f + 0.07f * MathF.Sin(ctx.Time * 1.9f + seedPhase)
                                  : 0.80f + 0.20f * MathF.Sin(ctx.Time * 2.6f + seedPhase);
        k *= flicker;

        float px = ctx.ScreenScale;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        Vector2 pos = p.DrawPos;
        bool overridden = DrawHelpers.ColorOverrideActive;

        // The pool of light the rune sits in.
        Span<float> rr = stackalloc float[2] { size * 0.75f, size * (inscribed ? 1.5f : 1.9f) };
        Span<uint> rc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.30f), k * (inscribed ? 0.10f : 0.17f)),
            DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.20f), 0f),
        };
        MeshDraw.Radial(dl, pos, uv, DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.34f), k * (inscribed ? 0.15f : 0.26f)),
                        12, rr, rc, 0f, 1f, 0, 0f);

        float cos = MathF.Cos(p.Rotation), sin = MathF.Sin(p.Rotation);
        var strokes = RuneGlyphs.Strokes(glyph);

        float bloom = MathF.Max(2.2f * px, size * (inscribed ? 0.16f : 0.26f));
        float bodyHalf = MathF.Max(0.65f * px, size * 0.050f);
        float coreHalf = MathF.Max(0.40f * px, size * 0.020f);

        uint bloomCol = DrawHelpers.Tint(CurseColor.Glow.Sample(0.30f), k * 0.30f, overridden);
        uint bodyCol  = DrawHelpers.Tint(CurseColor.Glow.Sample(0.70f), k * 0.95f, overridden);
        uint coreCol  = DrawHelpers.Tint(CurseColor.Glow.Sample(0.95f), k * 0.90f, overridden);

        // Three passes across every stroke (bloom, body, core) so one stroke's glow never sits on top of another's core.
        Mb.Clear();
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (var line in strokes)
            {
                int n = Math.Min(line.Length, MaxPts);
                if (n < 2) continue;

                for (int i = 0; i < n; i++)
                {
                    Vector2 g = line[i] * size;
                    Pts[i] = pos + new Vector2(g.X * cos - g.Y * sin, g.X * sin + g.Y * cos);
                }
                MeshBuilder.MiterNormals(Pts.AsSpan(0, n), Nrm.AsSpan(0, n));

                if (pass == 0)
                {
                    Array.Fill(Reach, bloom, 0, n);
                    Mb.Tent(Pts.AsSpan(0, n), Nrm.AsSpan(0, n), Reach.AsSpan(0, n), bloomCol);
                }
                else
                {
                    Array.Fill(Reach, pass == 1 ? bodyHalf : coreHalf, 0, n);
                    Mb.Ribbon(Pts.AsSpan(0, n), Nrm.AsSpan(0, n), Reach.AsSpan(0, n), pass == 1 ? bodyCol : coreCol, 0.8f * px);
                }
            }
        }
        Mb.Flush(dl, uv);
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    // As a stroke emitter: runes peeling off the line and floating away.
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Rune,
            DensityPer100px: 0.35f,
            SpeedMin: 6f, SpeedMax: 18f,
            LifespanMin: 2.0f, LifespanMax: 3.6f,
            SizeMin: 9f, SizeMax: 16f,
            SpreadRadians: 1.2f,
            BiasVelocity: new Vector2(0f, -12f),
            PrimaryDirection: new Vector2(0f, -1f)),
    };
}

/// <summary>A speck of arcane light: a soft halo round a bright pip, with a four-point flare on some. Large ones turn into out-of-focus discs, so whatever is nearest the camera reads as blurred.</summary>
public sealed class ParticleMote : IParticleMaterial
{
    public string Name => "particle.mote";
    public string[] NaturalLanguageWords { get; } = { "mote", "motes", "wisp", "wisps" };

    // Past this radius (at 1080p) a mote is drawn as a soft disc instead of a point of light.
    private const float BokehFrom = 11f;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.4f) return;

        float size = p.Size * DrawHelpers.LifeSize(p.AgeRatio, riseEnd: 0.20f, riseFrom: 0.25f, fallStart: 0.70f, floor: 0.06f);
        if (size <= 0.4f) return;

        float twinkle = 0.72f + 0.28f * MathF.Sin(ctx.Time * (2.2f + 3.1f * DrawHelpers.Hash01(p.Seed + 6)) + DrawHelpers.Hash01(p.Seed + 5) * MathF.Tau);
        k *= twinkle;

        Vector2 pos = p.DrawPos;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        float px = ctx.ScreenScale;

        if (size > BokehFrom * px)
        {
            // Out of focus: a dim disc with a slightly brighter rim.
            Span<float> rr = stackalloc float[3] { size * 0.80f, size * 0.93f, size };
            Span<uint> rc = stackalloc uint[3]
            {
                DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.34f), k * 0.17f),
                DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.50f), k * 0.26f),
                DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.40f), 0f),
            };
            MeshDraw.Radial(dl, pos, uv, DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.30f), k * 0.12f),
                            18, rr, rc, 0f, 1f, 0, 0f);
            return;
        }

        Span<float> hr = stackalloc float[2] { size * 1.4f, size * 3.2f };
        Span<uint> hc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.45f), k * 0.30f),
            DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.25f), 0f),
        };
        MeshDraw.Radial(dl, pos, uv, DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.60f), k * 0.55f), 10, hr, hc, 0f, 1f, 0, 0f);
        dl.AddCircleFilled(pos, MathF.Max(0.7f, size * 0.55f), DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.97f), k * 0.95f));

        // A flare on about one in eight.
        if ((p.Seed & 7) == 0)
        {
            float r = size * 4.2f;
            uint ray = DrawHelpers.WithAlpha(CurseColor.Glow.Sample(0.85f), k * 0.55f);
            dl.AddLine(pos + new Vector2(-r, 0f), pos + new Vector2(r, 0f), ray, 0.8f);
            dl.AddLine(pos + new Vector2(0f, -r), pos + new Vector2(0f, r), ray, 0.8f);
        }
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Mote,
            DensityPer100px: 0.6f,
            SpeedMin: 6f, SpeedMax: 24f,
            LifespanMin: 1.3f, LifespanMax: 2.6f,
            SizeMin: 1.6f, SizeMax: 3.6f,
            SpreadRadians: 1.5f,
            BiasVelocity: new Vector2(0f, -16f)),
    };
}

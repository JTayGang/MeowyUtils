using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A fine snow speck. Small bright white core with a soft pale-blue halo, drawn as a plain dot
/// most of the time - that's what fine snow should look like, and turning every speck into a
/// star would overwhelm the field. But roughly a quarter get a tiny four-point crystalline
/// glint cross, which reads as light catching an ice grain and gives the field visible variance
/// as it falls.
///
/// "frost" is also a word for it, so a status reading "white chains made of frost" gives chains that
/// shed cold white specks and flakes (the Frost effect's own keyword, like "flames" for Burns, is
/// skipped inside a "made of" phrase, so the phrase modifies Heavy rather than also freezing the screen).
///
/// A speck that is moving fast (wind-blown snow) is stretched into a soft comet along its velocity, so
/// a blizzard reads as streaks rather than a field of dots. The stretch begins above a speed no stroke
/// emitter reaches and grows continuously from nothing, so slow snow is drawn exactly as before.
///
/// When used as a stroke emitter ("frosty tentacles", "chains made of snow"), it declares TWO
/// emissions side by side: dense specks as the primary field, plus a sparse layer of crystalline
/// snowflakes rendered by particle.snowflake.
/// </summary>
public sealed class ParticleSnow : IParticleMaterial
{
    public string Name => "particle.snow";
    public string[] NaturalLanguageWords { get; } = { "snow", "frost", "hoarfrost", "rime" };

    private static readonly uint Halo = DrawHelpers.ToU32(0.72f, 0.86f, 1.00f, 1f);
    private static readonly uint Core = DrawHelpers.ToU32(0.98f, 1.00f, 1.00f, 1f);

    /// <summary>Speed (px/s at 1080p) below which a speck is a plain dot. Chain-shed snow tops out near 57.</summary>
    private const float StreakFromSpeed = 100f;

    /// <summary>Seconds of travel drawn behind a speck, for the speed beyond StreakFromSpeed.</summary>
    private const float StreakSeconds = 0.07f;

    /// <summary>A streak must be at least this many specks long before the comet replaces the dot (so there is no visible switch).</summary>
    private const float StreakMinSizes = 0.6f;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.3f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        float sizeT;
        if      (p.AgeRatio < 0.15f) sizeT = 0.30f + 0.70f * (p.AgeRatio / 0.15f);
        else if (p.AgeRatio < 0.80f) sizeT = 1f;
        else                          sizeT = MathF.Max(0.10f, 1f - (p.AgeRatio - 0.80f) / 0.20f);

        float size = p.Size * sizeT;
        if (size <= 0.3f) return;

        var pos = p.Position + new Vector2(p.Sway, 0f);

        float speed = p.Velocity.Length();
        float streak = MathF.Min(MathF.Max(0f, speed - StreakFromSpeed * ctx.ScreenScale) * StreakSeconds, size * 14f);
        if (streak > size * StreakMinSizes)
        {
            DrawComet(dl, pos, p.Velocity / speed, size, streak, alpha, ctx);
            return;
        }

        dl.AddCircleFilled(pos, size * 2.2f, DrawHelpers.WithAlpha(Halo, alpha * 0.22f));
        dl.AddCircleFilled(pos, size,        DrawHelpers.WithAlpha(Core, alpha * 0.92f));

        // ~25% of specks get a glint cross. Deterministic per particle (seed-based), so a given
        // speck keeps its glint for its whole life rather than flickering between the two.
        if ((p.Seed & 3) == 0)
        {
            float crossR = size * 2.6f;
            uint glint = DrawHelpers.WithAlpha(Core, alpha * 0.55f);
            dl.AddLine(pos + new Vector2(-crossR, 0f), pos + new Vector2(crossR, 0f), glint, 0.7f);
            dl.AddLine(pos + new Vector2(0f, -crossR), pos + new Vector2(0f, crossR), glint, 0.7f);
        }
    }

    /// <summary>
    /// A teardrop: a bright head and a tail that fades to nothing, as one fan (one native call, where
    /// the dot is two). Head and tail colors are the dot's core and halo.
    /// </summary>
    private static void DrawComet(ImDrawListPtr dl, Vector2 pos, Vector2 dir, float size, float streak, float alpha, in MaterialContext ctx)
    {
        Vector2 nrm = new(-dir.Y, dir.X);
        Vector2 tail = pos - dir * streak;
        Vector2 mid = pos - dir * (streak * 0.5f);

        uint head = DrawHelpers.WithAlpha(Core, alpha * 0.95f);
        uint side = DrawHelpers.WithAlpha(Core, alpha * 0.80f);
        uint back = DrawHelpers.WithAlpha(Halo, alpha * 0.30f);
        uint end  = DrawHelpers.WithAlpha(Halo, 0f);

        Span<Vector2> rim = stackalloc Vector2[6]
        {
            tail,
            mid - nrm * (size * 0.8f),
            pos - nrm * size,
            pos + dir * size,
            pos + nrm * size,
            mid + nrm * (size * 0.8f),
        };
        Span<uint> rimCol = stackalloc uint[6] { end, back, side, head, side, back };
        MeshDraw.Fan(dl, MeshDraw.WhiteUv(ctx.Time), pos, head, rim, rimCol);
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    private static readonly StrokeEmission[] EmissionSpecs =
    {
        // Specks.
        new(Role: PrimitiveRole.Snow,
            DensityPer100px: 4f,
            SpeedMin: 18f, SpeedMax: 45f,
            LifespanMin: 0.8f, LifespanMax: 1.4f,
            SizeMin: 1f, SizeMax: 2.4f,
            SpreadRadians: 0.7f,
            BiasVelocity: new Vector2(0f, 12f),
            PrimaryDirection: new Vector2(0f, 1f),
            RenderMaterial: "particle.snow"),

        // Flakes — sparse crystalline layer.
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
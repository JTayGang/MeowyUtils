using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Floating runes: the seal's own script (<see cref="VectorRunes"/>) drawn as drifting particles, as an
/// alternative to drifting text letters. Because a glyph here is a set of strokes rather than a font
/// character, it can do things text can't:
///   - it is INSCRIBED when it appears: a bright spark travels along the strokes, writing them, and the
///     finished rune flashes white-hot for a moment (a small echo of the seal's own lock flash);
///   - it tumbles slowly and sways as it drifts;
///   - its glow throbs in time with the seal's heartbeat, so the whole curse pulses as one;
///   - at the end of its life it is WIPED AWAY: a spark burns along the strokes from where it began.
///
/// The particles themselves (spawning, drifting, expiry) are still an <see cref="EdgeParticleField"/>;
/// this class only replaces how a particle is drawn, using the indexer that class provides for custom
/// shapes. The glyph rides along in the particle's string as a one-character token (see
/// <see cref="PickToken"/>) and every other per-particle random (tilt, spin, sway) is derived from the
/// particle's spawn time, so nothing in the shared particle code had to change.
/// </summary>
internal static class RuneParticles
{
    private const float WriteSeconds = 0.42f; // how long the inscribing takes
    private const float EraseSeconds = 0.55f; // how long the wipe at the end of a particle's life takes

    private static readonly string[] Tokens = MakeTokens();
    private static readonly float[] Lengths = MakeLengths(); // total stroke length of each glyph, in glyph units

    /// <summary>For EdgeParticleField's pickGlyph: a random rune, as a one-character token ('A' = first glyph, 'B' = second...).</summary>
    public static string PickToken(int seed) =>
        Tokens[Math.Min(Tokens.Length - 1, (int)(DrawHelpers.Hash01(seed) * Tokens.Length))];

    /// <summary>
    /// Draws every live particle in <paramref name="field"/> as a rune. Runes normally stand roughly upright
    /// with a random tilt; with <paramref name="radial"/> they instead start oriented away from
    /// <paramref name="radialCenter"/>, exactly like the runes on the seal's band, so ones that peel off
    /// the seal read as the band's own runes drifting away.
    /// </summary>
    public static void DrawField(
        ImDrawListPtr dl, EdgeParticleField field, float time, in MagicCircle.Palette pal, float alpha,
        bool radial = false, Vector2 radialCenter = default)
    {
        float beat = MagicCircle.Heartbeat(time);

        for (int i = 0; i < field.Count; i++)
        {
            ref readonly var p = ref field[i];
            float age = time - p.Born;
            if (age < 0f || age >= p.Lifespan) continue;

            // Everything random about this particle comes from its spawn time, so it's stable for its whole life.
            int h = BitConverter.SingleToInt32Bits(p.Born);

            float rot = radial
                ? MathF.Atan2(p.Pos.Y - radialCenter.Y, p.Pos.X - radialCenter.X) + MathF.PI * 0.5f // "up" = away from the seal
                : DrawHelpers.HashRange(h + 11, -0.45f, 0.45f);                                     // roughly upright, tilted

            float spin = DrawHelpers.HashRange(h + 12, 0.10f, 0.30f) * (DrawHelpers.Hash01(h + 13) < 0.5f ? -1f : 1f);
            rot += spin * age; // radians per second: a slow tumble

            float sway = MathF.Sin(age * 2.4f + DrawHelpers.HashRange(h + 14, 0f, MathF.PI * 2f)) * 5f;

            float write = Saturate(age / WriteSeconds);
            float erase = Saturate((age - (p.Lifespan - EraseSeconds)) / EraseSeconds);
            float bloom = age > WriteSeconds ? MathF.Exp(-(age - WriteSeconds) / 0.14f) : 0f; // 1 the instant it's fully written, then fades

            DrawOne(dl, new Vector2(p.Pos.X + sway, p.Pos.Y), p.Size, rot, GlyphIndex(p.Glyph), write, erase, bloom, beat, pal, alpha);
        }
    }

    /// <summary>
    /// Draws one rune. <paramref name="write"/> (0..1) is how far the inscribing spark has travelled along the
    /// glyph's strokes and <paramref name="erase"/> (0..1) is how far the wiping spark has followed it, so
    /// 1 and 0 mean "fully written, not yet erased". <paramref name="bloom"/> (0..1) briefly swells the glow
    /// as the rune completes. <paramref name="size"/> is the glyph's height in px and <paramref name="rot"/>
    /// rotates it (0 = upright, radians, clockwise on screen).
    /// </summary>
    public static void DrawOne(
        ImDrawListPtr dl, Vector2 center, float size, float rot, int glyph,
        float write, float erase, float bloom, float beat, in MagicCircle.Palette pal, float alpha)
    {
        if ((uint)glyph >= (uint)VectorRunes.Glyphs.Length || size <= 0f) return;

        float total = Lengths[glyph];
        float to = EaseOutCubic(write) * total;              // the inscribing spark
        float from = erase * erase * (3f - 2f * erase) * total; // the wiping spark (smoothstep)
        if (to - from <= 0.0001f) return;

        float fade = 1f - erase * erase; // what's left of a rune dims as it's wiped
        float coreW = Math.Clamp(size * 0.056f, 1.3f, 2.0f);

        // Same three-pass treatment as the runes on the seal's band: dark under-stroke, violet halo, colored core.
        // The halo is deliberately no wider than on the band's own runes (~4px): a fatter halo swamps the thin
        // pink core and the runes turn violet, which stops them looking like the seal's script. It swells with
        // the seal's heartbeat, and blooms as a rune finishes being written.
        uint ink = Tint(pal.Ink, 0.28f * fade, alpha);
        uint halo = Tint(pal.Halo, 0.24f * (1f + 0.8f * beat + 0.6f * bloom) * fade, alpha);
        uint core = Tint(pal.Main, (0.92f + 0.08f * beat) * fade, alpha);
        uint hot = Tint(pal.Core, 0.95f * bloom * fade, alpha); // the ignition: the finished rune flashes white-hot, then settles to pink
        float inkW = coreW + 2.0f, haloW = coreW + 3.4f + 1.4f * bloom;

        float sr = MathF.Sin(rot), cr = MathF.Cos(rot);
        var right = new Vector2(cr, sr);   // glyph x axis
        var up = new Vector2(sr, -cr);     // glyph +y axis ("up" for rot = 0)

        var strokes = VectorRunes.Glyphs[glyph];
        float acc = 0f; // distance travelled along the glyph so far
        Vector2 writeTip = default, eraseTip = default;
        bool haveWrite = false, haveErase = false;

        for (int s = 0; s < strokes.Length; s++)
        {
            var pts = strokes[s];
            float ax = pts[0], ay = pts[1];
            for (int k = 2; k < pts.Length; k += 2)
            {
                float bx = pts[k], by = pts[k + 1];
                float dx = bx - ax, dy = by - ay;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                float s0 = acc, s1 = acc + len;
                acc = s1;

                if (len > 1e-5f)
                {
                    // the slice of this segment that's currently visible: written by now, not yet wiped
                    float t0 = Saturate((from - s0) / len);
                    float t1 = Saturate((to - s0) / len);
                    if (t1 - t0 > 1e-4f)
                    {
                        Vector2 p0 = Map(center, right, up, size, ax + dx * t0, ay + dy * t0);
                        Vector2 p1 = Map(center, right, up, size, ax + dx * t1, ay + dy * t1);
                        dl.AddLine(p0, p1, ink, inkW);
                        dl.AddLine(p0, p1, halo, haloW);
                        dl.AddLine(p0, p1, core, coreW);
                        if (bloom > 0.04f) dl.AddLine(p0, p1, hot, coreW * 0.6f);
                    }

                    if (!haveWrite && to >= s0 && to <= s1)
                    {
                        float t = (to - s0) / len;
                        writeTip = Map(center, right, up, size, ax + dx * t, ay + dy * t);
                        haveWrite = true;
                    }
                    if (!haveErase && from >= s0 && from <= s1)
                    {
                        float t = (from - s0) / len;
                        eraseTip = Map(center, right, up, size, ax + dx * t, ay + dy * t);
                        haveErase = true;
                    }
                }

                ax = bx; ay = by;
            }
        }

        // The sparks: a hot core inside a soft three-step glow (a single flat disc reads as a lollipop).
        float sparkR = MathF.Max(1.4f, size * 0.055f);
        if (write < 1f && haveWrite)
            Spark(dl, writeTip, sparkR, 1f, pal, alpha);
        if (erase > 0f && erase < 1f && haveErase)
            Spark(dl, eraseTip, sparkR * 0.9f, 0.9f * fade, pal, alpha);
    }

    private static void Spark(ImDrawListPtr dl, Vector2 at, float r, float strength, in MagicCircle.Palette pal, float alpha)
    {
        dl.AddCircleFilled(at, r * 2.3f, Tint(pal.Halo, 0.14f * strength, alpha));
        dl.AddCircleFilled(at, r * 1.6f, Tint(pal.Main, 0.34f * strength, alpha));
        dl.AddCircleFilled(at, r, Tint(pal.Core, strength, alpha));
    }

    // ---------------------------------------------------------------------------------------------

    private static Vector2 Map(Vector2 center, Vector2 right, Vector2 up, float size, float x, float y) =>
        center + right * (x * size) + up * (y * size);

    private static uint Tint(uint color, float a, float alpha) => DrawHelpers.WithAlpha(color, a * alpha);

    private static int GlyphIndex(string? token)
    {
        if (string.IsNullOrEmpty(token)) return 0;
        int i = token[0] - 'A';
        return (uint)i < (uint)Lengths.Length ? i : 0;
    }

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static float EaseOutCubic(float t) { float u = 1f - Saturate(t); return 1f - u * u * u; }

    private static string[] MakeTokens()
    {
        var t = new string[VectorRunes.Glyphs.Length];
        for (int i = 0; i < t.Length; i++) t[i] = ((char)('A' + i)).ToString();
        return t;
    }

    private static float[] MakeLengths()
    {
        var glyphs = VectorRunes.Glyphs;
        var r = new float[glyphs.Length];
        for (int g = 0; g < glyphs.Length; g++)
        {
            float sum = 0f;
            foreach (var pts in glyphs[g])
                for (int k = 2; k < pts.Length; k += 2)
                {
                    float dx = pts[k] - pts[k - 2], dy = pts[k + 1] - pts[k - 1];
                    sum += MathF.Sqrt(dx * dx + dy * dy);
                }
            r[g] = sum;
        }
        return r;
    }
}

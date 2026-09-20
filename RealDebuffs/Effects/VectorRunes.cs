namespace RealDebuffs.Effects;

/// <summary>
/// A small alphabet of rune-like glyphs stored as line strokes instead of font characters.
///
/// Why strokes: ImGui can only draw text axis-aligned, so text can't follow a rotating ring. Strokes
/// are just points, so they rotate and scale for free - and, like the plain-ASCII particles in
/// SilenceEffect, they can never show up as missing-glyph tofu boxes whatever fonts Dalamud has loaded.
///
/// Each glyph is a list of polylines in a unit box: x runs -0.3..0.3 across the glyph, y runs
/// -0.5..0.5 with +y meaning "up" (which becomes "outward" when a glyph is set on a ring).
/// The shapes are straight-line, Elder-Futhark-flavored - deliberately angular so they read as
/// carved script rather than handwriting. Add or tweak glyphs freely; nothing else depends on the count.
/// </summary>
internal static class VectorRunes
{
    private static float[] S(params float[] xy) => xy;
    private static float[][] G(params float[][] strokes) => strokes;

    public static readonly float[][][] Glyphs =
    {
        G(S(0, -.5f, 0, .5f), S(0, -.05f, .3f, .25f), S(0, .2f, .3f, .5f)),                       // branching stem
        G(S(-.24f, -.5f, -.24f, .5f, .24f, .2f, .24f, -.5f)),                                       // gate
        G(S(-.2f, -.5f, -.2f, .5f), S(-.2f, .28f, .24f, 0, -.2f, -.28f)),                           // stem + wedge
        G(S(-.12f, -.5f, -.12f, .5f), S(-.12f, .5f, .26f, .24f), S(-.12f, .16f, .26f, -.1f)),        // stem + two flags
        G(S(-.2f, -.5f, -.2f, .5f, .22f, .24f, -.2f, 0, .24f, -.5f)),                               // flagged leg
        G(S(.24f, .42f, -.22f, 0, .24f, -.42f)),                                                    // chevron
        G(S(-.26f, .42f, .26f, -.42f), S(-.26f, -.42f, .26f, .42f)),                                // cross
        G(S(-.2f, -.5f, -.2f, .5f, .22f, .26f, -.2f, 0)),                                           // pennant
        G(S(-.22f, -.5f, -.22f, .5f), S(.22f, -.5f, .22f, .5f), S(-.22f, .18f, .22f, -.18f)),        // ladder
        G(S(0, -.5f, 0, .5f), S(-.26f, .16f, .26f, -.16f)),                                         // slashed stem
        G(S(0, -.5f, 0, .5f), S(-.22f, .2f, 0, .5f, .22f, .2f)),                                    // arrow
        G(S(0, -.5f, 0, .5f), S(0, .5f, .26f, .26f), S(0, -.5f, -.26f, -.26f)),                     // hooked stem
        G(S(-.2f, -.5f, -.2f, .5f), S(-.2f, .5f, .24f, .25f, -.2f, 0, .24f, -.25f, -.2f, -.5f)),     // double lobe
        G(S(0, -.5f, 0, .5f), S(-.28f, .46f, 0, .06f, .28f, .46f)),                                 // trident
        G(S(.2f, .5f, -.2f, .1f, .2f, -.1f, -.2f, -.5f)),                                           // bolt
        G(S(0, .46f, .26f, 0, 0, -.46f, -.26f, 0, 0, .46f)),                                        // diamond
        G(S(-.28f, .42f, -.28f, -.42f, .28f, .42f, .28f, -.42f, -.28f, .42f)),                       // bowtie
        G(S(0, .5f, .25f, .2f, 0, -.1f, -.25f, .2f, 0, .5f), S(-.26f, -.5f, 0, -.1f, .26f, -.5f)),   // diamond on legs
        G(S(-.24f, -.5f, -.24f, .5f, 0, .14f, .24f, .5f, .24f, -.5f)),                              // crown
        G(S(-.1f, -.5f, -.1f, .5f, .24f, .2f)),                                                     // flag
        G(S(-.04f, .5f, -.26f, .18f, -.04f, -.08f), S(.04f, .08f, .26f, -.18f, .04f, -.5f)),         // twin hooks
    };
}

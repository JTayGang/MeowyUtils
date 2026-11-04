using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>Stroke helpers shared by the effects: ring strokes and the hero binding ring.</summary>
internal static class SceneStrokes
{
    /// <summary>A closed ring-style stroke that draws on as reveal rises; salt varies seed and phase per ring.</summary>
    public static void Ring(this EffectScene scene, StrandPath path, PrimitiveRole role, int seed, int salt, float width, float brightness,
                            float depth, float excite, float reveal, float dash, Vector4? color, float agitGain = 0.7f)
    {
        if (brightness <= 0.003f || reveal <= 0.001f) return;
        scene.AddStroke(new StrokePrimitive
        {
            Path = path, Role = role, Reveal = reveal, WidthHint = width, Brightness = brightness,
            Seed = seed + salt * 101, Phase = salt * 1.7f, Depth = depth, Agitation = DrawHelpers.Saturate(excite * agitGain),
            TipFlare = reveal < 0.999f ? 1f : 0f, Closed = true, Dash = dash, ColorOverride = color,
        });
    }

    /// <summary>Half the link count a chain gets round a hero ring, so width follows length and links never re-space.</summary>
    private const int HeroHalfLinks = 90;

    /// <summary>The hero binding ring: a glow halo under the main stroke, so any swapped material keeps the light.</summary>
    public static void HeroRing(this EffectScene scene, StrandPath path, int seed, float reveal, float brightness, float haloGain,
                                float depth, float mainDepth, float agitation, float tipFlare, Vector4? color, float haloScale = 2.4f)
    {
        float width = path.Length / (1.2f * HeroHalfLinks);
        scene.AddStroke(new StrokePrimitive
        {
            Path = path, Role = PrimitiveRole.Halo, Reveal = reveal, WidthHint = width * haloScale, Brightness = brightness * haloGain,
            Seed = seed + 21, Phase = 2f, Depth = depth, Agitation = agitation, Closed = true, ColorOverride = color,
        });
        scene.AddStroke(new StrokePrimitive
        {
            Path = path, Role = PrimitiveRole.MainStroke, Reveal = reveal, WidthHint = width, Brightness = brightness,
            Seed = seed + 22, Phase = 0.7f, Depth = mainDepth, Agitation = agitation, TipFlare = tipFlare, Closed = true, ColorOverride = color,
        });
    }
}

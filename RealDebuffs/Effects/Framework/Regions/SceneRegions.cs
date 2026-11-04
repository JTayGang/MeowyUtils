using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>Region helpers shared by the effects: washes, edge light, pools, murk.</summary>
internal static class SceneRegions
{
    /// <summary>Full-screen wash.</summary>
    public static void Wash(this EffectScene scene, Vector2 size, uint tint, float alpha, Vector4? color)
    {
        if (alpha <= 0.002f) return;
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = tint, Alpha = alpha, ColorOverride = color });
    }

    /// <summary>Light spilling in from all four screen edges.</summary>
    public static void EdgeSpill(this EffectScene scene, Vector2 size, float depth, uint tint, float alpha, Vector4? color)
    {
        if (alpha <= 0.002f) return;
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new Vector2(size.X, depth), Tint = tint, Alpha = alpha, Top = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(0f, size.Y - depth), Max = size, Tint = tint, Alpha = alpha, Bottom = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new Vector2(depth, size.Y), Tint = tint, Alpha = alpha, Left = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(size.X - depth, 0f), Max = size, Tint = tint, Alpha = alpha, Right = true, ColorOverride = color });
    }

    /// <summary>A band of light or gloom pooled along the bottom edge.</summary>
    public static void Pool(this EffectScene scene, Vector2 size, float depth, uint tint, float alpha, Vector4? color)
    {
        if (alpha <= 0.002f || depth <= 1f) return;
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(0f, size.Y - depth), Max = size, Tint = tint, Alpha = alpha, Bottom = true, ColorOverride = color });
    }

    /// <summary>The dim mood under a heavy effect: an edge vignette plus gloom pooled along the bottom, swelling slowly.</summary>
    public static void Murk(this EffectScene scene, in CastFrame f, in MurkStyle m)
    {
        float ramp = DrawHelpers.Saturate(f.Age / m.RampSeconds);
        scene.RequestVignette(m.Vignette, 0.12f, f.Alpha * m.VignetteAlpha * ramp, priority: 20, f.Color);
        float depth = f.Size.Y * m.PoolDepth * (1f + m.Swell * DrawHelpers.Pulse(f.Time, m.SwellSeconds)) * ramp;
        scene.Pool(f.Size, depth, m.Pool, m.PoolAlpha * f.Alpha, f.Color);
    }
}

/// <summary>Tints and timing for SceneRegions.Murk (alphas and depth are fractions; the pool depth is of screen height).</summary>
internal readonly record struct MurkStyle(uint Vignette, float VignetteAlpha, uint Pool, float PoolAlpha, float PoolDepth,
                                          float RampSeconds, float Swell, float SwellSeconds);

namespace RealDebuffs.Effects.Framework;

/// Region helpers shared by the effects: washes, edge light, pools, murk.
internal static class SceneRegions
{
    /// Full-screen wash.
    public static void Wash(this EffectScene scene, Vector2 size, uint tint, float alpha, Vector4? color)
    {
        if (alpha <= 0.002f) return;
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = tint, Alpha = alpha, ColorOverride = color });
    }

    /// Light spilling in from all four screen edges.
    public static void EdgeSpill(this EffectScene scene, Vector2 size, float depth, uint tint, float alpha, Vector4? color)
    {
        if (alpha <= 0.002f) return;
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new(size.X, depth), Tint = tint, Alpha = alpha, Top = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = new(0f, size.Y - depth), Max = size, Tint = tint, Alpha = alpha, Bottom = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new(depth, size.Y), Tint = tint, Alpha = alpha, Left = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = new(size.X - depth, 0f), Max = size, Tint = tint, Alpha = alpha, Right = true, ColorOverride = color });
    }

    /// A band of light or gloom pooled along the bottom edge.
    public static void Pool(this EffectScene scene, Vector2 size, float depth, uint tint, float alpha, Vector4? color)
    {
        if (alpha <= 0.002f || depth <= 1f) return;
        scene.AddRegion(new RegionPrimitive { Min = new(0f, size.Y - depth), Max = size, Tint = tint, Alpha = alpha, Bottom = true, ColorOverride = color });
    }

    /// The dim mood under a heavy effect: an edge vignette plus gloom pooled along the bottom, swelling slowly.
    public static void Murk(this EffectScene scene, in CastFrame f, in MurkStyle m)
    {
        float ramp = Saturate(f.Age / m.RampSeconds);
        scene.RequestVignette(m.Vignette, 0.12f, f.Alpha * m.VignetteAlpha * ramp, priority: 20, f.Color);
        float depth = f.Size.Y * m.PoolDepth * (1f + m.Swell * Pulse(f.Time, m.SwellSeconds)) * ramp;
        scene.Pool(f.Size, depth, m.Pool, m.PoolAlpha * f.Alpha, f.Color);
    }
}

/// Tints and timing for SceneRegions.Murk (alphas and depth are fractions; the pool depth is of screen height).
internal readonly record struct MurkStyle(uint Vignette, float VignetteAlpha, uint Pool, float PoolAlpha, float PoolDepth,
                                          float RampSeconds, float Swell, float SwellSeconds);

using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Renders one frame's scene. Fixed order: vignette → regions → strokes → particles. Every
/// primitive's ColorOverride is pushed around its material call. Per-effect material overrides
/// (from the Effect Styles settings panel) are looked up by the primitive's stamped Owner + its
/// type + its role; when no override exists, the built-in default for that role is used.
/// </summary>
public static class EffectSceneRenderer
{
    public static void Render(ImDrawListPtr dl, EffectScene scene, Vector2 screenSize,
                              float time, float globalAlpha,
                              IReadOnlyDictionary<string, string>? materialOverrides)
    {
        float px = screenSize.X < screenSize.Y ? screenSize.X / 1080f : screenSize.Y / 1080f;
        if (px < 0.75f) px = 0.75f;
        if (px > 2.4f)  px = 2.4f;

        var ctx = new MaterialContext(time, px, globalAlpha, (int)screenSize.X, (int)screenSize.Y);

        // 1. Shared vignette.
        if (scene.Vignette.Active && scene.Vignette.Alpha > 0.001f)
        {
            DrawHelpers.PushColorOverride(scene.Vignette.ColorOverride);
            try
            {
                DrawHelpers.DrawVignette(dl, screenSize,
                    scene.Vignette.Tint, scene.Vignette.ThicknessFrac,
                    globalAlpha * scene.Vignette.Alpha);
            }
            finally { DrawHelpers.PopColorOverride(); }
        }

        // 2. Regions.
        foreach (var r in scene.Regions)
        {
            DrawHelpers.PushColorOverride(r.ColorOverride);
            try { MaterialRegistry.GetRegion(ResolveRegion(r, materialOverrides)).Draw(dl, in r, in ctx); }
            finally { DrawHelpers.PopColorOverride(); }
        }

        // 3. Strokes.
        foreach (var s in scene.Strokes)
        {
            DrawHelpers.PushColorOverride(s.ColorOverride);
            try { MaterialRegistry.GetStroke(ResolveStroke(s, materialOverrides)).Draw(dl, in s, in ctx); }
            finally { DrawHelpers.PopColorOverride(); }
        }

        // 4. Particles.
        foreach (var p in scene.Particles)
        {
            DrawHelpers.PushColorOverride(p.ColorOverride);
            try { MaterialRegistry.GetParticle(ResolveParticle(p, materialOverrides)).Draw(dl, in p, in ctx); }
            finally { DrawHelpers.PopColorOverride(); }
        }
    }

    private static string ResolveStroke(in StrokePrimitive s, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides != null &&
            overrides.TryGetValue(MaterialOverrideKey.For(s.Owner, "Stroke", s.Role), out var name))
            return name;
        return DefaultStroke(s.Role);
    }

    private static string ResolveParticle(in ParticlePrimitive p, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides != null &&
            overrides.TryGetValue(MaterialOverrideKey.For(p.Owner, "Particle", p.Role), out var name))
            return name;
        return DefaultParticle(p.Role);
    }

    private static string ResolveRegion(in RegionPrimitive r, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides != null &&
            overrides.TryGetValue(MaterialOverrideKey.For(r.Owner, "Region", r.Role), out var name))
            return name;
        return DefaultRegion(in r);
    }

    private static string DefaultStroke(PrimitiveRole r) => r switch
    {
        PrimitiveRole.BranchStroke => "stroke.simple",
        PrimitiveRole.DetailStroke => "stroke.simple",
        _                          => "stroke.lightning",
    };

    private static string DefaultParticle(PrimitiveRole r) => r switch
    {
        PrimitiveRole.Drip      => "particle.drip",
        PrimitiveRole.Flow      => "particle.drip",
        PrimitiveRole.Mote      => "particle.mote",
        PrimitiveRole.Ember     => "particle.ember",
        PrimitiveRole.Rune      => "particle.rune",
        PrimitiveRole.Word      => "particle.word",
        PrimitiveRole.Ring      => "particle.ring",
        PrimitiveRole.Flare     => "particle.flare",
        PrimitiveRole.Snowflake => "particle.snowflake",
        PrimitiveRole.Snow      => "particle.snow",
        PrimitiveRole.Fog       => "particle.fog",
        _                       => "particle.spark",
    };

    /// <summary>Any edge-mask flag set → edge glow. No edge-mask flag → flat fill.</summary>
    private static string DefaultRegion(in RegionPrimitive r) =>
        (r.Top || r.Bottom || r.Left || r.Right) ? "region.edge-glow" : "region.flat-fill";
}

/// <summary>
/// Shared key format for material overrides. Effects never build these keys; the settings panel
/// and the renderer are the only places that do, and they must agree, so the format lives here.
/// </summary>
public static class MaterialOverrideKey
{
    public static string For(DebuffKind kind, string type, PrimitiveRole role) =>
        $"{kind}.{type}.{role}";
}
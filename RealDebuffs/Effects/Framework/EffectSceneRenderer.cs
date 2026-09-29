using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Renders one frame's scene. Fixed order: vignette → regions → strokes → particles. Every
/// primitive's ColorOverride is pushed around its material call.
///
/// Material resolution order for each primitive:
///  1. User override (Settings → Effect generator, or a tooltip "made of X" phrase).
///  2. The effect's declared default (EffectRegistry).
///  3. A universal fallback (a plain stroke, a spark, a flat fill).
/// Step 3 is a safety net so an unregistered material name never crashes the plugin.
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
            try
            {
                var mat = MaterialRegistry.TryGetRegion(ResolveRegion(in r, materialOverrides))
                          ?? MaterialRegistry.TryGetRegion(EffectRegistry.FallbackRegion(in r))
                          ?? MaterialRegistry.TryGetRegion("region.flat-fill");
                mat?.Draw(dl, in r, in ctx);
            }
            finally { DrawHelpers.PopColorOverride(); }
        }

        // 3. Strokes.
        foreach (var s in scene.Strokes)
        {
            DrawHelpers.PushColorOverride(s.ColorOverride);
            try
            {
                var mat = MaterialRegistry.TryGetStroke(MaterialOverrideKey.ResolveStroke(in s, materialOverrides))
                          ?? MaterialRegistry.TryGetStroke(EffectRegistry.FallbackStroke())
                          ?? MaterialRegistry.TryGetStroke("stroke.simple");
                mat?.Draw(dl, in s, in ctx);
            }
            finally { DrawHelpers.PopColorOverride(); }
        }

        // 4. Particles.
        foreach (var p in scene.Particles)
        {
            DrawHelpers.PushColorOverride(p.ColorOverride);
            try
            {
                var mat = MaterialRegistry.TryGetParticle(ResolveParticle(in p, materialOverrides))
                          ?? MaterialRegistry.TryGetParticle(EffectRegistry.FallbackParticle(p.Role))
                          ?? MaterialRegistry.TryGetParticle("particle.spark");
                mat?.Draw(dl, in p, in ctx);
            }
            finally { DrawHelpers.PopColorOverride(); }
        }
    }

    private static string ResolveParticle(in ParticlePrimitive p, IReadOnlyDictionary<string, string>? overrides)
    {
        // A particle can carry its material with it directly (StrokeAutoEmitter sets this when a
        // user override asked for a specific emitter material).
        if (p.MaterialName is { } forced) return forced;

        if (overrides != null &&
            overrides.TryGetValue(MaterialOverrideKey.For(p.Owner, "Particle", p.Role), out var name))
            return name;

        return EffectRegistry.DefaultFor(p.Owner, "Particle", p.Role.ToString())
            ?? EffectRegistry.FallbackParticle(p.Role);
    }

    private static string ResolveRegion(in RegionPrimitive r, IReadOnlyDictionary<string, string>? overrides)
    {
        // Regions don't have a meaningful Role beyond "which kind of region is this", so the
        // override key uses a coarse tag derived from the edge mask instead of Role.ToString().
        string regionKind = r.HasEdge ? "EdgeGlow" : "FlatFill";

        if (overrides != null &&
            overrides.TryGetValue(MaterialOverrideKey.ForRegion(r.Owner, regionKind), out var name))
            return name;

        return EffectRegistry.DefaultFor(r.Owner, "Region", regionKind)
            ?? EffectRegistry.FallbackRegion(in r);
    }
}

/// <summary>
/// Shared key format for material overrides.
///  - Material axis: (Kind, "Stroke"|"Particle"|"Region", Role-or-tag) -> material name.
///  - Emit axis: (Kind, "Stroke", Role) + ".Emit" -> particle material name (ambient particles
///    spawned along that stroke).
/// Settings panel and renderer/emitter are the only places that build these.
/// </summary>
public static class MaterialOverrideKey
{
    public static string For(DebuffKind kind, string type, PrimitiveRole role) =>
        $"{kind}.{type}.{role}";

    public static string ForRegion(DebuffKind kind, string regionKind) =>
        $"{kind}.Region.{regionKind}";

    public static string ForRegion(DebuffKind kind, in RegionPrimitive r) =>
        ForRegion(kind, r.HasEdge ? "EdgeGlow" : "FlatFill");

    /// <summary>Emit axis: which particle material this stroke sheds along its length.</summary>
    public static string ForStrokeEmit(DebuffKind kind, PrimitiveRole role) =>
        $"{kind}.Stroke.{role}.Emit";

    /// <summary>
    /// Which stroke material should render this stroke, considering overrides first. Shared by
    /// the renderer and StrokeAutoEmitter so the two never disagree about which material is
    /// actually on screen. Public specifically so StrokeAutoEmitter (a different file) can call it.
    /// </summary>
    public static string ResolveStroke(in StrokePrimitive s, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides != null &&
            overrides.TryGetValue(For(s.Owner, "Stroke", s.Role), out var name))
            return name;

        return EffectRegistry.DefaultFor(s.Owner, "Stroke", s.Role.ToString())
            ?? EffectRegistry.FallbackStroke();
    }
}
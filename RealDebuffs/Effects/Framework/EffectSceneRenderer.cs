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
    // Resolved materials per (owner, role). Resolving builds override-key strings, which is far too
    // much garbage to do per primitive per frame, so it's done once per key and remembered. The
    // cache is valid for one override dictionary instance; EffectManager publishes a new instance
    // whenever the overrides change, which clears it.
    private static IReadOnlyDictionary<string, string>? _cachedFor;
    private static readonly Dictionary<(DebuffKind, PrimitiveRole), IStrokeMaterial?> Strokes = new();
    private static readonly Dictionary<(DebuffKind, PrimitiveRole), IParticleMaterial?> Particles = new();
    private static readonly Dictionary<(DebuffKind, bool), IRegionMaterial?> Regions = new();

    public static void Render(ImDrawListPtr dl, EffectScene scene, Vector2 screenSize,
                              float time, float globalAlpha,
                              IReadOnlyDictionary<string, string>? materialOverrides)
    {
        if (!ReferenceEquals(materialOverrides, _cachedFor))
        {
            Strokes.Clear();
            Particles.Clear();
            Regions.Clear();
            _cachedFor = materialOverrides;
        }

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
            try { RegionFor(in r, materialOverrides)?.Draw(dl, in r, in ctx); }
            finally { DrawHelpers.PopColorOverride(); }
        }

        // 3. Strokes.
        foreach (var s in scene.Strokes)
        {
            DrawHelpers.PushColorOverride(s.ColorOverride);
            try { StrokeFor(in s, materialOverrides)?.Draw(dl, in s, in ctx); }
            finally { DrawHelpers.PopColorOverride(); }
        }

        // 4. Particles.
        foreach (var p in scene.Particles)
        {
            DrawHelpers.PushColorOverride(p.ColorOverride);
            try { ParticleFor(in p, materialOverrides)?.Draw(dl, in p, in ctx); }
            finally { DrawHelpers.PopColorOverride(); }
        }
    }

    private static IRegionMaterial? RegionFor(in RegionPrimitive r, IReadOnlyDictionary<string, string>? overrides)
    {
        var key = (r.Owner, r.HasEdge);
        if (!Regions.TryGetValue(key, out var mat))
        {
            mat = MaterialRegistry.TryGetRegion(ResolveRegion(in r, overrides))
                  ?? MaterialRegistry.TryGetRegion(EffectRegistry.FallbackRegion(in r))
                  ?? MaterialRegistry.TryGetRegion("region.flat-fill");
            Regions[key] = mat;
        }
        return mat;
    }

    private static IStrokeMaterial? StrokeFor(in StrokePrimitive s, IReadOnlyDictionary<string, string>? overrides)
    {
        var key = (s.Owner, s.Role);
        if (!Strokes.TryGetValue(key, out var mat))
        {
            mat = MaterialRegistry.TryGetStroke(MaterialOverrideKey.ResolveStroke(in s, overrides))
                  ?? MaterialRegistry.TryGetStroke(EffectRegistry.FallbackStroke())
                  ?? MaterialRegistry.TryGetStroke("stroke.simple");
            Strokes[key] = mat;
        }
        return mat;
    }

    private static IParticleMaterial? ParticleFor(in ParticlePrimitive p, IReadOnlyDictionary<string, string>? overrides)
    {
        // A particle can carry its material with it directly (StrokeAutoEmitter sets this when a
        // user override asked for a specific emitter material).
        if (p.MaterialName is { } forced) return FindParticle(forced, p.Role);

        var key = (p.Owner, p.Role);
        if (!Particles.TryGetValue(key, out var mat))
        {
            string name = overrides != null &&
                          overrides.TryGetValue(MaterialOverrideKey.For(p.Owner, "Particle", p.Role), out var overridden)
                ? overridden
                : EffectRegistry.DefaultFor(p.Owner, "Particle", MaterialOverrideKey.RoleName(p.Role))
                  ?? EffectRegistry.FallbackParticle(p.Role);
            mat = FindParticle(name, p.Role);
            Particles[key] = mat;
        }
        return mat;
    }

    private static IParticleMaterial? FindParticle(string name, PrimitiveRole role) =>
        MaterialRegistry.TryGetParticle(name)
        ?? MaterialRegistry.TryGetParticle(EffectRegistry.FallbackParticle(role))
        ?? MaterialRegistry.TryGetParticle("particle.spark");

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

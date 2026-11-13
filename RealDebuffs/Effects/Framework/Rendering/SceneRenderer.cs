using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>Draws a scene back to front: vignette, regions, strokes (each followed by the particles clinging to it), then free particles. A vignette that asks to be Over goes after the regions; BelowVeil particles go between the plain regions and the Veil ones.</summary>
public sealed class SceneRenderer
{
    // Per particle: the stroke it is drawn after (-1 = free, -2 = already drawn below the veil) and its resolved material.
    private int[] _host = new int[2048];
    private IParticleMaterial?[] _material = new IParticleMaterial?[2048];

    public void Render(ImDrawListPtr dl, EffectScene scene, Vector2 screenSize, float time, float globalAlpha)
    {
        var ctx = new MaterialContext(time, DrawHelpers.PixelScale(screenSize), globalAlpha, (int)screenSize.X, (int)screenSize.Y);

        if (!scene.Vignette.Over) DrawVignette(dl, scene, screenSize, globalAlpha);
        DrawRegions(dl, scene, in ctx, veil: false);
        DrawBelowVeil(dl, scene, in ctx);
        DrawRegions(dl, scene, in ctx, veil: true);
        if (scene.Vignette.Over) DrawVignette(dl, scene, screenSize, globalAlpha);
        DrawStrokesAndParticles(dl, scene, in ctx);
    }

    private static void DrawVignette(ImDrawListPtr dl, EffectScene scene, Vector2 screenSize, float globalAlpha)
    {
        ref readonly var v = ref scene.Vignette;
        if (!v.Active || v.Alpha <= 0.001f) return;

        DrawHelpers.PushColorOverride(v.ColorOverride);
        try { DrawHelpers.DrawVignette(dl, screenSize, v.Tint, v.ThicknessFrac, globalAlpha * v.Alpha); }
        finally { DrawHelpers.PopColorOverride(); }
    }

    private static void DrawRegions(ImDrawListPtr dl, EffectScene scene, in MaterialContext ctx, bool veil)
    {
        foreach (var r in scene.Regions)
        {
            if (r.Veil != veil) continue;
            DrawHelpers.PushColorOverride(r.ColorOverride);
            try { scene.Materials.Region(r.Owner, r.Kind)?.Draw(dl, in r, in ctx); }
            finally { DrawHelpers.PopColorOverride(); }
        }
    }

    private static void DrawBelowVeil(ImDrawListPtr dl, EffectScene scene, in MaterialContext ctx)
    {
        foreach (ref readonly var p in CollectionsMarshal.AsSpan(scene.Particles))
            if (p.BelowVeil) DrawParticle(dl, in p, scene.Materials.Particle(in p), in ctx);
    }

    private void DrawStrokesAndParticles(ImDrawListPtr dl, EffectScene scene, in MaterialContext ctx)
    {
        int pc = scene.Particles.Count;
        if (_host.Length < pc)
        {
            int n = Math.Max(pc, _host.Length * 2);
            _host = new int[n];
            _material = new IParticleMaterial?[n];
        }

        bool anyHosted = false;
        for (int j = 0; j < pc; j++)
        {
            var p = scene.Particles[j];
            var mat = scene.Materials.Particle(in p);
            _material[j] = mat;
            _host[j] = p.BelowVeil ? -2 : -1;
            if (p.BelowVeil || !p.HasHost || mat is null || !mat.DrawsWithHost) continue;

            for (int i = scene.Strokes.Count - 1; i >= 0; i--)
                if (scene.Strokes[i].Owner == p.Owner && scene.Strokes[i].Seed == p.HostSeed) { _host[j] = i; anyHosted = true; break; }
        }

        for (int i = 0; i < scene.Strokes.Count; i++)
        {
            var s = scene.Strokes[i];
            DrawHelpers.PushColorOverride(s.ColorOverride);
            try { scene.Materials.Stroke(s.Owner, s.Role).Material?.Draw(dl, in s, in ctx); }
            finally { DrawHelpers.PopColorOverride(); }

            if (!anyHosted) continue;
            for (int j = 0; j < pc; j++)
                if (_host[j] == i) DrawParticle(dl, scene.Particles[j], _material[j], in ctx);
        }

        for (int j = 0; j < pc; j++)
            if (_host[j] == -1) DrawParticle(dl, scene.Particles[j], _material[j], in ctx);
    }

    private static void DrawParticle(ImDrawListPtr dl, in ParticlePrimitive p, IParticleMaterial? mat, in MaterialContext ctx)
    {
        DrawHelpers.PushColorOverride(p.ColorOverride);
        try { mat?.Draw(dl, in p, in ctx); }
        finally { DrawHelpers.PopColorOverride(); }
    }
}

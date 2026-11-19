using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>Draws a scene back to front: vignette, regions, strokes (each followed by the particles clinging to it), then free particles. A vignette that asks to be Over goes after the regions; BelowVeil particles go between the plain regions and the Veil ones.</summary>
public sealed class SceneRenderer
{
    // Per particle: the stroke it is drawn after (-1 = free, -2 = already drawn below the veil) and its resolved material.
    private int[] _host = new int[2048];
    private int[] _coatHost = new int[16];
    private IParticleMaterial?[] _material = new IParticleMaterial?[2048];

    public void Render(ImDrawListPtr dl, EffectScene scene, Vector2 screenSize, float time, float globalAlpha)
    {
        var ctx = new MaterialContext(time, DrawHelpers.PixelScale(screenSize), globalAlpha, (int)screenSize.X, (int)screenSize.Y);
        MeshDraw.AbortBatch();

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
            IRegionMaterial? mat = null;
            DrawHelpers.PushColorOverride(r.ColorOverride);
            try
            {
                mat = scene.Materials.Region(r.Owner, r.Kind);
                if (DrawGuard.Allowed(mat, r.Owner)) { DrawGuard.Probe?.Invoke(mat!); mat!.Draw(dl, in r, in ctx); DrawGuard.Ok(mat, r.Owner); }
            }
            catch (Exception e) { if (mat != null) DrawGuard.Fail(mat, r.Owner, e); }
            finally { DrawHelpers.PopColorOverride(); }
        }
    }

    private static void DrawBelowVeil(ImDrawListPtr dl, EffectScene scene, in MaterialContext ctx)
    {
        foreach (ref readonly var p in CollectionsMarshal.AsSpan(scene.Particles))
            if (p.BelowVeil) DrawParticle(dl, in p, scene.Materials.Particle(in p), in ctx);
        MeshDraw.EndBatch(dl);
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

        int cc = scene.Coats.Count;
        if (_coatHost.Length < cc) _coatHost = new int[Math.Max(cc, _coatHost.Length * 2)];
        for (int c = 0; c < cc; c++)
        {
            var co = scene.Coats[c];
            _coatHost[c] = -1;
            for (int i = scene.Strokes.Count - 1; i >= 0; i--)
                if (scene.Strokes[i].Owner == co.Owner && scene.Strokes[i].Seed == co.HostSeed) { _coatHost[c] = i; break; }
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
            var stroke = scene.Materials.Stroke(s.Owner, s.Role).Material;
            int layered = -1;  // a host that places the coat among its own passes draws it itself
            if (stroke is ICoatLayeredHost lh)
                for (int c = 0; c < cc; c++)
                    if (_coatHost[c] == i && lh.Layers(scene.Coats[c].Coat)) { layered = c; break; }
            DrawHelpers.PushColorOverride(s.ColorOverride);
            try
            {
                if (DrawGuard.Allowed(stroke, s.Owner))
                {
                    DrawGuard.Probe?.Invoke(stroke!);
                    if (layered >= 0) ((ICoatLayeredHost)stroke!).DrawWithCoat(dl, in s, in ctx, scene.Coats[layered].Coat, scene.Coats[layered].Alpha);
                    else stroke!.Draw(dl, in s, in ctx);
                    DrawGuard.Ok(stroke!, s.Owner);
                }
            }
            catch (Exception e) { if (stroke != null) DrawGuard.Fail(stroke, s.Owner, e); }
            finally { DrawHelpers.PopColorOverride(); }

            for (int c = 0; c < cc; c++)
                if (_coatHost[c] == i && c != layered) DrawCoat(dl, scene.Coats[c], in ctx);

            if (!anyHosted) continue;
            for (int j = 0; j < pc; j++)
                if (_host[j] == i) DrawParticle(dl, scene.Particles[j], _material[j], in ctx);
            MeshDraw.EndBatch(dl);  // the next stroke draws on its own
        }

        for (int j = 0; j < pc; j++)
            if (_host[j] == -1) DrawParticle(dl, scene.Particles[j], _material[j], in ctx);
        MeshDraw.EndBatch(dl);
    }

    private static void DrawCoat(ImDrawListPtr dl, in CoatPrimitive c, in MaterialContext ctx)
    {
        var key = c.Coat.Matter;
        DrawHelpers.PushColorOverride(c.ColorOverride);
        try
        {
            if (!DrawGuard.Allowed(key, c.Owner)) return;
            DrawGuard.Probe?.Invoke(key); c.Coat.Draw(dl, c.Alpha, in ctx); DrawGuard.Ok(key, c.Owner);
        }
        catch (Exception e) { DrawGuard.Fail(key, c.Owner, e); }
        finally { DrawHelpers.PopColorOverride(); }
    }

    private static void DrawParticle(ImDrawListPtr dl, in ParticlePrimitive p, IParticleMaterial? mat, in MaterialContext ctx)
    {
        if (mat is IBatchedParticleMaterial) MeshDraw.BeginBatch(); else MeshDraw.EndBatch(dl);  // a run of mesh-only drops is staged; anything else draws in its place
        DrawHelpers.PushColorOverride(p.ColorOverride);
        try
        {
            if (!DrawGuard.Allowed(mat, p.Owner)) return;
            DrawGuard.Probe?.Invoke(mat!); mat!.Draw(dl, in p, in ctx); DrawGuard.Ok(mat, p.Owner);
        }
        catch (Exception e) { if (mat != null) DrawGuard.Fail(mat, p.Owner, e); }
        finally { DrawHelpers.PopColorOverride(); }
    }
}

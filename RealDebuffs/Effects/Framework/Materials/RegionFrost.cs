using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// The frost itself: a milky coating that creeps in from the screen edges, drawn as one sparse
/// vertex-colored mesh over a FrostField's growth grid. Condensation (a cool grey fog) leads the ice,
/// and the ice thickens behind its front, which is thick and white at the borders and thin and
/// bluish where it is still arriving.
///
/// It reads everything from RegionPrimitive.State (a FrostField built by FrostEffect); a region with
/// any other state is ignored, so swapping this slot onto another effect does nothing rather than
/// misbehave.
/// </summary>
public sealed class RegionFrost : IRegionMaterial
{
    public string Name => "region.frost-cover";

    // ---- look ----
    private const float FilmAlpha = 0.05f;      // the thin film that coats even the middle of the glass
    private const float IceAlpha  = 0.80f;      // extra opacity of thick ice
    private const float SettleAge = 8f;         // after this the front has all but stopped, and the haze is rebuilt only now and then
    private const float HazeRefresh = 0.12f;    // seconds between rebuilds once settled (also how fast a color-override change shows)

    public void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx)
    {
        if (r.State is not FrostField f) return;

        float a = r.Alpha * ctx.Alpha * f.Alpha;
        if (a <= 0.002f) return;

        // Everything looks colder as the glass freezes: one very faint blue wash across the screen.
        float wash = a * 0.07f * Math.Clamp(f.Progress, 0f, 1f);
        if (wash > 0.003f)
            dl.AddRectFilled(Vector2.Zero, new Vector2(ctx.ScreenW, ctx.ScreenH), DrawHelpers.WithAlpha(FireColor.Pack(0.45f, 0.65f, 0.95f), wash));

        DrawHaze(dl, f, a, ctx);

        var style = new Dendrite.DrawStyle(
            IceColor.Crystal,
            Shadow: FireColor.Pack(0.07f, 0.17f, 0.36f),
            ShadowOffset: StudioLighting.ShadowDirection * (1.3f * ctx.ScreenScale),
            ShadowAlpha: 0.22f,
            Body: IceColor.Pack(0.88f, 0.94f, 1.00f), BodyAlpha: 0.20f,
            WidthScale: ctx.ScreenScale, Alpha: a, Time: ctx.Time);
        f.Ferns.Draw(dl, MeshDraw.WhiteUv(ctx.Time), f.Age, style);
    }

    private static void DrawHaze(ImDrawListPtr dl, FrostField f, float a, in MaterialContext ctx)
    {
        var g = f.Growth;

        // Once the front has settled the haze barely moves (the creep is slower than the eye can follow),
        // so it is rebuilt a few times a second rather than every frame - but at once whenever the
        // overall opacity changes, as it does through every fade.
        bool fresh = f.Age <= SettleAge || a != f.HazeAlpha || MathF.Abs(ctx.Time - f.HazeTime) >= HazeRefresh;
        if (fresh)
        {
            BuildHaze(f, a);
            f.HazeAlpha = a;
            f.HazeTime = ctx.Time;
        }

        MeshDraw.SparseGrid(dl, g.Cols, g.Rows, g.Positions, f.Haze, MeshDraw.WhiteUv(ctx.Time));
    }

    private static void BuildHaze(FrostField f, float a)
    {
        var g = f.Growth;
        int nv = (g.Cols + 1) * (g.Rows + 1);
        float ice = f.Progress, fog = f.Fog;

        float slam = MathF.Exp(-f.Age / 0.45f);                      // the border darkens hard at first, then settles
        float rimAlpha = a * (0.30f + 0.45f * slam);
        bool overridden = DrawHelpers.ColorOverrideActive;           // no override: skip the per-vertex remap entirely

        float[] arrivalArr = g.Arrival, mottle = f.Mottle, rimArr = f.Rim, cap = f.Cap;
        uint[] haze = f.Haze;

        for (int i = 0; i < nv; i++)
        {
            float arrival = arrivalArr[i];
            float mott = mottle[i];
            float rim = rimArr[i];

            // Bottom layer: the cold rim, there from the first frame.
            float cr = 0.09f, cg = 0.20f, cb = 0.42f, ca = rimAlpha * rim;

            // Condensation: a thin cool film that is already on the glass before the ice arrives.
            float fogLead = fog - arrival;
            if (fogLead > 0f)
                Over(ref cr, ref cg, ref cb, ref ca, 0.55f, 0.72f, 0.95f, a * 0.12f * Math.Clamp(fogLead * 1.8f, 0f, 1f) * (0.7f + 0.3f * mott));

            // Ice: a fairly crisp front, then thickening and whitening behind it.
            float lead = ice - arrival;
            if (lead > 0f)
            {
                // A front soft enough for the grid to show cleanly (a sharper one aliases into steps).
                float edge = Math.Clamp(lead / 0.26f, 0f, 1f);
                edge = edge * edge * (3f - 2f * edge);
                float thick = Math.Clamp(lead / 0.75f, 0f, 1f);
                // Frost is thickest at the border, which is where the cold comes from, and thins toward
                // the middle of the glass, which stays mostly clear: the ferns reach in, the coat does not.
                float dens = Math.Clamp(thick * cap[i] * (0.60f + 0.40f * mott) + 0.55f * rim * thick, 0f, 1f);
                uint c = IceColor.Frost.Sample(0.15f + 0.85f * dens);
                Over(ref cr, ref cg, ref cb, ref ca,
                     (c & 255) / 255f, ((c >> 8) & 255) / 255f, ((c >> 16) & 255) / 255f,
                     a * edge * (FilmAlpha + IceAlpha * dens));

                // A faint bright band just behind the front, where the ice is still forming.
                float d = (lead - 0.14f) / 0.12f;
                if (d * d < 6f)
                    Over(ref cr, ref cg, ref cb, ref ca, 0.96f, 0.99f, 1f, a * 0.14f * MathF.Exp(-d * d));
            }

            // Vertex colors are not premultiplied, so a transparent vertex must still carry frost-colored
            // RGB: interpolating toward black would darken the fringe of every cell, not just fade it.
            if (ca <= 0.002f) { cr = 0.55f; cg = 0.72f; cb = 0.92f; ca = 0f; }
            haze[i] = Pack(cr, cg, cb, ca, overridden);
        }
    }

    /// <summary>
    /// Opaque color plus alpha. Without a color override this is exactly what WithAlpha(Pack(rgb), alpha)
    /// produces, minus its override lookup; with one it lifts the color off grey (IceColor.Tintable) and
    /// calls it, so near-white frost recolors smoothly instead of snapping to the override's full saturation.
    /// </summary>
    private static uint Pack(float r, float g, float b, float alpha, bool overridden)
    {
        uint rgb = FireColor.Pack(r, g, b);
        if (overridden) return DrawHelpers.WithAlpha(IceColor.Tintable(rgb), alpha);
        alpha = alpha > 0f ? (alpha < 1f ? alpha : 1f) : 0f;
        return (rgb & 0x00FFFFFFu) | ((uint)(int)(255f * alpha) << 24);
    }

    /// <summary>Straight-alpha "over": layer (r, g, b, a) on top of the accumulated color.</summary>
    private static void Over(ref float r, ref float g, ref float b, ref float a, float r2, float g2, float b2, float a2)
    {
        float outA = a2 + a * (1f - a2);
        if (outA <= 1e-5f) return;
        float k1 = a2 / outA, k0 = 1f - k1;
        r = r2 * k1 + r * k0;
        g = g2 * k1 + g * k0;
        b = b2 * k1 + b * k0;
        a = outA;
    }
}

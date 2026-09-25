using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Hysteria: your mind is coming apart. A faint, unsteady purple-red vignette with short jittery scribbles flickering into existence at random and vanishing again, like intrusive thoughts that won't resolve into anything.</summary>
public sealed class HysteriaEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Hysteria;

    private static readonly uint Tint = DrawHelpers.ToU32(0.35f, 0.05f, 0.30f, 1f);
    private static readonly uint Scribble = DrawHelpers.ToU32(0.85f, 0.30f, 0.75f, 1f);

    private const int ScribbleSlots = 5;
    private const int PointsPerScribble = 6;

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        int flickerBucket = (int)(time * 7f);
        float jitter = 0.6f + 0.4f * DrawHelpers.Hash01(flickerBucket);
        DrawHelpers.DrawVignette(dl, screenSize, Tint, 0.14f, alpha * jitter * 0.7f);

        Span<Vector2> pts = stackalloc Vector2[PointsPerScribble];
        for (int slot = 0; slot < ScribbleSlots; slot++)
        {
            // Each slot gets its own short-lived "life" (on for a fraction of its period, then a
            // gap) from a coarse per-slot clock, so scribbles pop in and out independently rather
            // than all flickering in sync.
            float slotPeriod = 1.1f + slot * 0.13f;
            float slotPhase = (time / slotPeriod + slot * 0.37f) % 1f;
            if (slotPhase > 0.45f) continue;
            float life = 1f - slotPhase / 0.45f;

            int seed = unchecked(0x100008 + slot * 5003 + (int)(time / slotPeriod) * 97);
            var center = new Vector2(
                DrawHelpers.HashRange(seed, 0.15f, 0.85f) * screenSize.X,
                DrawHelpers.HashRange(seed + 1, 0.12f, 0.80f) * screenSize.Y);
            float scale = DrawHelpers.HashRange(seed + 2, 26f, 55f);

            var cursor = center;
            for (int p = 0; p < PointsPerScribble; p++)
            {
                pts[p] = cursor;
                cursor += new Vector2(
                    DrawHelpers.HashRange(seed + 10 + p * 2, -1f, 1f),
                    DrawHelpers.HashRange(seed + 11 + p * 2, -1f, 1f)) * scale * 0.5f;
            }

            ref Vector2 first = ref pts[0];
            dl.AddPolyline(ref first, PointsPerScribble, DrawHelpers.WithAlpha(Scribble, alpha * life * 0.85f), ImDrawFlags.None, 2.0f);
        }
    }
}

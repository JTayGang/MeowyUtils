using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Roots creep up from the bottom edge and sway gently - you're not going anywhere.</summary>
public sealed class BindEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Bind;
    private const int VineCount = 6;
    private const int SegmentsPerVine = 10;

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        uint vine = DrawHelpers.WithAlpha(DrawHelpers.ToU32(0.22f, 0.42f, 0.16f, 1f), alpha);
        uint vineDark = DrawHelpers.WithAlpha(DrawHelpers.ToU32(0.12f, 0.22f, 0.08f, 1f), alpha);
        float sway = DrawHelpers.Pulse(time, 3.5f) - 0.5f;

        for (int i = 0; i < VineCount; i++)
        {
            int seed = i * 7331;
            float baseX = screenSize.X * ((i + 0.5f) / VineCount) + DrawHelpers.HashRange(seed, -20f, 20f);
            // vines climb higher the more established the bind is (ramps in with the fade-in alpha)
            float height = screenSize.Y * (0.10f + 0.16f * DrawHelpers.Hash01(seed + 1)) * alpha;

            var prev = DrawHelpers.V(baseX, screenSize.Y);
            for (int s = 1; s <= SegmentsPerVine; s++)
            {
                float f = (float)s / SegmentsPerVine;
                float swayAmount = MathF.Sin(f * MathF.PI * 1.5f + i) * 14f * sway * f;
                var next = DrawHelpers.V(baseX + swayAmount, screenSize.Y - height * f);

                dl.AddLine(prev, next, s % 3 == 0 ? vineDark : vine, 4f * (1f - f * 0.4f));
                if (s % 3 == 0)
                {
                    var leafDir = DrawHelpers.V(DrawHelpers.HashRange(seed + s, -1f, 1f), -0.3f);
                    if (leafDir.LengthSquared() > 0.0001f)
                        dl.AddLine(next, next + Vector2.Normalize(leafDir) * 10f, vine, 3f);
                }
                prev = next;
            }
        }
    }
}

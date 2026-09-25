using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Electrocution: a rapid, buzzing shock rather than Paralysis's dramatic storm. A thin yellow-white band that stutters unevenly, with short sparks jittering in and out along the edges.</summary>
public sealed class ElectrocutionEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Electrocution;

    private static readonly uint Glow = DrawHelpers.ToU32(1.00f, 0.95f, 0.55f, 1f);
    private static readonly uint Hot = DrawHelpers.ToU32(1.00f, 1.00f, 0.85f, 1f);

    private const int SparkCount = 7;

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);

        // Genuine stutter, not a smooth pulse: step time into ~18 buckets/sec and hash EACH bucket,
        // so brightness jumps between values instead of easing between them - reads as buzzing
        // rather than breathing.
        int bucket = (int)(time * 18f);
        float buzz = 0.5f + 0.5f * DrawHelpers.Hash01(bucket);
        DrawHelpers.DrawVignette(dl, screenSize, Glow, 0.05f, alpha * buzz);

        for (int i = 0; i < SparkCount; i++)
        {
            int sparkBucket = (int)(time * 9f) + i * 101;
            if (DrawHelpers.Hash01(sparkBucket) > 0.4f) continue; // most bucket/spark pairs stay dark - sparse and irregular

            float along = DrawHelpers.HashRange(sparkBucket + 1, 0.03f, 0.97f);
            byte edge = (byte)(DrawHelpers.Hash01(sparkBucket + 2) * 4f);
            float len = DrawHelpers.HashRange(sparkBucket + 3, 0.01f, 0.03f) * shortSide;
            float jag = DrawHelpers.HashRange(sparkBucket + 4, -1f, 1f) * len * 0.5f;

            Vector2 a, b;
            switch (edge)
            {
                default:
                case 0: a = new Vector2(along * screenSize.X, 0); b = a + new Vector2(jag, len); break;
                case 1: a = new Vector2(screenSize.X, along * screenSize.Y); b = a + new Vector2(-len, jag); break;
                case 2: a = new Vector2(along * screenSize.X, screenSize.Y); b = a + new Vector2(jag, -len); break;
                case 3: a = new Vector2(0, along * screenSize.Y); b = a + new Vector2(len, jag); break;
            }

            float sparkAlpha = alpha * DrawHelpers.HashRange(sparkBucket + 5, 0.5f, 1f);
            dl.AddLine(a, b, DrawHelpers.WithAlpha(Glow, sparkAlpha * 0.6f), 3.5f);
            dl.AddLine(a, b, DrawHelpers.WithAlpha(Hot, sparkAlpha), 1.4f);
        }
    }
}

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Disease: a dull, sapping sickness - duller and slower than Poison's vivid acid tone. An olive-brown haze with a scattering of specks drifting listlessly, like the whole screen has caught a cold.</summary>
public sealed class DiseaseEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Disease;

    private static readonly uint Tint = DrawHelpers.ToU32(0.42f, 0.40f, 0.20f, 1f);
    private static readonly uint Speck = DrawHelpers.ToU32(0.55f, 0.52f, 0.28f, 1f);

    private const int SpeckCount = 22;
    private readonly float[] _along = new float[SpeckCount];
    private readonly float[] _depth = new float[SpeckCount];
    private readonly byte[] _edge = new byte[SpeckCount];
    private readonly float[] _size = new float[SpeckCount];

    public DiseaseEffect()
    {
        for (int i = 0; i < SpeckCount; i++)
        {
            int s = unchecked(0x100005 + i * 733);
            _edge[i] = (byte)(DrawHelpers.Hash01(s) * 4f);
            _along[i] = DrawHelpers.HashRange(s + 1, 0f, 1f);
            _depth[i] = DrawHelpers.HashRange(s + 2, 0.01f, 0.09f);
            _size[i] = DrawHelpers.HashRange(s + 3, 3f, 7f);
        }
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float sag = 0.85f + 0.15f * DrawHelpers.Pulse(time, 5.0f); // slow, tired breathing rather than a lively pulse
        DrawHelpers.DrawVignette(dl, screenSize, Tint, 0.11f, alpha * sag);

        for (int i = 0; i < SpeckCount; i++)
        {
            float depthPx = shortSide * _depth[i];
            float wander = MathF.Sin(time * 0.18f + i) * shortSide * 0.02f;

            float x, y;
            switch (_edge[i])
            {
                default:
                case 0: x = _along[i] * screenSize.X + wander; y = depthPx; break;
                case 1: x = screenSize.X - depthPx; y = _along[i] * screenSize.Y + wander; break;
                case 2: x = _along[i] * screenSize.X + wander; y = screenSize.Y - depthPx; break;
                case 3: x = depthPx; y = _along[i] * screenSize.Y + wander; break;
            }

            float twinkle = 0.5f + 0.5f * DrawHelpers.Pulse(time * 0.4f, 2.6f, i * 0.31f);
            dl.AddCircleFilled(new Vector2(x, y), _size[i], DrawHelpers.WithAlpha(Speck, alpha * 0.5f * twinkle));
        }
    }
}

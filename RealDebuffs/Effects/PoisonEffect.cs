using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Toxic cloud: a pulsing green vignette with a scattering of semi-transparent haze circles
/// drifting inside its band, plus a dense swarm of bubbles streaming chaotically inward
/// toward the centre. Colours lean yellow-green (acid) rather than pure green.
/// </summary>
public sealed class PoisonEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Poison;

    // ---- palette ----
    private static readonly uint Hot  = DrawHelpers.ToU32(0.85f, 1.00f, 0.45f, 1f); // acid core
    private static readonly uint Mid  = DrawHelpers.ToU32(0.55f, 0.85f, 0.25f, 1f); // warm body
    private static readonly uint Glow = DrawHelpers.ToU32(0.20f, 0.45f, 0.10f, 1f); // dark haze

    // ---- haze circles drifting inside the vignette band ----
    private const int HazeCircles = 36;

    // Depth measured inward from the edge, as a fraction of the SHORTER screen side. Same scale
    // on every edge, so the band is visually uniform thickness. At draw time this is converted
    // to per-axis pixels, so on a 16:9 screen the horizontal inset gets a proportionally larger
    // fraction of width than the vertical inset gets of height.
    private const float HazeDepthMin = 0.01f;
    private const float HazeDepthMax = 0.05f;

    private struct HazeCircle
    {
        public byte  Edge;              // 0=top, 1=right, 2=bottom, 3=left
        public float Along;             // 0..1 position along that edge
        public float Depth;             // shortSide-fraction inset from that edge
        public float DriftX, DriftY;    // wander amplitude, fraction of shortSide
        public float PhaseX, PhaseY;    // wander phase
        public float FreqX,  FreqY;     // wander frequency
        public float SizeFrac;          // radius, fraction of shortSide
        public float Alpha;             // per-circle base alpha
    }

    private readonly HazeCircle[] _haze = new HazeCircle[HazeCircles];

    // ---- bubble swarm ----
    private readonly EdgeParticleField _bubbles = new(maxParticles: 80, seedSalt: 0x0B0BB1E5);

    // Cached per frame - spawn delegates read this directly so we never allocate a closure.
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _bubblePos;
    private readonly Func<int, Vector2> _bubbleVel;
    private readonly Func<int, string>  _noGlyph;

    public PoisonEffect()
    {
        _bubblePos = BubbleSpawnPos;
        _bubbleVel = BubbleSpawnVelocity;
        _noGlyph   = static _ => "";

        // Bake the haze field once. Each circle picks an edge, a position along it, and a depth
        // into the band. Because both placement and inset are recomputed at draw time from the
        // current screen size, positions stay correct at any resolution/aspect ratio.
        for (int i = 0; i < HazeCircles; i++)
        {
            int s = unchecked(0x0F060000 + i * 7919);
            _haze[i] = new HazeCircle
            {
                Edge  = (byte)(DrawHelpers.Hash01(s) * 4f),
                Along = DrawHelpers.HashRange(s + 1, 0f, 1f),
                Depth = DrawHelpers.HashRange(s + 2, HazeDepthMin, HazeDepthMax),
                DriftX = DrawHelpers.HashRange(s + 3, 0.012f, 0.032f),
                DriftY = DrawHelpers.HashRange(s + 4, 0.012f, 0.032f),
                PhaseX = DrawHelpers.HashRange(s + 5, 0f, MathF.PI * 2f),
                PhaseY = DrawHelpers.HashRange(s + 6, 0f, MathF.PI * 2f),
                FreqX  = DrawHelpers.HashRange(s + 7, 0.06f, 0.16f),
                FreqY  = DrawHelpers.HashRange(s + 8, 0.06f, 0.16f),
                SizeFrac = DrawHelpers.HashRange(s + 9, 0.025f, 0.055f),
                Alpha    = DrawHelpers.HashRange(s + 10, 0.08f, 0.18f),
            };
        }
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        // Pulsing vignette - the base "gas" tint and its motion.
        float queasy = DrawHelpers.Pulse(time, 3.2f);
        DrawHelpers.DrawVignette(dl, screenSize, Mid, 0.10f + 0.02f * queasy, alpha * 0.85f);

        // Semi-transparent haze circles drifting inside the band.
        DrawHaze(dl, screenSize, time, alpha);

        // Dense chaotic swarm of bubbles drifting in from all four edges toward the centre.
        _bubbles.Update(
            time, dt,
            spawnIntervalMin: 0.02f, spawnIntervalMax: 0.08f,
            spawnPos: _bubblePos, spawnVelocity: _bubbleVel,
            pickGlyph: _noGlyph,
            lifespanMin: 1.6f, lifespanMax: 2.8f,
            sizeMin: 2f, sizeMax: 6f);

        for (int i = 0; i < _bubbles.Count; i++)
        {
            ref readonly var p = ref _bubbles[i];
            float age  = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);

            // 2D wobble, independent of travel direction.
            float wobX = MathF.Sin(age * 3.4f + p.Born * 1.7f) * 4f;
            float wobY = MathF.Cos(age * 2.6f + p.Born * 2.3f) * 4f;
            var pos = p.Pos + new Vector2(wobX, wobY);

            dl.AddCircleFilled(pos, p.Size * 1.8f, DrawHelpers.WithAlpha(Glow, alpha * fade * 0.35f));
            dl.AddCircleFilled(pos, p.Size,        DrawHelpers.WithAlpha(Mid,  alpha * fade * 0.70f));
            dl.AddCircleFilled(pos, p.Size * 0.55f, DrawHelpers.WithAlpha(Hot,  alpha * fade * 0.95f));
        }
    }

    // =====================================================================================
    // Haze circles
    // =====================================================================================

    private void DrawHaze(ImDrawListPtr dl, Vector2 screenSize, float time, float alpha)
    {
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);

        for (int i = 0; i < HazeCircles; i++)
        {
            ref readonly var c = ref _haze[i];

            // Depth is a shortSide-fraction; convert to a pixel offset. Same pixels on every
            // edge, so the band thickness is visually uniform all the way around.
            float depthPx = shortSide * c.Depth;

            float baseX, baseY;
            switch (c.Edge)
            {
                default:
                case 0: baseX = c.Along * screenSize.X;       baseY = depthPx;                 break; // top
                case 1: baseX = screenSize.X - depthPx;       baseY = c.Along * screenSize.Y;  break; // right
                case 2: baseX = c.Along * screenSize.X;       baseY = screenSize.Y - depthPx;  break; // bottom
                case 3: baseX = depthPx;                      baseY = c.Along * screenSize.Y;  break; // left
            }

            // Each circle drifts on its own little 2D Lissajous. Independent phases and
            // frequencies mean there's no coherent flow direction - just slow random drift.
            float dx = MathF.Sin(time * c.FreqX + c.PhaseX) * shortSide * c.DriftX;
            float dy = MathF.Cos(time * c.FreqY + c.PhaseY) * shortSide * c.DriftY;

            // Cheap per-circle breathing: reuse the existing wander frequency/phase pair
            // (with a phase swapped in) so no extra baked state is needed. FreqX * 1.7
            // decorrelates the fade from the X-drift so it doesn't read as a coupled wobble.
            float fade = 0.7f + 0.3f * MathF.Sin(time * c.FreqX * 1.7f + c.PhaseY);

            float r = shortSide * c.SizeFrac;
            float a = alpha * c.Alpha * fade;
            if (a < 0.002f) continue;

            dl.AddCircleFilled(new Vector2(baseX + dx, baseY + dy), r,
                               DrawHelpers.WithAlpha(Mid, a));
        }
    }

    // =====================================================================================
    // Cached spawn delegates (no per-frame closures)
    // =====================================================================================

    private Vector2 BubbleSpawnPos(int seed)
    {
        float edge  = DrawHelpers.Hash01(seed);
        float along = DrawHelpers.HashRange(seed + 1, 0.02f, 0.98f);
        const float pad = 12f;

        if (edge < 0.25f) return new Vector2(along * _screenSize.X, _screenSize.Y + pad); // bottom
        if (edge < 0.50f) return new Vector2(along * _screenSize.X, -pad);                // top
        if (edge < 0.75f) return new Vector2(-pad, along * _screenSize.Y);                // left
        return new Vector2(_screenSize.X + pad, along * _screenSize.Y);                   // right
    }

    private Vector2 BubbleSpawnVelocity(int seed)
    {
        Vector2 center = _screenSize * 0.5f;
        Vector2 pos    = BubbleSpawnPos(seed);
        Vector2 toC    = center - pos;
        float len = toC.Length();
        if (len < 1f) return Vector2.Zero;

        Vector2 dir = toC / len;

        // Wide angular spread -> chaotic cloud rather than neat radial spokes.
        float spread = DrawHelpers.HashRange(seed + 2, -0.9f, 0.9f);
        float c = MathF.Cos(spread), s = MathF.Sin(spread);
        Vector2 d2 = new(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);

        float speed = DrawHelpers.HashRange(seed + 3, 60f, 170f);
        return d2 * speed;
    }
}
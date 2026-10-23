using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>The lidless eye: a shaded sphere with its iris turned toward the middle of the screen, veins over it, and the nerve it came off trailing behind.</summary>
public sealed partial class ParticleEye
{
    private const int OrbRows = 5, MaxOrbSegs = 28, TailPts = 12;

    private static readonly Vector2[] OrbPos = new Vector2[OrbRows * (MaxOrbSegs + 1)];
    private static readonly uint[] OrbCol = new uint[OrbRows * (MaxOrbSegs + 1)];
    private static readonly Vector2[] Tail = new Vector2[TailPts];
    private static readonly Vector2[] TailN = new Vector2[TailPts];
    private static readonly float[] TailHalf = new float[TailPts];
    private static readonly uint[] TailCol = new uint[TailPts];

    // Light comes from up and to the left of the screen.
    private static readonly Vector2 LightDir = Vector2.Normalize(new Vector2(-0.45f, -0.55f));

    private void DrawOrb(ImDrawListPtr dl, in ParticlePrimitive p, in Pose e, Vector2 uv, float time, int screenW, int screenH)
    {
        float w = e.W;
        Vector2 toMid = new Vector2(screenW * 0.5f, screenH * 0.5f) - e.Pos;
        float len = toMid.Length();
        Vector2 toward = len > 1f ? toMid / len : new Vector2(0f, -1f);

        // It faces you, its iris leaning a little toward the middle of the screen; its nerve trails back the way it has come.
        int glance = (int)(time * 1.3f);
        Vector2 jitter = new Vector2(DrawHelpers.Hash01(p.Seed * 3 + glance * 17) - 0.5f, DrawHelpers.Hash01(p.Seed * 5 + glance * 29) - 0.5f) * 0.10f * w;
        Vector2 ic = e.FromScreenDir(toward) * 0.30f * w + jitter;

        // A dark pool round it, so it reads on a bright scene too, and a little light of its own.
        Span<float> rr = stackalloc float[2] { w * 0.95f, w * 2.0f };
        Span<uint> rc = stackalloc uint[2] { DrawHelpers.WithAlpha(Ink, e.K * 0.40f), DrawHelpers.WithAlpha(Ink, 0f) };
        MeshDraw.Radial(dl, e.Pos, uv, DrawHelpers.WithAlpha(Ink, e.K * 0.55f), 14, rr, rc, 0f, 1f, 0, 0f);

        Mb.Clear();
        BuildNerve(in e, toward, time, p.Seed);
        BuildSphere(in e);
        if (e.Detailed) BuildOrbVeins(in e, ic, p.Seed);
        BuildIris(in e, ic, p.Seed, time, flat: MathF.Sqrt(MathF.Max(0.2f, 1f - (ic.LengthSquared() / (w * w)))));
        Mb.Flush(dl, uv);

        // The wet glint: a window, and a faint answer.
        uint white = _palette.Sample(1f);
        Span<float> hr = stackalloc float[2];
        Span<uint> hc = stackalloc uint[2];
        for (int g = 0; g < 2; g++)
        {
            Vector2 at = e.Pos + (g == 0 ? new Vector2(-0.36f, -0.42f) : new Vector2(0.30f, 0.34f)) * w;
            float size = (g == 0 ? 0.24f : 0.09f) * w, gain = g == 0 ? 1f : 0.35f;
            hr[0] = size * 0.5f; hr[1] = size;
            hc[0] = e.Tint(white, 0.65f * gain); hc[1] = e.Tint(white, 0f);
            MeshDraw.Radial(dl, at, uv, e.Tint(white, gain), 8, hr, hc, 0f, 1f, 0, 0f);
        }
    }

    /// <summary>The ball: lit from the upper left, darkening toward its edge the way a sphere does, warm at the rim.</summary>
    private void BuildSphere(in Pose e)
    {
        float w = e.W;
        int segs = e.Cols >= 16 ? MaxOrbSegs : e.Cols >= 12 ? 20 : 14;
        ReadOnlySpan<float> radii = stackalloc float[OrbRows] { 0f, 0.55f, 0.88f, 1.0f, 1.05f };
        for (int j = 0; j <= segs; j++)
        {
            float a = MathF.Tau * j / segs;
            Vector2 dir = new(MathF.Cos(a), MathF.Sin(a));
            float facing = Vector2.Dot(dir, LightDir);
            for (int r = 0; r < OrbRows; r++)
            {
                float f = radii[r];
                float tone = 0.84f - 0.62f * MathF.Pow(f, 2.4f) + 0.20f * facing * MathF.Pow(f, 1.5f) - (r == 3 ? 0.10f : 0f);
                uint c = DrawHelpers.LerpColor(_palette.Sample(DrawHelpers.Saturate(tone)), Flesh, 0.30f * MathF.Pow(f, 3f));
                float alpha = r == OrbRows - 1 ? 0f : 0.97f;
                int at = r * (segs + 1) + j;
                OrbPos[at] = e.Pos + dir * (f * w);
                OrbCol[at] = e.Tint(c, alpha);
            }
        }
        int count = OrbRows * (segs + 1);
        Mb.Reserve(count, (OrbRows - 1) * segs * 6);
        int first = Mb.AddBlockReserved(OrbPos, OrbCol, count);
        Mb.StitchGrid(first, OrbRows, segs + 1);
    }

    /// <summary>Veins crawling in from the rim toward the iris.</summary>
    private void BuildOrbVeins(in Pose e, Vector2 ic, int seed)
    {
        float w = e.W, R = e.IrisRadius;
        int count = e.Look.VeinCount;
        for (int v = 0; v < count; v++)
        {
            int h = seed * 7 + v * 13;
            float a = MathF.Tau * (v + DrawHelpers.Hash01(h)) / count;
            float bend = (DrawHelpers.Hash01(h + 1) - 0.5f) * 0.9f;
            for (int i = 0; i < 4; i++)
            {
                float t = i / 3f;
                float ang = a + bend * t * (1f - t) * 2f;
                float rad = DrawHelpers.Lerp(0.98f * w, MathF.Max(0.4f * w, ic.Length() + R * 1.15f), t);
                VeinCtl[i] = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rad;
            }
            for (int i = 0; i < VeinPts; i++)
            {
                int span = Math.Min(i / 2, 2);
                float t = i == VeinPts - 1 ? 1f : (i % 2) * 0.5f;
                Vein[i] = e.ToScreen(Spline(VeinCtl, span, t));
                VeinHalf[i] = MathF.Max(0.12f * e.Px, (0.5f * e.Px + 0.006f * w) * (1f - 0.7f * i / (VeinPts - 1)));
            }
            MeshBuilder.MiterNormals(Vein, VeinN);
            Mb.Ribbon(Vein, VeinN, VeinHalf, e.Tint(VeinCol, e.Look.VeinAlpha), 0.5f * e.Px);
        }
    }

    /// <summary>The nerve it hangs from: a fleshy cord trailing toward the middle of the screen, thick where it joins and writhing as it thins.</summary>
    private void BuildNerve(in Pose e, Vector2 toward, float time, int seed)
    {
        float w = e.W;
        Vector2 across = new(-toward.Y, toward.X);
        float phase = DrawHelpers.Hash01(seed + 3) * MathF.Tau;
        Vector2 root = e.Pos + toward * (0.82f * w);
        uint flesh = DrawHelpers.LerpColor(_palette.Sample(0.34f), Flesh, 0.5f);

        for (int i = 0; i < TailPts; i++)
        {
            float s = i / (float)(TailPts - 1);
            float wiggle = MathF.Sin(s * 6f - time * 3f + phase) * 0.30f * w * MathF.Pow(s, 0.8f);
            Tail[i] = root + toward * (s * 4.5f * w) + across * wiggle;
            TailHalf[i] = w * (0.20f * MathF.Pow(1f - s, 0.9f) + 0.02f);
            TailCol[i] = e.Tint(flesh, 0.85f * MathF.Pow(1f - s, 1.1f));
        }
        MeshBuilder.MiterNormals(Tail, TailN);
        Mb.Ribbon(Tail, TailN, TailHalf, TailCol, MathF.Max(0.8f * e.Px, 0.04f * w));

        // A paler thread of light down it.
        for (int i = 0; i < TailPts; i++)
        {
            float s = i / (float)(TailPts - 1);
            TailHalf[i] = MathF.Max(0.3f * e.Px, TailHalf[i] * 0.18f);
            TailCol[i] = e.Tint(_palette.Sample(0.62f), 0.30f * (1f - s));
        }
        Mb.Ribbon(Tail, TailN, TailHalf, TailCol, 0.5f * e.Px);
    }
}

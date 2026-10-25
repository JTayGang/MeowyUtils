using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>The lidless eye: a shaded sphere with its iris turned toward the middle of the screen, veins over it, and the nerve it came off trailing behind.</summary>
public sealed partial class ParticleEye
{
    private const int OrbRows = 7, MaxOrbSegs = 28, TailPts = 12;

    private static readonly Vector2[] OrbPos = new Vector2[OrbRows * (MaxOrbSegs + 1)];
    private static readonly uint[] OrbCol = new uint[OrbRows * (MaxOrbSegs + 1)];
    private static readonly Vector2[] Tail = new Vector2[TailPts];
    private static readonly Vector2[] TailN = new Vector2[TailPts];
    private static readonly float[] TailHalf = new float[TailPts];
    private static readonly uint[] TailCol = new uint[TailPts];

    /// <summary>The light on the orb, as a direction in 3D: from the middle of the screen (the lit end of the tunnel), and a little toward you.</summary>
    private static Vector3 OrbLight(Vector2 toLight) => Vector3.Normalize(new Vector3(toLight.X, toLight.Y, 0.45f));

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

        // The wet glint sits where the surface faces halfway between you and the light; a faint answer opposite; a bright thin rim on the lit edge.
        uint white = _palette.Sample(1f);
        Vector3 h3 = Vector3.Normalize(OrbLight(toward) + Vector3.UnitZ);
        Span<float> hr = stackalloc float[3];
        Span<uint> hc = stackalloc uint[3];
        for (int g = 0; g < 2; g++)
        {
            Vector2 off = g == 0 ? new Vector2(h3.X, h3.Y) * 0.92f : new Vector2(-h3.X, -h3.Y) * 0.62f;
            Vector2 at = e.Pos + off * w;
            float size = (g == 0 ? 0.20f : 0.08f) * w, gain = (g == 0 ? 1f : 0.28f) * (1f - 0.5f * e.Haze);
            hr[0] = size * 0.62f; hr[1] = size * 0.86f; hr[2] = size * 1.12f;
            hc[0] = e.Tint(white, gain); hc[1] = e.Tint(white, 0.55f * gain); hc[2] = e.Tint(white, 0f);
            MeshDraw.Radial(dl, at, uv, e.Tint(white, gain), 8, hr, hc, MathF.PI * 0.125f, g == 0 ? 0.86f : 1f, 0, 0f);
        }
    }

    /// <summary>
    /// The ball, shaded as a sphere lit from the far end of the tunnel: diffuse falling off toward the side away from the light, a warm
    /// flush where the light just reaches around the edge (flesh is translucent), a thin pale rim on the lit limb where the wet surface
    /// catches grazing light, and the dusty air lifting it toward haze at a distance.
    /// </summary>
    private void BuildSphere(in Pose e)
    {
        float w = e.W;
        int segs = e.Cols >= 16 ? MaxOrbSegs : e.Cols >= 12 ? 20 : 14;
        Vector3 L = OrbLight(e.Light);
        ReadOnlySpan<float> radii = stackalloc float[OrbRows] { 0f, 0.38f, 0.62f, 0.80f, 0.92f, 1.00f, 1.05f };
        for (int j = 0; j <= segs; j++)
        {
            float a = MathF.Tau * j / segs;
            Vector2 dir = new(MathF.Cos(a), MathF.Sin(a));
            for (int r = 0; r < OrbRows; r++)
            {
                float f = MathF.Min(radii[r], 1f);
                // Surface normal of a sphere at this distance from its middle.
                float nz = MathF.Sqrt(MathF.Max(0f, 1f - f * f));
                float ndl = MathF.Max(0f, dir.X * f * L.X + dir.Y * f * L.Y + nz * L.Z);

                float tone = 0.14f + 0.72f * MathF.Pow(ndl, 0.85f) + 0.10f * nz;
                float rim = MathF.Pow(1f - nz, 3f) * DrawHelpers.Saturate(Vector2.Dot(dir, e.Light) * 1.4f);
                tone += 0.30f * rim;
                // Light bleeds red through the thin edge just past the terminator.
                float flush = DrawHelpers.Smooth(ndl * 5f) * (1f - DrawHelpers.Smooth(ndl * 1.6f)) * MathF.Pow(f, 1.5f);

                uint c = DrawHelpers.LerpColor(_palette.Sample(DrawHelpers.Saturate(tone)), Flesh, DrawHelpers.Saturate(0.20f * MathF.Pow(f, 3f) + 0.42f * flush));
                float alpha = r == OrbRows - 1 ? 0f : 0.97f;
                int at = r * (segs + 1) + j;
                OrbPos[at] = e.Pos + dir * (f * w) * (r == OrbRows - 1 ? 1.05f : 1f);
                OrbCol[at] = e.Tint(c, alpha);
            }
        }
        int count = OrbRows * (segs + 1);
        Mb.Reserve(count, (OrbRows - 1) * segs * 6);
        int first = Mb.AddBlockReserved(OrbPos, OrbCol, count);
        Mb.StitchGrid(first, OrbRows, segs + 1);
    }

    /// <summary>Veins crawling in from the rim toward the iris: long, wandering, thick where they leave the edge and fading as they thin, the larger ones forking.</summary>
    private void BuildOrbVeins(in Pose e, Vector2 ic, int seed)
    {
        float w = e.W, R = e.IrisRadius;
        int count = Math.Min(14, e.Look.VeinCount * 2 + 2);
        int pts = e.Huge ? FinePts : e.Large ? 7 : 5;
        float reachIn = MathF.Max(0.30f * w, ic.Length() + R * 1.12f);
        for (int v = 0; v < count; v++)
        {
            int h = seed * 7 + v * 13;
            float a = MathF.Tau * (v + DrawHelpers.Hash01(h)) / count;
            float bend = (DrawHelpers.Hash01(h + 1) - 0.5f) * 1.1f, ph = DrawHelpers.Hash01(h + 2) * MathF.Tau;
            float lit = 0.55f + 0.45f * MathF.Cos(a - MathF.Atan2(e.Light.Y, e.Light.X));
            for (int i = 0; i < pts; i++)
            {
                float u = i / (float)(pts - 1);
                float ang = a + bend * u * (1f - u) * 2.2f + 0.05f * MathF.Sin(u * 8f + ph);
                float rad = DrawHelpers.Lerp(0.985f * w, reachIn, MathF.Pow(u, 0.9f));
                FineP[i] = e.ToScreen(new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rad);
                FineHalf[i] = MathF.Max(0.12f * e.Px, (0.62f * e.Px + 0.008f * w) * (1f - 0.75f * u));
                FineCol[i] = e.Tint(VeinCol, e.Look.VeinAlpha * lit * (1f - 0.6f * u * u));
            }
            MeshBuilder.MiterNormals(FineP.AsSpan(0, pts), FineN.AsSpan(0, pts));
            Mb.Ribbon(FineP.AsSpan(0, pts), FineN.AsSpan(0, pts), FineHalf.AsSpan(0, pts), FineCol.AsSpan(0, pts), e.Soft(0.5f * e.Px));

            if (!e.Huge || DrawHelpers.Hash01(h + 3) < 0.4f) continue;
            // A fork part of the way in.
            float u0 = 0.25f + 0.30f * DrawHelpers.Hash01(h + 4);
            float a0 = a + bend * u0 * (1f - u0) * 2.2f, r0 = DrawHelpers.Lerp(0.985f * w, reachIn, MathF.Pow(u0, 0.9f));
            float side = DrawHelpers.Hash01(h + 5) < 0.5f ? -1f : 1f;
            for (int i = 0; i < 5; i++)
            {
                float u = i / 4f;
                float ang = a0 + side * (0.02f + 0.20f * u) / MathF.Max(0.35f, r0 / w);
                float rad = r0 - u * 0.22f * w;
                FineP[i] = e.ToScreen(new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rad);
                FineHalf[i] = MathF.Max(0.10f * e.Px, 0.34f * e.Px * (1f - 0.8f * u));
                FineCol[i] = e.Tint(VeinCol, e.Look.VeinAlpha * 0.8f * lit * (1f - 0.85f * u));
            }
            MeshBuilder.MiterNormals(FineP.AsSpan(0, 5), FineN.AsSpan(0, 5));
            Mb.Ribbon(FineP.AsSpan(0, 5), FineN.AsSpan(0, 5), FineHalf.AsSpan(0, 5), FineCol.AsSpan(0, 5), e.Soft(0.4f * e.Px));
        }
    }

    /// <summary>The nerve it hangs from: a fleshy cord trailing toward the middle of the screen, thick where it joins and writhing as it thins, shaded as a tube.</summary>
    private void BuildNerve(in Pose e, Vector2 toward, float time, int seed)
    {
        float w = e.W;
        Vector2 across = new(-toward.Y, toward.X);
        float phase = DrawHelpers.Hash01(seed + 3) * MathF.Tau;
        Vector2 root = e.Pos + toward * (0.82f * w);
        uint flesh = DrawHelpers.LerpColor(_palette.Sample(0.34f), Flesh, 0.5f);
        uint shadow = DrawHelpers.LerpColor(flesh, Ink, 0.62f);
        uint lit = DrawHelpers.LerpColor(flesh, _palette.Sample(0.80f), 0.38f);
        // The light is at the end of the tunnel, which is where the nerve runs: the side of it facing the middle of its width catches it, the far edges fall away.
        for (int i = 0; i < TailPts; i++)
        {
            float s = i / (float)(TailPts - 1);
            float wiggle = MathF.Sin(s * 6f - time * 3f + phase) * 0.30f * w * MathF.Pow(s, 0.8f);
            Tail[i] = root + toward * (s * 4.5f * w) + across * wiggle;
        }
        MeshBuilder.MiterNormals(Tail, TailN);

        // Three ribbons, wide and dark to narrow and light, build up the roundness of the cord.
        for (int layer = 0; layer < 3; layer++)
        {
            float widthK = layer == 0 ? 1f : layer == 1 ? 0.64f : 0.28f;
            uint c = layer == 0 ? shadow : layer == 1 ? flesh : lit;
            float alphaK = layer == 0 ? 0.90f : layer == 1 ? 0.80f : 0.55f;
            for (int i = 0; i < TailPts; i++)
            {
                float s = i / (float)(TailPts - 1);
                TailHalf[i] = w * (0.20f * MathF.Pow(1f - s, 0.9f) + 0.02f) * widthK;
                TailCol[i] = e.Tint(c, alphaK * MathF.Pow(1f - s, 1.1f));
            }
            Mb.Ribbon(Tail, TailN, TailHalf, TailCol, MathF.Max(0.8f * e.Px, 0.04f * w) * (layer == 0 ? 1f : 0.7f));
        }

        // A thin wet streak of light down it, off to the side the light falls from.
        if (e.Detailed)
        {
            for (int i = 0; i < TailPts; i++)
            {
                float s = i / (float)(TailPts - 1);
                float half = w * (0.20f * MathF.Pow(1f - s, 0.9f) + 0.02f);
                TailHalf[i] = MathF.Max(0.3f * e.Px, half * 0.10f);
                TailCol[i] = e.Tint(_palette.Sample(0.95f), 0.42f * (1f - s));
            }
            // shift the streak sideways within the cord
            Span<Vector2> shifted = stackalloc Vector2[TailPts];
            for (int i = 0; i < TailPts; i++)
            {
                float s = i / (float)(TailPts - 1);
                float half = w * (0.20f * MathF.Pow(1f - s, 0.9f) + 0.02f);
                shifted[i] = Tail[i] + TailN[i] * (0.38f * half);
            }
            Mb.Ribbon(shifted, TailN, TailHalf, TailCol, 0.5f * e.Px);
        }
    }
}

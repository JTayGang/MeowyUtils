using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Petrification: the screen ossifies from the edges inward. A dense, near-opaque crust of
/// irregular crystalline plates builds up along every edge (two overlapping layers - big base
/// plates with smaller detail plates on top, each with a lit rim toward an implied light source).
/// Bright hairline fractures radiate inward, and small faceted crystal chips drift near the border.
///
/// Strictly monochrome: every color in the palette has R == G == B (or within a hair of it), and
/// the darkening tint is a neutral dark gray rather than a cool one. Earlier versions pushed blue
/// above red and green across the whole palette, which read as a blue cast over the entire screen.
/// </summary>
public sealed class PetrificationEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Petrification;

    // ---- palette: strictly neutral grayscale ----
    // Every entry below is chosen so R, G, B are equal (or differ by <= 0.004, which is
    // invisible). Nothing here should read as "cool" or "warm" - the reference is neutral stone.
    private static readonly uint StoneDeep  = DrawHelpers.ToU32(0.007f, 0.007f, 0.008f, 1f); // deepest crevice plate
    private static readonly uint StoneBody  = DrawHelpers.ToU32(0.042f, 0.042f, 0.045f, 1f); // typical plate body
    private static readonly uint StoneMid   = DrawHelpers.ToU32(0.120f, 0.120f, 0.125f, 1f); // plate catching the light
    private static readonly uint StoneLit   = DrawHelpers.ToU32(0.250f, 0.250f, 0.258f, 1f); // brightest plate face
    private static readonly uint StoneRim   = DrawHelpers.ToU32(0.740f, 0.740f, 0.748f, 1f); // lit plate edge
    private static readonly uint StoneRimHi = DrawHelpers.ToU32(0.970f, 0.970f, 0.975f, 1f); // hottest glint on rims
    private static readonly uint CrackGlint = DrawHelpers.ToU32(0.870f, 0.870f, 0.878f, 1f); // hairline cracks
    private static readonly uint CrackDark  = DrawHelpers.ToU32(0.000f, 0.000f, 0.000f, 1f); // crack shadow under-stroke
    private static readonly uint ShardBody  = DrawHelpers.ToU32(0.145f, 0.145f, 0.150f, 1f); // shard facet dark
    private static readonly uint ShardLit   = DrawHelpers.ToU32(0.520f, 0.520f, 0.528f, 1f); // shard facet lit
    private static readonly uint ShardEdge  = DrawHelpers.ToU32(0.970f, 0.970f, 0.975f, 1f); // shard bright edge

    // Darkening tint - neutral dark gray, not the old bluish one. This is the single biggest
    // contributor to the cast, because it covers the entire screen and multiplies every plate.
    private static readonly uint DarkTint   = DrawHelpers.ToU32(0.030f, 0.030f, 0.032f, 1f);

    // Implied light direction (from upper-left), used to pick which plate edges get highlighted.
    private static readonly Vector2 LightDir = Vector2.Normalize(new Vector2(-0.7f, -0.7f));

    // =====================================================================================
    // Crust
    // =====================================================================================
    private const int   BasePlateCount   = 820;
    private const int   DetailPlateCount = 1150;

    private const float BaseDepthMin   = 0.006f;
    private const float BaseDepthMax   = 0.180f;
    private const float BaseRadiusMin  = 0.032f;
    private const float BaseRadiusMax  = 0.105f;

    private const float DetailDepthMin  = 0.006f;
    private const float DetailDepthMax  = 0.150f;
    private const float DetailRadiusMin = 0.011f;
    private const float DetailRadiusMax = 0.050f;

    // Corner weight: plates placed closer to a screen corner sit deeper, so the four corners
    // build up a thicker crust than the middle of each edge.
    private const float CornerBoostMax = 0.75f;  // up to +75% depth at the very corner
    private const float DepthHardCap   = 0.230f; // never place a plate deeper than this fraction

    private struct Plate
    {
        public byte  Edge;
        public float Along;
        public float Depth;
        public float Radius;
        public float Rotation;
        public int   Verts;
        public int   Seed;
        public float Alpha;
        public float ToneBias;
    }

    private readonly Plate[] _basePlates   = new Plate[BasePlateCount];
    private readonly Plate[] _detailPlates = new Plate[DetailPlateCount];

    // =====================================================================================
    // Fractures
    // =====================================================================================
    private const int FractureCount = 40;

    private struct Fracture
    {
        public byte  Edge;
        public float Along;
        public float Depth;
        public float Length;
        public float AngleJitter;
        public int   Segments;
        public int   Seed;
        public float Alpha;
        public float BranchChance;
    }

    private readonly Fracture[] _fractures = new Fracture[FractureCount];

    // =====================================================================================
    // Shards
    // =====================================================================================
    private const int ShardCount = 95;

    private struct Shard
    {
        public byte  Edge;
        public float Along;
        public float Depth;
        public float Radius;
        public float Rotation;
        public float DriftX, DriftY;
        public float PhaseX, PhaseY;
        public float FreqX,  FreqY;
        public int   Verts;
        public int   Seed;
        public float Alpha;
    }

    private readonly Shard[] _shards = new Shard[ShardCount];

    public PetrificationEffect()
    {
        // ---- bake base plates ----
        for (int i = 0; i < BasePlateCount; i++)
        {
            int s = unchecked(0x5E7B1000 + i * 7919);
            float along = DrawHelpers.HashRange(s + 1, -0.10f, 1.10f);

            float t = DrawHelpers.Hash01(s + 2);
            t *= t; // bias toward the outer edge so the rim is the densest part of the crust
            float cornerT = MathF.Abs(along - 0.5f) * 2f;
            float cornerBoost = 1f + CornerBoostMax * cornerT * cornerT;

            float depth = BaseDepthMin + (BaseDepthMax - BaseDepthMin) * t * cornerBoost;
            if (depth > DepthHardCap) depth = DepthHardCap;

            _basePlates[i] = new Plate
            {
                Edge     = (byte)(DrawHelpers.Hash01(s) * 4f),
                Along    = along,
                Depth    = depth,
                Radius   = DrawHelpers.HashRange(s + 3, BaseRadiusMin, BaseRadiusMax),
                Rotation = DrawHelpers.HashRange(s + 4, 0f, MathF.PI * 2f),
                Verts    = 4 + (int)(DrawHelpers.Hash01(s + 5) * 3f), // 4..6: angular, not round
                Seed     = s + 7,
                Alpha    = DrawHelpers.HashRange(s + 6, 0.94f, 1.00f),
                ToneBias = DrawHelpers.HashRange(s + 8, 0f, 1f),
            };
        }

        // ---- bake detail plates ----
        for (int i = 0; i < DetailPlateCount; i++)
        {
            int s = unchecked(0x5E2A1000 + i * 7919);
            float along = DrawHelpers.HashRange(s + 1, -0.06f, 1.06f);

            float t = DrawHelpers.Hash01(s + 2);
            t *= t;
            float cornerT = MathF.Abs(along - 0.5f) * 2f;
            float cornerBoost = 1f + (CornerBoostMax * 0.6f) * cornerT * cornerT;

            float depth = DetailDepthMin + (DetailDepthMax - DetailDepthMin) * t * cornerBoost;
            if (depth > DepthHardCap) depth = DepthHardCap;

            _detailPlates[i] = new Plate
            {
                Edge     = (byte)(DrawHelpers.Hash01(s) * 4f),
                Along    = along,
                Depth    = depth,
                Radius   = DrawHelpers.HashRange(s + 3, DetailRadiusMin, DetailRadiusMax),
                Rotation = DrawHelpers.HashRange(s + 4, 0f, MathF.PI * 2f),
                Verts    = 3 + (int)(DrawHelpers.Hash01(s + 5) * 3f), // 3..5: small sharp chips
                Seed     = s + 7,
                Alpha    = DrawHelpers.HashRange(s + 6, 0.75f, 0.98f),
                ToneBias = DrawHelpers.HashRange(s + 8, 0f, 1f),
            };
        }

        // ---- bake fractures ----
        for (int i = 0; i < FractureCount; i++)
        {
            int s = unchecked(0x5F4C1000 + i * 7919);
            _fractures[i] = new Fracture
            {
                Edge         = (byte)(DrawHelpers.Hash01(s) * 4f),
                Along        = DrawHelpers.HashRange(s + 1, 0f, 1f),
                Depth        = DrawHelpers.HashRange(s + 2, 0.02f, 0.15f),
                Length       = DrawHelpers.HashRange(s + 3, 0.05f, 0.22f),
                AngleJitter  = DrawHelpers.HashRange(s + 4, -0.65f, 0.65f),
                Segments     = 3 + (int)(DrawHelpers.Hash01(s + 5) * 4f),
                Seed         = s + 6,
                Alpha        = DrawHelpers.HashRange(s + 7, 0.18f, 0.48f),
                BranchChance = DrawHelpers.HashRange(s + 8, 0.10f, 0.40f),
            };
        }

        // ---- bake shards ----
        for (int i = 0; i < ShardCount; i++)
        {
            int s = unchecked(0x5A4D1000 + i * 7919);
            _shards[i] = new Shard
            {
                Edge     = (byte)(DrawHelpers.Hash01(s) * 4f),
                Along    = DrawHelpers.HashRange(s + 1, 0f, 1f),
                Depth    = DrawHelpers.HashRange(s + 2, 0.008f, 0.185f),
                Radius   = DrawHelpers.HashRange(s + 3, 0.0030f, 0.0110f),
                Rotation = DrawHelpers.HashRange(s + 4, 0f, MathF.PI * 2f),
                DriftX   = DrawHelpers.HashRange(s + 5, 0.003f, 0.012f),
                DriftY   = DrawHelpers.HashRange(s + 6, 0.003f, 0.012f),
                PhaseX   = DrawHelpers.HashRange(s + 7, 0f, MathF.PI * 2f),
                PhaseY   = DrawHelpers.HashRange(s + 8, 0f, MathF.PI * 2f),
                FreqX    = DrawHelpers.HashRange(s + 9, 0.06f, 0.18f),
                FreqY    = DrawHelpers.HashRange(s + 10, 0.06f, 0.18f),
                Verts    = 3 + (int)(DrawHelpers.Hash01(s + 11) * 3f),
                Seed     = s + 12,
                Alpha    = DrawHelpers.HashRange(s + 13, 0.55f, 0.95f),
            };
        }
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float breathe = 0.95f + 0.05f * DrawHelpers.Pulse(time, 4.2f);

        // ---- 1) darkening: neutral gray tint, still light enough to see the world through it ----
        dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
            DrawHelpers.WithAlpha(DarkTint, alpha * 0.24f));
        DrawHelpers.DrawVignette(dl, screenSize, DarkTint, 0.36f, alpha * 0.55f);

        // ---- 2) crust (base layer, then detail layer on top) ----
        DrawPlateLayer(dl, screenSize, shortSide, alpha, time, breathe, _basePlates,   baseLayer: true);
        DrawPlateLayer(dl, screenSize, shortSide, alpha, time, breathe, _detailPlates, baseLayer: false);

        // ---- 3) fractures ----
        DrawFractures(dl, screenSize, shortSide, alpha);

        // ---- 4) floating crystal shards ----
        DrawShards(dl, screenSize, shortSide, alpha, time);
    }

    // =====================================================================================
    // Crust
    // =====================================================================================

    private void DrawPlateLayer(
        ImDrawListPtr dl, Vector2 screenSize, float shortSide, float alpha, float time, float breathe,
        Plate[] plates, bool baseLayer)
    {
        Span<Vector2> pts = stackalloc Vector2[8];
        Span<Vector2> normals = stackalloc Vector2[8];

        float pulseAmp = baseLayer ? 0.03f : 0.07f;
        float rimBase  = baseLayer ? 0.90f : 1.00f;
        uint  rimCol   = baseLayer ? StoneRim : StoneRimHi;
        float rimWidth = baseLayer ? 2.2f : 1.6f;

        for (int i = 0; i < plates.Length; i++)
        {
            ref readonly var p = ref plates[i];

            Vector2 center = EdgePoint(screenSize, shortSide, p.Edge, p.Along, p.Depth);

            float scale = 1f + pulseAmp * MathF.Sin(time * (baseLayer ? 0.7f : 1.3f) + p.Seed * 0.11f);
            float radius = shortSide * p.Radius * scale;

            int n = Math.Clamp(p.Verts, 3, 7);

            Vector2 centroid = Vector2.Zero;
            for (int k = 0; k < n; k++)
            {
                float a  = p.Rotation + MathF.Tau * k / n;
                float rr = radius * DrawHelpers.HashRange(p.Seed + k * 13, 0.38f, 1.0f);
                pts[k] = center + new Vector2(MathF.Cos(a) * rr, MathF.Sin(a) * rr);
                centroid += pts[k];
            }
            centroid /= n;

            for (int k = 0; k < n; k++)
            {
                Vector2 mid = (pts[k] + pts[(k + 1) % n]) * 0.5f;
                Vector2 nrm = mid - centroid;
                float len = nrm.Length();
                normals[k] = len > 1e-4f ? nrm / len : Vector2.Zero;
            }

            float tone = DrawHelpers.Hash01(p.Seed + 101) + p.ToneBias;
            uint body;
            if (baseLayer)
                body = tone > 1.35f ? StoneMid
                     : tone > 0.75f ? StoneBody
                     : StoneDeep;
            else
                body = tone > 1.60f ? StoneLit
                     : tone > 1.15f ? StoneMid
                     : tone > 0.60f ? StoneBody
                     : StoneDeep;

            float aBody = alpha * p.Alpha * breathe;

            dl.AddConvexPolyFilled(ref pts[0], n, DrawHelpers.WithAlpha(body, aBody));

            for (int k = 0; k < n; k++)
            {
                float dot = Vector2.Dot(normals[k], LightDir);
                if (dot <= 0f) continue;

                float w = dot * dot;
                float a = aBody * rimBase * w;
                dl.AddLine(pts[k], pts[(k + 1) % n], DrawHelpers.WithAlpha(rimCol, a), rimWidth);
            }
        }
    }

    // =====================================================================================
    // Fractures
    // =====================================================================================

    private void DrawFractures(ImDrawListPtr dl, Vector2 screenSize, float shortSide, float alpha)
    {
        for (int i = 0; i < FractureCount; i++)
        {
            ref readonly var f = ref _fractures[i];

            Vector2 start = EdgePoint(screenSize, shortSide, f.Edge, f.Along, f.Depth);

            Vector2 inward = f.Edge switch
            {
                0 => new Vector2(0f,  1f),
                1 => new Vector2(-1f, 0f),
                2 => new Vector2(0f, -1f),
                _ => new Vector2(1f,  0f),
            };

            float ca = MathF.Cos(f.AngleJitter), sa = MathF.Sin(f.AngleJitter);
            Vector2 dir = new(inward.X * ca - inward.Y * sa, inward.X * sa + inward.Y * ca);

            float segLen = shortSide * f.Length / f.Segments;

            uint core = DrawHelpers.WithAlpha(CrackGlint, alpha * f.Alpha * 0.85f);
            uint glow = DrawHelpers.WithAlpha(CrackGlint, alpha * f.Alpha * 0.18f);
            uint dark = DrawHelpers.WithAlpha(CrackDark,  alpha * f.Alpha * 0.90f);

            Vector2 cursor = start;
            for (int k = 0; k < f.Segments; k++)
            {
                float j = DrawHelpers.HashRange(f.Seed + k, -0.55f, 0.55f);
                float cj = MathF.Cos(j), sj = MathF.Sin(j);
                Vector2 stepDir = new(dir.X * cj - dir.Y * sj, dir.X * sj + dir.Y * cj);
                Vector2 next = cursor + stepDir * segLen;

                float t = (float)k / f.Segments;
                float darkW = 4.0f - 2.0f * t;
                float glowW = 2.4f - 0.9f * t;
                float coreW = 1.3f - 0.5f * t;

                dl.AddLine(cursor, next, dark, darkW);
                Vector2 litOffset = LightDir * 0.8f;
                dl.AddLine(cursor + litOffset, next + litOffset, glow, glowW);
                dl.AddLine(cursor + litOffset, next + litOffset, core, coreW);

                if (k < f.Segments - 1 && DrawHelpers.Hash01(f.Seed + k * 31 + 7) < f.BranchChance)
                {
                    float bs = (DrawHelpers.Hash01(f.Seed + k * 31 + 3) < 0.5f ? 1f : -1f)
                             * DrawHelpers.HashRange(f.Seed + k * 31 + 5, 0.9f, 1.4f);
                    float bca = MathF.Cos(bs), bsa = MathF.Sin(bs);
                    Vector2 bDir = new(stepDir.X * bca - stepDir.Y * bsa, stepDir.X * bsa + stepDir.Y * bca);
                    Vector2 bend = next + bDir * segLen * 0.6f;

                    uint darkB = DrawHelpers.WithAlpha(CrackDark,  alpha * f.Alpha * 0.90f * 0.7f);
                    uint glowB = DrawHelpers.WithAlpha(CrackGlint, alpha * f.Alpha * 0.18f * 0.8f);
                    uint coreB = DrawHelpers.WithAlpha(CrackGlint, alpha * f.Alpha * 0.85f * 0.9f);

                    dl.AddLine(next, bend, darkB, darkW * 0.7f);
                    dl.AddLine(next + litOffset, bend + litOffset, glowB, glowW * 0.7f);
                    dl.AddLine(next + litOffset, bend + litOffset, coreB, coreW * 0.8f);
                }

                cursor = next;
            }
        }
    }

    // =====================================================================================
    // Shards (faceted crystals)
    // =====================================================================================

    private void DrawShards(ImDrawListPtr dl, Vector2 screenSize, float shortSide, float alpha, float time)
    {
        Span<Vector2> ring = stackalloc Vector2[6];

        for (int i = 0; i < ShardCount; i++)
        {
            ref readonly var s = ref _shards[i];

            Vector2 basePos = EdgePoint(screenSize, shortSide, s.Edge, s.Along, s.Depth);
            float dx = MathF.Sin(time * s.FreqX + s.PhaseX) * shortSide * s.DriftX;
            float dy = MathF.Cos(time * s.FreqY + s.PhaseY) * shortSide * s.DriftY;
            Vector2 center = basePos + new Vector2(dx, dy);

            float radius = shortSide * s.Radius;
            float tw = 0.75f + 0.25f * MathF.Sin(time * 1.7f + s.Seed * 0.31f);
            float a = alpha * s.Alpha * tw;
            if (a < 0.004f) continue;

            int n = Math.Clamp(s.Verts, 3, 5);

            for (int k = 0; k < n; k++)
            {
                float ang = s.Rotation + MathF.Tau * k / n;
                float rr  = radius * DrawHelpers.HashRange(s.Seed + k * 17, 0.42f, 1.0f);
                ring[k] = center + new Vector2(MathF.Cos(ang) * rr, MathF.Sin(ang) * rr);
            }

            dl.AddCircleFilled(center, radius * 2.6f, DrawHelpers.WithAlpha(CrackGlint, a * 0.09f));

            int fanOffset = (int)(s.Seed & 1);
            for (int k = 0; k < n; k++)
            {
                Vector2 a0 = ring[k];
                Vector2 a1 = ring[(k + 1) % n];
                bool lit = ((k + fanOffset) & 1) == 0;
                uint fill = lit ? ShardLit : ShardBody;
                float fillA = lit ? 0.62f : 0.90f;
                dl.AddTriangleFilled(a0, a1, center, DrawHelpers.WithAlpha(fill, a * fillA));
            }

            Vector2 centroid = center;
            for (int k = 0; k < n; k++)
            {
                Vector2 a0 = ring[k];
                Vector2 a1 = ring[(k + 1) % n];
                Vector2 mid = (a0 + a1) * 0.5f;
                Vector2 nrm = mid - centroid;
                float len = nrm.Length();
                float dot = len > 1e-4f ? Vector2.Dot(nrm / len, LightDir) : 0f;
                float w = dot > 0f ? 0.35f + 0.65f * dot : 0.35f;
                dl.AddLine(a0, a1, DrawHelpers.WithAlpha(ShardEdge, a * w), 1.0f);
            }

            Vector2 hi = center + LightDir * radius * 0.35f;
            dl.AddCircleFilled(hi, MathF.Max(0.8f, radius * 0.18f),
                               DrawHelpers.WithAlpha(ShardEdge, a * 0.95f));
        }
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private static Vector2 EdgePoint(Vector2 screenSize, float shortSide, byte edge, float along, float depthFrac)
    {
        float depthPx = shortSide * depthFrac;
        return edge switch
        {
            0 => new Vector2(along * screenSize.X,            depthPx),
            1 => new Vector2(screenSize.X - depthPx,          along * screenSize.Y),
            2 => new Vector2(along * screenSize.X,            screenSize.Y - depthPx),
            _ => new Vector2(depthPx,                         along * screenSize.Y),
        };
    }
}
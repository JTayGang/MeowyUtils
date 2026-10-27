using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>Everything RegionTunnel draws for one frame (built by HysteriaEffect, passed via RegionPrimitive.State).</summary>
internal sealed class TunnelField
{
    // The room the effect and material agree on, as fractions of the short side at the focal plane.

    /// <summary>The bare wall.</summary>
    public const float WallRadius = 0.90f;

    /// <summary>The inside lip of a rib: it stands out from the wall by the difference.</summary>
    public const float RibRadius = 0.78f;

    public const int MaxRibs = 12, DustCount = 150, BillowCount = 9, GritCount = 520, CrackCount = 8;

    // set once per cast

    public int Seed;

    /// <summary>Where each speck of dust and each billow of haze starts along the tunnel (TunnelFlight offsets).</summary>
    public readonly float[] DustAt = new float[DustCount], BillowAt = new float[BillowCount], GritAt = new float[GritCount], CrackAt = new float[CrackCount];

    /// <summary>A new cast: lays dust and haze out evenly along a tunnel of this length, so it's never empty or bunched.</summary>
    public void Begin(int seed, float tunnelLength)
    {
        Seed = seed;
        _bendPhase = DrawHelpers.Hash01(seed + 14) * MathF.Tau;
        _bendDir = DrawHelpers.Sign(seed + 15);
        for (int i = 0; i < DustCount; i++) DustAt[i] = tunnelLength * (i + DrawHelpers.Hash01(seed + 600 + i * 17)) / DustCount;
        for (int i = 0; i < BillowCount; i++) BillowAt[i] = tunnelLength * (i + DrawHelpers.Hash01(seed + 700 + i * 17)) / BillowCount;
        for (int i = 0; i < GritCount; i++) GritAt[i] = tunnelLength * (i + DrawHelpers.Hash01(seed + 800 + i * 17)) / GritCount;
        for (int i = 0; i < CrackCount; i++) CrackAt[i] = tunnelLength * (i + DrawHelpers.Hash01(seed + 900 + i * 17)) / CrackCount;
    }

    /// <summary>Where each rib starts along the tunnel (TunnelFlight offsets); the effect's rings ride the same ones.</summary>
    public float[] RibAt = Array.Empty<float>();

    public readonly WallTexture Wall = new();

    // set every frame

    public TunnelFlight Flight = null!;
    public LayerCamera Cam;
    public Vector2 Size;
    public float Px, Short, Time, Age;

    /// <summary>The effect's own twist per px of height and roll of the whole view, so the walls turn with everything else.</summary>
    public float Swirl, Roll;

    /// <summary>The walls breathe with the heart: 1 is at rest, a little under 1 is a beat; rings and runes follow.</summary>
    public float Breath = 1f;

    /// <summary>The fall, in px/s at 1080p (0 = hanging in the air).</summary>
    public float FlightSpeed;

    /// <summary>The cast's fade in and out, and how far the tunnel has materialised out of the dark (0..1).</summary>
    public float Alpha, Appear;

    /// <summary>Heartbeat pulse (0..1), how frightened it all is (0..1), and the apparition's envelope, kick and ghosting.</summary>
    public float Beat, Stress, Env, Kick, Ghost;

    /// <summary>What is being shown: 0 none, 1 stare, 2 great eye, 3 gate, 4 surge. And how long it has been (seconds).</summary>
    public int Vision;
    public float VisionAge;

    /// <summary>Stagger of the red and cyan fringes at the edge of the lens, in px at 1080p.</summary>
    public float Fringe = 1.2f;

    public Vector2 Center => Size * 0.5f;

    private float _bendPhase, _bendDir = 1f;

    /// <summary>The inside lip of a rib, in px, breathing.</summary>
    public float RibPx => RibRadius * Short * Breath;

    /// <summary>Where the tunnel's middle is at this height (px at 1080p): add it to plane positions before projecting.</summary>
    public Vector2 Bend(float hu)
    {
        if (Flight == null) return default;
        float s = Math.Clamp((Flight.NearHeight - hu) / Flight.Length, 0f, 1.25f);     // 0 at the lens, 1 at the far end
        float phi = _bendPhase + Roll + _bendDir * 0.10f * Time;
        float reach = Short * (0.30f * s * s);
        float sway = Short * 0.05f * s * MathF.Sin(2.4f * s + 0.33f * Time + _bendPhase * 3f);
        Vector2 d = new(MathF.Cos(phi), MathF.Sin(phi));
        return d * reach + new Vector2(-d.Y, d.X) * sway;
    }

    /// <summary>Tileable wall grime: an (angle, length) texture of mottle, streak and pitting (0..1); rebuilt when the seed changes.</summary>
    internal sealed class WallTexture
    {
        public const int Around = 192, Along = 128;
        private readonly Vector3[] _t = new Vector3[Around * Along];
        private int _seed = int.MinValue;

        public void Ensure(int seed)
        {
            if (seed == _seed) return;
            _seed = seed;
            for (int y = 0; y < Along; y++)
            {
                for (int x = 0; x < Around; x++)
                {
                    float u = x / (float)Around, v = y / (float)Along;

                    // Blotches of damp and soot, three octaves; the lattice wraps round the wall (14) and once a lap (10).
                    float mottle = 0.5f + 0.5f * Fbm(u * 14f, v * 10f, 14, 10, 4, seed + 1);

                    // Stains run down the wall: stretched along the tunnel, fine across it.
                    float streak = 0.5f + 0.5f * Fbm(u * 44f, v * 4f, 44, 4, 3, seed + 2);
                    streak = DrawHelpers.Smooth((streak - 0.50f) / 0.28f);

                    float pit = 0.5f + 0.5f * Fbm(u * 64f, v * 48f, 64, 48, 2, seed + 3);
                    _t[y * Around + x] = new Vector3(mottle, streak, pit);
                }
            }
        }

        /// <summary>Bilinear sample; both coordinates wrap (turns round the wall, laps along the tunnel).</summary>
        public Vector3 Sample(float turns, float laps)
        {
            float fx = (turns - MathF.Floor(turns)) * Around, fy = (laps - MathF.Floor(laps)) * Along;
            int x0 = (int)fx, y0 = (int)fy;
            float tx = fx - x0, ty = fy - y0;
            int x1 = x0 + 1 == Around ? 0 : x0 + 1, y1 = y0 + 1 == Along ? 0 : y0 + 1;
            Vector3 a = _t[y0 * Around + x0], b = _t[y0 * Around + x1], c = _t[y1 * Around + x0], d = _t[y1 * Around + x1];
            return Vector3.Lerp(Vector3.Lerp(a, b, tx), Vector3.Lerp(c, d, tx), ty);
        }

        // Value noise on a lattice that repeats every px by py cells, so the texture tiles.
        private static float Tile(float x, float y, int px, int py, int salt)
        {
            float fx = MathF.Floor(x), fy = MathF.Floor(y);
            int xi = (int)fx, yi = (int)fy;
            float xf = x - fx, yf = y - fy;
            float u = xf * xf * xf * (xf * (xf * 6f - 15f) + 10f), v = yf * yf * yf * (yf * (yf * 6f - 15f) + 10f);
            int x0 = Mod(xi, px), x1 = Mod(xi + 1, px), y0 = Mod(yi, py), y1 = Mod(yi + 1, py);
            float a = Cell(x0, y0, salt), b = Cell(x1, y0, salt), c = Cell(x0, y1, salt), d = Cell(x1, y1, salt);
            float top = a + u * (b - a), bot = c + u * (d - c);
            return (top + v * (bot - top)) * 2f - 1f;
        }

        private static float Cell(int x, int y, int salt) => DrawHelpers.Hash01(x * 7919 + y * 104729 + salt * 6151);
        private static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

        private static float Fbm(float x, float y, int px, int py, int octaves, int salt)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Tile(x, y, px, py, salt + i * 13);
                norm += amp;
                x *= 2f; y *= 2f; px *= 2; py *= 2; amp *= 0.5f;
            }
            return sum / norm;
        }
    }
}

/// <summary>Faux-3D flight down a tunnel: a thing's height (px, as in LayerCamera) is its offset plus distance flown, wrapped.</summary>
internal sealed class TunnelFlight
{
    public readonly float FarHeight, NearHeight;

    /// <summary>Distance flown so far, px. Advance it each frame.</summary>
    public float Travel;

    public TunnelFlight(float farHeight, float nearHeight)
    {
        FarHeight = farHeight;
        NearHeight = nearHeight;
    }

    public float Length => NearHeight - FarHeight;

    public void Advance(float dt, float speed) => Travel += speed * dt;

    /// <summary>Where a thing at this offset is now, and which lap it is on (changes each time it comes round).</summary>
    public float HeightOf(float offset, out int lap)
    {
        float p = (offset + Travel) / Length;
        float whole = MathF.Floor(p);
        lap = (int)whole;
        return FarHeight + (p - whole) * Length;
    }

    /// <summary>0 at the far end of the tunnel, 1 at the lens.</summary>
    public float Near(float height) => DrawHelpers.Saturate((height - FarHeight) / Length);
}

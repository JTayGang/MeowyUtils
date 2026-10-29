using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A stream as a chain of launched parcels, each flying its own parabola: it thins as it speeds up, ripples grow until it pinches into beads (a thick liquid stays a rope), and a fast one sheds spray.</summary>
public sealed class LiquidJet
{
    internal const int Cap = 192, Bead = 7;  // parcels kept; parcels per bead (one ripple wavelength)

    public float X, Y;  // source, px
    public float Speed;  // launch speed, px/s: may change every frame (pressure)
    public float Angle;  // launch direction in radians from straight down, positive toward +x: may drift every frame
    public float Radius;  // px at Flow = 1
    public float Flow;  // volume flux multiplier, set every frame; 0 shuts it off
    public float Churn = 1f;  // how much it writhes, lumps and sheds
    public int Seed;

    /// <summary>Shut off and fully drained: free to launch again.</summary>
    public bool Idle => _hi == _lo && Flow <= 0f;

    private readonly float[] _t0 = new float[Cap], _r = new float[Cap], _vx = new float[Cap], _vy = new float[Cap], _sp0 = new float[Cap];
    private readonly float[] _ex = new float[Cap], _ey = new float[Cap], _er = new float[Cap], _evx = new float[Cap], _evy = new float[Cap];  // scratch: the parcels, oldest first
    private long _lo, _hi;
    private float _next, _dtau, _prevFlow, _shed;

    // the path as drawn, top to bottom
    internal readonly Vector2[] Path = new Vector2[Cap + 8], PathV = new Vector2[Cap + 8];
    internal readonly float[] Rad = new float[Cap + 8], Born = new float[Cap + 8];
    internal int Rows;

    // where it meets the water
    internal bool Wet;
    internal Vector2 Hit;
    internal float HitR, HitSpeed;

    /// <summary>Launch-time jump between neighbouring rows that means the flow was off between them.</summary>
    internal float GapTime => 2.5f * _dtau;

    private static readonly float[] HeadAngles = { 0.45f, 0.90f, 1.25f, 1.52f };

    private static readonly float[] BeadCos = BuildBeadCos();
    private static float[] BuildBeadCos()
    {
        var t = new float[Bead];
        for (int j = 0; j < Bead; j++) t[j] = MathF.Cos(MathF.Tau * j / Bead);
        return t;
    }

    internal void Reset()
    {
        _lo = _hi = 0; _next = 0f; _prevFlow = 0f; _shed = 0f; Rows = 0; Wet = false; Flow = 0f;
    }

    /// <summary>Starts a stream here: it must be Idle.</summary>
    internal void Start(LiquidField f)
    {
        Reset();
        _next = f.Time;
        Retune(f);
    }

    /// <summary>Sets the bead spacing from the nominal radius and speed.</summary>
    internal void Retune(LiquidField f)
    {
        var ph = f.Liquid.Phys;
        float sigma = ph.Breakup * MathF.Pow(8f * f.Px / MathF.Max(Radius * 0.7f, f.Px), 1.5f);
        float ab = sigma > 0.1f ? Math.Clamp(3f / sigma, 0.1f, 1.2f) : 0.6f;  // when the ripples would close the neck
        float vb = MathF.Sqrt(MathF.Pow(Speed * MathF.Sin(Angle), 2f) + MathF.Pow(Speed * MathF.Cos(Angle) + f.Gravity * ab, 2f));
        float rb = Radius * MathF.Sqrt(MathF.Max(Speed, 1f) / MathF.Max(vb, 1f));
        _dtau = Math.Clamp(9f * rb / MathF.Max(vb, 1f), 0.055f, 0.25f) / Bead;
    }

    private struct Parcel { public float X, Y, R, Vx, Vy, E; }

    private float Eps(long i, float rLaunch, float rLoc, float a, LiquidField f)
    {
        var ph = f.Liquid.Phys;
        float e0 = ph.Ripple * (1f + 0.6f * Churn) * (0.55f + 0.9f * DrawHelpers.Hash01(Seed + (int)(i / Bead) * 31));
        // stretching slows the ripples' growth as thinning speeds it up, so the stream's own radius is a mean of where it began and where it is
        float rEff = MathF.Sqrt(MathF.Max(rLaunch, 0.5f * f.Px) * MathF.Max(rLoc, 0.5f * f.Px));
        float sigma = ph.Breakup * MathF.Pow(8f * f.Px / rEff, 1.5f);
        return MathF.Min(1f, e0 * MathF.Exp(MathF.Min(sigma * a, 30f)));
    }

    private Parcel Eval(long i, float T, LiquidField f)
    {
        int s = (int)(i % Cap);
        float t0 = _t0[s], a = MathF.Max(T - t0, 0f), g = f.Gravity;
        float vx = _vx[s], vy = _vy[s] + g * a;
        float sp = MathF.Max(MathF.Sqrt(vx * vx + vy * vy), 1f);

        // it writhes across its own direction of travel
        float churn = Churn * f.Liquid.Phys.Churn;
        float writhe = churn * f.Px * 9f * (a / MathF.Sqrt(a + 0.15f))
                     * (MathF.Sin(t0 * 10.7f + Seed * 0.91f) + 0.5f * MathF.Sin(t0 * 24.5f + Seed * 1.7f));

        float rLoc = _r[s] * MathF.Sqrt(_sp0[s] / sp);  // continuity: it thins as it speeds up
        rLoc *= MathF.Max(0.55f, 1f + 0.20f * churn * Noise.Value(t0 * 6.3f + Seed * 0.7f, 3.1f));  // lumps, fixed to the parcel so they stretch as it speeds up
        float e = Eps(i, _r[s], rLoc, a, f);
        float m = 1f - e * BeadCos[(int)(i % Bead)];  // the cross-section's ripple; zero is a closed neck
        return new Parcel
        {
            E = e,
            X = X + vx * a + writhe * (vy / sp),
            Y = Y + _vy[s] * a + 0.5f * g * a * a - writhe * (vx / sp),
            R = rLoc * MathF.Sqrt(MathF.Max(m, 0f)),
            Vx = vx, Vy = vy,
        };
    }

    internal void Step(LiquidField f, float T, float dt)
    {
        // launch whatever fell due since the last frame; nothing is launched while the flow is off
        float vx0 = Speed * MathF.Sin(Angle), vy0 = Speed * MathF.Cos(Angle);
        for (int guard = 0; _next <= T && guard < 64; guard++)
        {
            float k = dt > 1e-5f ? Math.Clamp((_next - (T - dt)) / dt, 0f, 1f) : 1f;
            float flow = _prevFlow + (Flow - _prevFlow) * k;
            if (flow > 0.002f)
            {
                int s = (int)(_hi % Cap);
                _t0[s] = _next;
                _r[s] = Radius * MathF.Sqrt(flow);
                _vx[s] = vx0; _vy[s] = vy0; _sp0[s] = MathF.Max(Speed, 1f);
                _hi++;
            }
            _next += _dtau;
        }
        if (_next < T) _next = T;
        _prevFlow = Flow;
        if (_hi - _lo > Cap) _lo = _hi - Cap;

        // parcels launched while the flow was off, or long since gone, are not the front of the stream
        while (_hi > _lo && (_r[(int)(_lo % Cap)] < 0.2f * f.Px || T - _t0[(int)(_lo % Cap)] > 3.5f)) _lo++;

        int n = Gather(f, T, out int first);

        // a bead lets go when the neck behind it has closed
        while (_hi - _lo > 2 * Bead)
        {
            long next = (_lo / Bead + 1) * Bead;
            if (next >= _hi || Eval(next, T, f).E < 1f) break;
            PinchOff(f, _lo, next, T);
            _lo = next;
            n = Gather(f, T, out first);
        }

        BuildPath(f, n, first);
        Shed(f, dt);
    }

    /// <summary>Evaluates the live parcels, oldest first; those that reached the water are retired, bar the last (it locates the crossing).</summary>
    private int Gather(LiquidField f, float T, out int first)
    {
        int n = (int)(_hi - _lo);
        for (int k = 0; k < n; k++)
        {
            var p = Eval(_lo + k, T, f);
            _ex[k] = p.X; _ey[k] = p.Y; _er[k] = p.R; _evx[k] = p.Vx; _evy[k] = p.Vy;
        }
        first = 0;
        while (first < n && _ey[first] >= f.SurfaceY(_ex[first])) first++;
        if (first >= n) { _lo += n; n = 0; first = 0; }  // all of it is in the water
        else if (first > 1)
        {
            int drop = first - 1;
            _lo += drop; n -= drop; first = 1;
            Array.Copy(_ex, drop, _ex, 0, n); Array.Copy(_ey, drop, _ey, 0, n); Array.Copy(_er, drop, _er, 0, n);
            Array.Copy(_evx, drop, _evx, 0, n); Array.Copy(_evy, drop, _evy, 0, n);
        }
        return n;
    }

    /// <summary>Gathers parcels lo..hi into one drop, sometimes with a satellite; a fast thin stream scatters as it breaks.</summary>
    private void PinchOff(LiquidField f, long lo, long hi, float T)
    {
        float vol = 0f, cx = 0f, cy = 0f, cvx = 0f, cvy = 0f;
        for (long i = lo; i < hi; i++)
        {
            var p = Eval(i, T, f);
            float w = MathF.PI * p.R * p.R * MathF.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy) * _dtau;
            vol += w; cx += p.X * w; cy += p.Y * w; cvx += p.Vx * w; cvy += p.Vy * w;
        }
        if (vol < 1e-3f) return;
        float r = MathF.Cbrt(3f * vol / (4f * MathF.PI));
        if (r < 0.45f * f.Px) return;
        var ph = f.Liquid.Phys;
        var at = new Vector2(cx / vol, cy / vol);
        var v = new Vector2(cvx / vol, cvy / vol);
        float sp = v.Length();
        Vector2 dir = sp > 1f ? v / sp : Vector2.UnitY, perp = new(-dir.Y, dir.X);

        // pressure atomises: the faster and thinner it is, the wider the drops scatter
        float scatter = 0.05f * ph.Churn * Churn * MathF.Min(1.6f, _sp0[(int)(lo % Cap)] / (900f * f.Px)) * (f.Px * 4f / MathF.Max(r, f.Px));
        v += perp * (sp * scatter * f.Rand(-1f, 1f));
        f.SpawnDrop(at, v, r, ph.Drag * 0.3f, Seed + (int)lo);

        if (DrawHelpers.Hash01(Seed + (int)lo * 7 + 3) < ph.Satellites)
            f.SpawnDrop(at - dir * (r * 2.6f), v * 0.97f + perp * (sp * scatter * f.Rand(-1f, 1f)), r * DrawHelpers.HashRange(Seed + (int)lo * 5, 0.18f, 0.32f), ph.Drag * 0.3f, Seed + (int)lo + 1);
    }

    private static bool Off(LiquidField f, float x, float y, float r)
    {
        float m = r + 24f * f.Px;
        return y < -m || x < -m || x > f.Size.X + m;
    }

    private void BuildPath(LiquidField f, int n, int first)
    {
        Rows = 0; Wet = first > 0;
        if (first >= n) { Wet = false; return; }  // nothing left above the water
        float px = f.Px, thin = 0.35f * px;

        // top: the newest parcels still too thin to show are skipped, but one is kept so the tail tapers to nothing
        int top = n - 1;
        while (top > first && _er[top] < thin) top--;
        bool tapers = top < n - 1;
        if (tapers) top++;

        // and those still off the screen, bar the last
        while (top > first + 1 && Off(f, _ex[top - 1], _ey[top - 1], _er[top - 1])) top--;

        float sy0 = Wet ? f.SurfaceY(_ex[first]) : float.MaxValue;
        for (int k = top; k >= first && k >= 0; k--)
        {
            Path[Rows] = new Vector2(_ex[k], _ey[k]);
            PathV[Rows] = new Vector2(_evx[k], _evy[k]);
            // water spreads where it leaves the lip and where it meets the pool
            float edge = MathF.Max(MathF.Min(_ey[k], MathF.Min(_ex[k], f.Size.X - _ex[k])), 0f);
            float flare = 1f + 0.35f * DrawHelpers.Smooth(1f - edge / (46f * px)) + 0.30f * DrawHelpers.Smooth(1f - (sy0 - _ey[k]) / (34f * px));
            Rad[Rows] = k == top && tapers ? 0f : _er[k] * flare;
            Born[Rows] = _t0[(int)((_lo + k) % Cap)];
            Rows++;
        }

        if (!Wet && Rows >= 2 && Rad[Rows - 1] > 0.8f * px)
        {
            // a front still in the air ends in a rounded head, not a cut
            Vector2 d = Vector2.Normalize(Path[Rows - 1] - Path[Rows - 2]);
            Vector2 tip = Path[Rows - 1], tv = PathV[Rows - 1]; float r = Rad[Rows - 1], born = Born[Rows - 1];
            foreach (float a in HeadAngles)
            {
                Path[Rows] = tip + d * (r * MathF.Sin(a)); PathV[Rows] = tv; Rad[Rows] = r * MathF.Cos(a); Born[Rows] = born; Rows++;
            }
        }

        if (Wet)
        {
            // the last row is moved up onto the water
            float sy = f.SurfaceY(_ex[first]);
            float span = _ey[0] - _ey[first];
            float t = span > 1e-3f ? Math.Clamp((sy - _ey[first]) / span, 0f, 1f) : 0f;
            var cross = new Vector2(_ex[first] + (_ex[0] - _ex[first]) * t, sy);
            float rc = _er[first] + (_er[0] - _er[first]) * t;
            if (Rows < Path.Length) { Path[Rows] = cross; PathV[Rows] = PathV[Rows - 1]; Rad[Rows] = rc; Born[Rows] = Born[Math.Max(Rows - 1, 0)]; Rows++; }
            Hit = cross; HitR = rc;
            HitSpeed = MathF.Sqrt(_evx[0] * _evx[0] + _evy[0] * _evy[0]);
            if (Rows < 2 || rc < 0.4f * px) Wet = false;
        }
    }

    /// <summary>A fast, churning stream strips fine droplets off its surface.</summary>
    private void Shed(LiquidField f, float dt)
    {
        if (Rows < 4 || Flow < 0.05f) return;
        var ph = f.Liquid.Phys;
        float px = f.Px;
        int mid = Rows / 2;
        float sp = PathV[mid].Length();
        _shed += ph.Splash * Churn * MathF.Min(Rad[mid] / (5f * px), 3f) * 10f * MathF.Min(sp / (1000f * px), 1.5f) * dt;
        if (_shed > 4f) _shed = 0f;
        for (; _shed >= 1f; _shed -= 1f)
        {
            int k = 1 + (int)(f.Rand() * (Rows - 2));
            float rr = Rad[k];
            if (rr < 2f * px) continue;
            Vector2 dv = Path[k + 1] - Path[k - 1];
            if (dv.LengthSquared() < 1e-4f) continue;
            Vector2 d = Vector2.Normalize(dv), perp = new(-d.Y, d.X);
            float side = f.Rand() < 0.5f ? -1f : 1f;
            Vector2 v = PathV[k] + perp * (side * f.Rand(30f, 220f) * px) + new Vector2(f.Rand(-30f, 30f), f.Rand(-30f, 30f)) * px;
            f.SpawnDrop(Path[k] + perp * (side * rr), v, MathF.Max(0.55f * px, rr * f.Rand(0.04f, 0.14f)), ph.Drag * 0.3f, Seed + k * 31 + (int)(f.Time * 113f), life: 2.4f);
        }
    }
}

/// <summary>A ceiling drip site.</summary>
public sealed class LiquidSeep
{
    public float X, Y;  // where it clings, px
    public float Rate = 1f;  // how fast it fills: 1 is the liquid's own pace
    public float Boost = 1f;  // a temporary multiplier on Rate
    public float Scale = 1f;  // drop size
    public bool Live;  // off: it fades away
    internal float Phase, Cycle = 1f, Fade;
    internal int Cycles, Seed;
}

/// <summary>Jets and seeps feeding a wave-equation pool, with drops, splashes, bubbles, foam and lens beads; the liquid's physics drive all of it.</summary>
public sealed class LiquidField
{
    private const int MaxDrops = 360, MaxBubbles = 64, MaxHits = 48, MaxRebounds = 12, MaxBeads = 44, MaxTrails = 56, TrailPoints = 26;
    private const float MaxStep = 0.05f;

    public Vector2 Size { get; private set; }
    public float Px { get; private set; }
    public float Time { get; private set; }
    public Liquid Liquid { get; private set; } = Liquid.Water;
    public Vector4? Color;

    public float Level;  // rest depth of the pool, px
    public float Chop;  // extra choppiness on the surface and spatter on the lens, 0..

    public IReadOnlyList<LiquidJet> Jets => _jets;
    public IReadOnlyList<LiquidSeep> Seeps => _seeps;

    internal float Gravity;

    private readonly List<LiquidJet> _jets = new();
    private readonly List<LiquidSeep> _seeps = new();
    private readonly DropPool _drops = new(MaxDrops);
    private DebuffKind _owner;
    private int _seed;
    private uint _rng;

    // the pool
    private int _n;
    private float _dx;
    internal float[] H = Array.Empty<float>(), Vel = Array.Empty<float>(), Foam = Array.Empty<float>();

    // bubbles under the surface
    internal struct Bubble { public Vector2 P; public float R, Phase, Age; public Vector2 V; }
    internal readonly Bubble[] Bubbles = new Bubble[MaxBubbles];
    internal int BubbleCount;

    // beads clinging to the lens, and the wet trails they leave
    private struct Bead { public Vector2 P; public float R, Speed, Burst, Heading, Wait, Age, Life; public int Seed, Trail; public bool Slides; }
    internal struct Trail { public Vector3[] Pts; public int Count; public float Fade; public bool Live; }
    private readonly Bead[] _beads = new Bead[MaxBeads];
    internal readonly Trail[] Trails = new Trail[MaxTrails];
    private int _beadCount;
    private float _spatter;

    private struct Impact { public float X, V, R; }
    private readonly Impact[] _splats = new Impact[MaxHits];
    private int _splatCount;

    private struct Rebound { public float At, X, V, R; }
    private readonly Rebound[] _rebounds = new Rebound[MaxRebounds];
    private int _reboundCount;

    private float _spray, _fizz, _ripple;  // fractional droplets, bubbles and ripples owed to a jet in contact

    public LiquidField()
    {
        for (int i = 0; i < Trails.Length; i++) Trails[i].Pts = new Vector3[TrailPoints];
    }

    /// <summary>Starts a new cast with this many stream and drip slots, all idle (call again on resize).</summary>
    public void Begin(DebuffKind owner, Vector2 size, Liquid liquid, int seed, int jets, int seeps)
    {
        _owner = owner; Size = size; Px = DrawHelpers.PixelScale(size); Liquid = liquid; _seed = seed; _rng = (uint)seed | 1u;
        Time = 0f; Level = 0f; Chop = 0f;
        _drops.Count = 0; BubbleCount = 0; _splatCount = _reboundCount = _beadCount = 0; _spray = _fizz = _ripple = _spatter = 0f;
        for (int i = 0; i < Trails.Length; i++) { Trails[i].Count = 0; Trails[i].Fade = 0f; Trails[i].Live = false; }

        while (_jets.Count < jets) _jets.Add(new LiquidJet());
        while (_seeps.Count < seeps) _seeps.Add(new LiquidSeep());
        foreach (var j in _jets) j.Reset();
        foreach (var s in _seeps) { s.Live = false; s.Fade = 0f; }

        _n = Math.Clamp((int)MathF.Round(size.X / (7f * Px)), 96, 420);
        _dx = size.X / (_n - 1);
        if (H.Length != _n) { H = new float[_n]; Vel = new float[_n]; Foam = new float[_n]; }
        else { Array.Clear(H); Array.Clear(Vel); Array.Clear(Foam); }
        Gravity = liquid.Phys.Gravity * Px;
    }

    /// <summary>Starts a stream from at (px): speed in px/s at 1080p, angle in radians from straight down (positive toward +x), radius in px at 1080p. The jet must be Idle.</summary>
    public void Launch(LiquidJet j, Vector2 at, float speed, float angle, float radius, int seed)
    {
        j.X = at.X; j.Y = at.Y; j.Speed = speed * Px; j.Angle = angle; j.Radius = radius * Px; j.Seed = seed;
        j.Start(this);
    }

    /// <summary>Hangs a drip at at (px) and lets it fill.</summary>
    public void Place(LiquidSeep s, Vector2 at, float scale, float rate, int seed)
    {
        var ph = Liquid.Phys;
        s.X = at.X; s.Y = at.Y; s.Scale = scale; s.Rate = rate; s.Seed = seed; s.Cycles = 0; s.Live = true;
        s.Cycle = DrawHelpers.HashRange(seed + 1, ph.CycleMin, ph.CycleMax);
        s.Phase = DrawHelpers.Hash01(seed + 3) * GoopEmitter.PinchAt * 0.6f;
    }

    /// <summary>Swaps the liquid mid-cast; everything keeps its place.</summary>
    public void SetLiquid(Liquid liquid)
    {
        if (liquid == Liquid) return;
        Liquid = liquid;
        Gravity = liquid.Phys.Gravity * Px;
        foreach (var j in _jets) j.Retune(this);
    }

    // random numbers owned by the field, so a cast replays the same way
    public float Rand() { _rng = _rng * 1664525u + 1013904223u; return (_rng >> 8) * (1f / 16777216f); }
    public float Rand(float lo, float hi) => lo + Rand() * (hi - lo);

    /// <summary>Surface height at x, px from the top (level and waves).</summary>
    public float SurfaceY(float x)
    {
        if (_n == 0) return Size.Y;
        float u = Math.Clamp(x / _dx, 0f, _n - 1.001f);
        int i = (int)u;
        return Size.Y - Level - (H[i] + (H[i + 1] - H[i]) * (u - i));
    }

    /// <summary>Kicks the surface under a Gaussian; positive px/s is up.</summary>
    internal void Disturb(float x, float reach, float push)
    {
        int lo = Math.Max(0, (int)((x - 3f * reach) / _dx)), hi = Math.Min(_n - 1, (int)((x + 3f * reach) / _dx) + 1);
        float inv = 1f / (2f * reach * reach);
        for (int i = lo; i <= hi; i++)
        {
            float d = i * _dx - x;
            Vel[i] += push * MathF.Exp(-d * d * inv);
        }
    }

    internal void AddFoam(float x, float reach, float amount)
    {
        int lo = Math.Max(0, (int)((x - 2.5f * reach) / _dx)), hi = Math.Min(_n - 1, (int)((x + 2.5f * reach) / _dx) + 1);
        float inv = 1f / (2f * reach * reach);
        for (int i = lo; i <= hi; i++)
        {
            float d = i * _dx - x;
            Foam[i] = MathF.Min(Foam[i] + amount * MathF.Exp(-d * d * inv), 1.6f);
        }
    }

    internal int Columns => _n;
    internal float Spacing => _dx;

    /// <summary>Spawns a free drop (specks too small to mesh get the cheap look).</summary>
    internal void SpawnDrop(Vector2 pos, Vector2 vel, float r, float drag, int seed, float life = 4f)
    {
        if (_drops.Full) return;
        _drops.Add(new DropPool.Drop
        {
            Owner = _owner, Pos = pos, Vel = vel, Gravity = new Vector2(0f, Gravity), Drag = drag,
            Born = Time - 0.3f * life, Life = life,  // born past the fade-in: a bead that has just let go is already there
            Size = r, Brightness = 1f, Seed = seed, ColorOverride = Color,
            Variant = r < 1.7f * Px ? GoopDraw.Speck : 0,
        });
    }

    /// <summary>Flings droplets from at in a cone (angle from straight down, spread in radians, speed px/s at 1080p, radius px at 1080p): one call is one puff, so call it over a few frames for a burst.</summary>
    public void Spray(Vector2 at, float angle, float spread, float speed, int count, float radius)
    {
        var ph = Liquid.Phys;
        for (int k = 0; k < count; k++)
        {
            float a = angle + Rand(-1f, 1f) * spread, v = speed * Rand(0.35f, 1f) * Px;
            float r = radius * (0.22f + 0.78f * MathF.Pow(Rand(), 2.5f)) * Px;
            SpawnDrop(at + new Vector2(MathF.Sin(a), MathF.Cos(a)) * Rand(0f, 6f) * Px, new Vector2(MathF.Sin(a), MathF.Cos(a)) * v, r, ph.Drag * 0.3f, _seed + k * 37 + (int)(Time * 211f), life: 2.4f);
        }
    }

    /// <summary>Throws one drop (radius px at 1080p, velocity px/s at 1080p) from at.</summary>
    public void Fling(Vector2 at, Vector2 velocity, float radius) =>
        SpawnDrop(at, velocity * Px, radius * Px, Liquid.Phys.Drag * 0.3f, _seed + (int)(Time * 331f), life: 4f);

    /// <summary>Water thrown at the lens: one big bead with a scatter of smaller ones round it.</summary>
    public void Splat(Vector2 at, float radius, int count)
    {
        var ph = Liquid.Phys;
        for (int k = 0; k < count && _beadCount < MaxBeads; k++)
        {
            float a = Rand(0f, MathF.Tau), d = k == 0 ? 0f : radius * Px * 3.6f * MathF.Pow(Rand(), 0.7f);
            float r = (k == 0 ? Rand(0.85f, 1.15f) : Rand(0.14f, 0.55f)) * radius * Px;
            _beads[_beadCount++] = new Bead
            {
                P = at + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d, R = r, Life = Rand(14f, 30f), Trail = -1, Seed = _seed + k * 61 + (int)(Time * 53f),
                Wait = k == 0 ? Rand(0.05f, 0.5f) : Rand(0.4f, 5f) * (1f + 5f * ph.Viscosity), Heading = Rand(-0.4f, 0.4f),
            };
        }
    }

    private struct Landing : DropPool.IHook
    {
        public LiquidField F;
        public bool Hit(ref DropPool.Drop d)
        {
            if (d.Vel.Y <= 0f || d.Pos.Y + 0.35f * d.Size < F.SurfaceY(d.Pos.X)) return false;
            if (F._splatCount < MaxHits) F._splats[F._splatCount++] = new Impact { X = d.Pos.X, V = d.Vel.Length(), R = d.Size };
            return true;
        }
    }

    public void Step(float dt)
    {
        dt = Math.Clamp(dt, 0f, MaxStep);
        if (dt <= 0f || _n == 0) return;
        Time += dt;
        Gravity = Liquid.Phys.Gravity * Px;

        for (int i = 0; i < _jets.Count; i++) _jets[i].Step(this, Time, dt);
        StepSeeps(dt);

        var landing = new Landing { F = this };
        _drops.Advance(Time, dt, float.MaxValue, ref landing);

        for (int i = 0; i < _splatCount; i++) Land(_splats[i]);
        _splatCount = 0;
        for (int i = 0; i < _jets.Count; i++) if (_jets[i].Wet) Pour(_jets[i], dt);
        StepRebounds();
        StepBubbles(dt);
        StepBeads(dt);
        StepPool(dt);
    }

    private void StepSeeps(float dt)
    {
        var ph = Liquid.Phys;
        for (int i = 0; i < _seeps.Count; i++)
        {
            var s = _seeps[i];
            s.Fade += ((s.Live ? 1f : 0f) - s.Fade) * MathF.Min(1f, 4f * dt);
            if (!s.Live) continue;
            s.Phase += dt / s.Cycle * s.Rate * s.Boost;
            if (s.Phase < GoopEmitter.PinchAt) continue;

            int cs = unchecked(s.Seed + s.Cycles * 7331);
            float r = DrawHelpers.HashRange(cs + 1, ph.SizeMin, ph.SizeMax) * Px * s.Scale;
            float len = r * (1.2f + 5.4f * ph.Stringiness);
            GoopEmitter.PendantShape(1f, r, len, ph.Stringiness, out float rb, out float l, out float neck);
            Vector2 anchor = new(s.X, s.Y), bead = anchor + Swing(s, l, 1f);
            _drops.Add(new DropPool.Drop
            {
                Owner = _owner, Pos = bead, Vel = new Vector2(DrawHelpers.HashRange(cs + 7, -10f, 10f) * Px, DrawHelpers.HashRange(cs + 5, ph.ReleaseMin, ph.ReleaseMax) * Px),
                Gravity = new Vector2(0f, Gravity), Drag = ph.Drag, Anchor = anchor, Pinch = bead,
                Born = Time - 1.2f, Life = 4f, Size = rb, Brightness = 1f, Tether = neck, RecoilTime = 0.15f + 0.12f * ph.Stringiness,
                Seed = cs, ColorOverride = Color,
            });
            if (!_drops.Full && DrawHelpers.Hash01(cs + 8) < ph.Satellites)
                SpawnDrop(Vector2.Lerp(anchor, bead, 0.62f), new Vector2(DrawHelpers.HashRange(cs + 9, -12f, 12f) * Px, ph.ReleaseMin * 0.35f * Px),
                          rb * DrawHelpers.HashRange(cs + 10, 0.20f, 0.32f), ph.Drag, cs + 99);

            s.Cycles++;
            s.Cycle = DrawHelpers.HashRange(unchecked(s.Seed + s.Cycles * 7331 + 1), ph.CycleMin, ph.CycleMax);
            s.Phase = 0f;
        }
    }

    private static Vector2 Swing(LiquidSeep s, float len, float q)
    {
        float swing = 0.06f * MathF.Sin(s.Phase * 5f + s.Seed * 0.37f) * q;
        return new Vector2(MathF.Sin(swing), MathF.Cos(swing)) * len;
    }

    /// <summary>A drop reaches the water.</summary>
    private void Land(in Impact h)
    {
        var ph = Liquid.Phys;
        float r = h.R / Px, v = h.V / (1500f * Px), s = r * v;  // strength: about 3.5 for a fat drop at speed
        float surface = SurfaceY(h.X);

        // a crater, and the ring that leaves it
        float depth = MathF.Min(r * 1.6f, 15f) * MathF.Sqrt(Math.Clamp(v, 0.2f, 2f)) * Px;
        Disturb(h.X, MathF.Max(h.R * 1.3f, 3f * Px), -depth / 0.05f);
        AddFoam(h.X, h.R * 1.6f + 3f * Px, 0.18f * s * ph.Foam);

        if (r < 1.0f) return;

        // the crown: droplets flung up and outward
        int crown = (int)MathF.Round(ph.Splash * Math.Clamp(1.2f + 1.3f * s, 0f, 9f) * Rand(0.7f, 1.3f));
        for (int k = 0; k < crown; k++)
        {
            float side = k % 2 == 0 ? -1f : 1f, ang = Rand(0.12f, 1.0f);
            float sp = h.V * Rand(0.07f, 0.20f) * (0.55f + 0.45f * ph.Splash);
            SpawnDrop(new Vector2(h.X + side * h.R * Rand(0.4f, 1.1f), surface - 1.5f * Px),
                      new Vector2(side * MathF.Sin(ang) * sp, -MathF.Cos(ang) * sp * Rand(0.8f, 1.5f)),
                      h.R * Rand(0.14f, 0.34f), ph.Drag, _seed + k * 13 + (int)(h.X * 7f), life: 2.4f);
        }

        // air carried under
        int bubbles = (int)MathF.Round(ph.Bubbles * Math.Clamp(s * 0.8f, 0f, 5f) * Rand(0.6f, 1.2f));
        for (int k = 0; k < bubbles; k++) AddBubble(h.X + Rand(-1f, 1f) * h.R, surface + Rand(3f, 12f) * Px, Rand(0.9f, 2.6f) * Px, new Vector2(Rand(-20f, 20f) * Px, Rand(20f, 90f) * Px));

        // the jet that follows a big drop: the crater closes and throws a column up
        if (s > 2.2f && ph.Splash > 0.3f && _reboundCount < MaxRebounds)
            _rebounds[_reboundCount++] = new Rebound { At = Time + Rand(0.06f, 0.10f), X = h.X, V = h.V * Rand(0.18f, 0.30f), R = h.R * Rand(0.4f, 0.6f) };
    }

    private void StepRebounds()
    {
        int w = 0;
        for (int i = 0; i < _reboundCount; i++)
        {
            var b = _rebounds[i];
            if (Time >= b.At) SpawnDrop(new Vector2(b.X, SurfaceY(b.X) - 2f * Px), new Vector2(Rand(-18f, 18f) * Px, -b.V), b.R, Liquid.Phys.Drag, _seed + i * 17, life: 2.4f);
            else _rebounds[w++] = b;
        }
        _reboundCount = w;
    }

    /// <summary>A stream meeting the pool: hollow, foam, ripples, spray, bubbles.</summary>
    private void Pour(LiquidJet j, float dt)
    {
        var ph = Liquid.Phys;
        float r = j.HitR / Px, v = j.HitSpeed / (1500f * Px);
        float raw = r * r * v * 0.012f;  // about 1 for a 9 px stream at speed
        float F = 3f * raw / (3f + raw);  // a pool can only be dug so deep, however hard it is hit
        float x = j.Hit.X, reach = j.HitR * 1.3f + 5f * Px;

        // water digs a hollow; a thick liquid has nowhere to go and piles up instead
        float dig = DrawHelpers.Lerp(-1f, 0.7f, DrawHelpers.Smooth(0.30f, 0.90f, ph.Viscosity));
        Disturb(x, reach, dig * F * 260f * Px * dt * MathF.Min(1f, 22f * Px / reach));  // a steady force: the hollow settles near 10 px, however wide the stream
        Disturb(x + Rand(-1.5f, 1.5f) * reach, reach * 0.6f, Rand(-1f, 1f) * F * 110f * Px * (0.6f + Chop) * dt);  // the boil
        AddFoam(x, reach * 1.5f, F * dt * 7f * ph.Foam);

        // the churn sheds ripples that travel out across the pool
        _ripple += (2f + 5f * F) * dt;
        for (; _ripple >= 1f; _ripple -= 1f)
            Disturb(x + Rand(-0.8f, 0.8f) * reach, reach * 0.5f, Rand(-1f, 1f) * (50f + 90f * F) * Px * (0.5f + Chop));

        _spray += ph.Splash * MathF.Min(F * 22f, 60f) * dt;
        for (; _spray >= 1f; _spray -= 1f)
        {
            float side = Rand() < 0.5f ? -1f : 1f, ang = Rand(0.15f, 1.1f), sp = j.HitSpeed * Rand(0.05f, 0.17f);
            SpawnDrop(new Vector2(x + side * Rand(0.2f, 1.1f) * j.HitR, SurfaceY(x) - Px),
                      new Vector2(side * MathF.Sin(ang) * sp, -MathF.Cos(ang) * sp), Rand(0.6f, 2.3f) * Px, ph.Drag, _seed + (int)(Time * 977f), life: 2.4f);
        }

        _fizz += ph.Bubbles * MathF.Min(F * 6f, 14f) * dt;
        for (; _fizz >= 1f; _fizz -= 1f)
            AddBubble(x + Rand(-1f, 1f) * j.HitR * 1.2f, SurfaceY(x) + Rand(4f, 18f) * Px, Rand(0.8f, 2.8f) * Px, new Vector2(Rand(-40f, 40f) * Px, Rand(40f, 160f) * Px));
    }

    private void AddBubble(float x, float y, float r, Vector2 v)
    {
        if (BubbleCount >= MaxBubbles) return;
        Bubbles[BubbleCount++] = new Bubble { P = new Vector2(x, y), R = r, V = v, Phase = Rand(0f, MathF.Tau) };
    }

    /// <summary>Lens beads: spatter that merges, then wanders down the glass in fits and starts (thicker liquid clings longer).</summary>
    private void StepBeads(float dt)
    {
        var ph = Liquid.Phys;
        float v = ph.Viscosity, crit = (2.8f + 6f * v) * Px;

        _spatter += (0.5f + 2.2f * Chop) * (1f - 0.7f * v) * dt;
        for (; _spatter >= 1f && _beadCount < MaxBeads; _spatter -= 1f)
        {
            float x = Rand() < 0.62f ? Size.X * (Rand() < 0.5f ? Rand(0.01f, 0.24f) : Rand(0.76f, 0.99f)) : Rand(0.02f, 0.98f) * Size.X;
            _beads[_beadCount++] = new Bead
            {
                P = new Vector2(x, Rand(0.03f, 0.66f) * Size.Y), R = (2.1f + 4.4f * MathF.Pow(Rand(), 1.8f)) * Px,
                Wait = Rand(0.4f, 5f) * (1f + 5f * v), Life = Rand(14f, 30f), Seed = _seed + _beadCount * 53 + (int)(Time * 97f), Trail = -1, Heading = Rand(-0.4f, 0.4f),
            };
        }
        if (_spatter > 3f) _spatter = 0f;

        for (int i = 0; i < Trails.Length; i++)
            if (Trails[i].Fade > 0f && !Trails[i].Live) Trails[i].Fade = MathF.Max(0f, Trails[i].Fade - dt / (3f + 5f * v));

        for (int i = 0; i < _beadCount; i++)
        {
            ref var b = ref _beads[i];
            b.Age += dt;
            if (!b.Slides)
            {
                if (b.R < crit) continue;
                b.Wait -= dt;
                if (b.Wait <= 0f) { b.Slides = true; b.Speed = 1f; b.Heading = Rand(-0.5f, 0.5f); b.Trail = NewTrail(b.P, b.R); }
                continue;
            }

            // running: a drop on glass is slow, hesitates, and takes bursts when it gathers more water
            float run = ph.Cling * Px * (0.15f + b.R / (5f * Px)) * MathF.Max(0.15f, 0.55f + 0.9f * Noise.Value(b.Seed * 0.13f, b.Age * 1.3f));
            b.Speed += (run + b.Burst - b.Speed) * MathF.Min(1f, 5f * dt);
            b.Burst *= MathF.Exp(-3f * dt);

            // the glass is rough, so the path wanders: neighbours share the roughness, and a big bead holds a straighter line
            float stiff = 1f / (1f + b.R / (3f * Px));
            float rough = 0.55f * Noise.Value(b.P.X * 0.011f / Px, b.P.Y * 0.006f / Px);
            b.Heading += (rough * (0.6f + 0.8f * stiff) - b.Heading) * MathF.Min(1f, 1.8f * dt) + Rand(-1f, 1f) * 1.1f * stiff * MathF.Sqrt(dt);
            b.Heading = Math.Clamp(b.Heading, -1f, 1f);

            Vector2 dir = new(MathF.Sin(b.Heading), MathF.Cos(b.Heading));
            float dist = b.Speed * dt;
            b.P += dir * dist;
            b.R = MathF.Min(b.R + 0.027f * dist * Px, 5.8f * Px);
            if (b.Trail >= 0) AppendTrail(b.Trail, b.P, b.R * 0.6f);

            if (Rand() < (0.5f + 0.9f * v) * dt)
            {
                // it stalls, leaving a speck of itself behind
                EndTrail(ref b);
                b.Slides = false; b.Speed = 0f; b.Wait = Rand(0.3f, 2.5f) * (1f + 4f * v);
                float rr = Rand(0.8f, 1.3f) * Px;
                if (_beadCount < MaxBeads && rr * 3f < b.R)
                {
                    b.R = MathF.Cbrt(b.R * b.R * b.R - rr * rr * rr);
                    _beads[_beadCount++] = new Bead { P = b.P - dir * (b.R * 1.4f), R = rr, Life = Rand(8f, 16f), Trail = -1, Seed = b.Seed + 5 };
                }
            }
        }

        // a bead that meets a smaller one takes it in, and lurches
        for (int i = 0; i < _beadCount; i++)
            for (int k = 0; k < _beadCount; k++)
            {
                if (k == i || _beads[i].R <= 0f || _beads[k].R <= 0f || _beads[k].R > _beads[i].R || (_beads[k].R == _beads[i].R && k < i)) continue;
                float reach = (_beads[i].R + _beads[k].R) * 0.75f;
                if ((_beads[i].P - _beads[k].P).LengthSquared() > reach * reach) continue;
                _beads[i].R = MathF.Cbrt(_beads[i].R * _beads[i].R * _beads[i].R + _beads[k].R * _beads[k].R * _beads[k].R);
                _beads[i].Burst += Rand(25f, 70f) * Px;
                if (!_beads[i].Slides) _beads[i].Wait = MathF.Min(_beads[i].Wait, 0.1f);
                EndTrail(ref _beads[k]);
                _beads[k].R = 0f;
            }

        int w = 0;
        for (int i = 0; i < _beadCount; i++)
        {
            ref var b = ref _beads[i];
            bool gone = b.R <= 0f || b.P.Y - b.R > SurfaceY(b.P.X) || b.P.X < -10f || b.P.X > Size.X + 10f || b.Age > b.Life;
            if (gone) { EndTrail(ref b); continue; }
            _beads[w++] = b;
        }
        _beadCount = w;
    }

    private int NewTrail(Vector2 at, float r)
    {
        int best = 0; float low = float.MaxValue;
        for (int i = 0; i < Trails.Length; i++)
        {
            if (Trails[i].Live) continue;
            if (Trails[i].Fade < low) { low = Trails[i].Fade; best = i; if (low <= 0f) break; }
        }
        ref var t = ref Trails[best];
        t.Count = 1; t.Pts[0] = new Vector3(at.X, at.Y, r * 0.6f); t.Fade = 1f; t.Live = true;
        return best;
    }

    // the last point follows the bead; a new one is dropped every few pixels
    private void AppendTrail(int index, Vector2 at, float w)
    {
        ref var t = ref Trails[index];
        var p = new Vector3(at.X, at.Y, w);
        if (t.Count >= 2 && new Vector2(t.Pts[t.Count - 2].X - at.X, t.Pts[t.Count - 2].Y - at.Y).LengthSquared() < 36f * Px * Px) { t.Pts[t.Count - 1] = p; return; }
        if (t.Count == t.Pts.Length) { Array.Copy(t.Pts, 1, t.Pts, 0, t.Pts.Length - 1); t.Count--; }
        t.Pts[t.Count++] = p;
    }

    private void EndTrail(ref Bead b) { if (b.Trail >= 0) { Trails[b.Trail].Live = false; b.Trail = -1; } }

    private void StepBubbles(float dt)
    {
        float rise = Liquid.Phys.Rise * Px;
        int w = 0;
        for (int i = 0; i < BubbleCount; i++)
        {
            var b = Bubbles[i];
            b.Age += dt;
            float want = -rise * (0.55f + 0.45f * b.R / (2f * Px));
            b.V.Y += (want - b.V.Y) * MathF.Min(1f, 3.2f * dt);
            b.V.X *= MathF.Exp(-2.5f * dt);
            b.P += b.V * dt;
            b.P.X += MathF.Sin(b.Phase + b.Age * 7f) * 9f * Px * dt;

            float s = SurfaceY(b.P.X);
            if (b.P.Y - b.R <= s) { AddFoam(b.P.X, b.R * 2f + 2f * Px, 0.05f * Liquid.Phys.Foam); Disturb(b.P.X, 3f * Px, b.R * 4f); continue; }
            if (b.Age > 6f || b.P.Y > Size.Y + 8f) continue;
            Bubbles[w++] = b;
        }
        BubbleCount = w;
    }

    private void StepPool(float dt)
    {
        var ph = Liquid.Phys;
        float c = ph.Wave * Px, dx2 = _dx * _dx;
        float k = c * c / dx2, diff = ph.Smooth * Px * Px / dx2;
        int sub = Math.Clamp((int)MathF.Ceiling(MathF.Max(2f * c * dt / _dx / 1.1f, diff * dt / 0.4f)), 1, 16);
        float h = dt / sub, damp = ph.Damping, spring = 14f;

        for (int s = 0; s < sub; s++)
        {
            for (int i = 0; i < _n; i++)
            {
                float l = i > 0 ? H[i - 1] : H[i], r = i < _n - 1 ? H[i + 1] : H[i];
                Vel[i] += h * (k * (l + r - 2f * H[i]) - spring * H[i] - damp * Vel[i]);
            }
            for (int i = 0; i < _n; i++)
            {
                float l = i > 0 ? H[i - 1] : H[i], r = i < _n - 1 ? H[i + 1] : H[i];
                H[i] += h * (Vel[i] + diff * (l + r - 2f * H[i]));
            }
        }

        // foam fades, and spreads a little along the surface
        float fade = MathF.Exp(-dt / MathF.Max(ph.FoamLife, 0.05f));
        float prev = Foam[0];
        for (int i = 0; i < _n; i++)
        {
            float cur = Foam[i], next = i < _n - 1 ? Foam[i + 1] : cur;
            Foam[i] = (cur * 0.6f + (prev + next) * 0.2f) * fade;
            prev = cur;
        }
    }

    /// <summary>Hands the scene the pool region, swelling drips, beads and drops.</summary>
    public void Emit(EffectScene scene, float alpha)
    {
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = Size, Bottom = true, Alpha = alpha, ColorOverride = Color, State = this });

        var ph = Liquid.Phys;
        for (int i = 0; i < _seeps.Count; i++)
        {
            var s = _seeps[i];
            if (s.Fade < 0.02f) continue;
            float q = Math.Clamp((s.Phase - GoopEmitter.RestEnd) / (GoopEmitter.PinchAt - GoopEmitter.RestEnd), 0f, 1f);
            int cs = unchecked(s.Seed + s.Cycles * 7331);
            float rMax = DrawHelpers.HashRange(cs + 1, ph.SizeMin, ph.SizeMax) * Px * s.Scale;
            GoopEmitter.PendantShape(q, rMax, rMax * (1.2f + 5.4f * ph.Stringiness), ph.Stringiness, out float rb, out float len, out float neck);
            Vector2 anchor = new(s.X, s.Y);
            scene.AddParticle(new ParticlePrimitive
            {
                Position = anchor + Swing(s, len, q), Anchor = anchor, ThreadEnd = anchor + Swing(s, len, q), Tether = neck,
                Size = rb, AgeRatio = q, Brightness = alpha * s.Fade, Seed = cs, Role = PrimitiveRole.Goop, ColorOverride = Color,
            });
        }
        for (int i = 0; i < _beadCount; i++)
        {
            ref readonly var b = ref _beads[i];
            float fade = DrawHelpers.Smooth(b.Age / 0.4f) * DrawHelpers.Smooth((b.Life - b.Age) / 1.5f);
            scene.AddParticle(new ParticlePrimitive
            {
                Position = b.P, Velocity = new Vector2(MathF.Sin(b.Heading), MathF.Cos(b.Heading)) * b.Speed, Size = b.R, Brightness = alpha * fade, Seed = b.Seed,
                AgeRatio = 0.5f, Role = PrimitiveRole.Goop, ColorOverride = Color,
            });
        }
        _drops.Emit(scene, Time, hosted: false, alpha);
    }
}

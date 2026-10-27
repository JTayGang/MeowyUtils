using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Paralysis: being shocked rigid, over and over.</summary>
public sealed class ParalysisEffect : SceneEffect, IHasSwappableSlots
{
    // The arcs are what a "made of X" phrase replaces; a particle in it also replaces their crackle and sparks.
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke, "Arcs",         "stroke.lightning", Hero: true),
        new("Stroke",   PrimitiveRole.Halo,       "Arc afterglow", "stroke.corona"),
        new("Stroke",   PrimitiveRole.Edge,       "Edge arcs",    "stroke.lightning"),
        new("Particle", PrimitiveRole.Arc,        "Crackle",      "particle.arc", Hero: true),
        new("Particle", PrimitiveRole.Spark,      "Sparks",       "particle.spark", Hero: true),
        new("Particle", PrimitiveRole.Flash,      "Flare",        "particle.flare"),
        new("Particle", PrimitiveRole.Mote,       "Static",       "particle.static"),
        new("Particle", PrimitiveRole.Haze,       "Thunderhead",  "particle.thunderhead"),
        new("Region",   PrimitiveRole.MainStroke, "Storm light",  "region.edge-glow", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke, "Strike flash", "region.flat-fill", "FlatFill"),
    };

    // the cycle
    private const float ShockMin = 6f, ShockMax = 10f;  // seconds from one shock to the next
    private const float FirstShock = 0.08f;  // the cast is the first shock (a hair late, after the fade-in starts)
    private const float ChargeLead = 2.6f;  // the static builds for this long before a shock
    private const float DefaultLeader = 0.075f;  // seconds a leader takes to run an arc's length (a shock rolls its own)
    private const float ZapSeconds = 2.0f;  // an arc is spent this long after its strike
    private const int MinActive = 6;  // a shock always strikes at least this many arcs

    // the camera
    private const float CameraDistance = 1500f;  // px at 1080p; smaller = stronger perspective
    private readonly SigilView _view = new(new SigilSway(BaseTilt: 0.08f, TiltSway: 0.020f, Precession: 0.10f, LurchTilt: 0f, LurchSeconds: 1f, BreathBase: 1f, BreathAmp: 0f), CameraDistance);
    private const float PunchTilt = 0.06f;  // lean kicked by a shock
    private const float PunchDepth = 0.10f;  // camera yank toward the layers on a shock: near arcs surge at the lens
    private const float ShakeAmp = 13f;  // px at 1080p the first spasm throws the view
    private const float Tremor = 2.2f;  // px of per-frame shiver that rides the spasm

    // the arcs
    private const int ArcSegments = 40;
    private const float HalfLinks = 26f;  // chain links are 2x this however long the arc (see EmitArcs width)
    private const float HaloWidthOverArc = 1.8f;
    private const float AnchorOverhang = 0.05f;  // of the short side: anchors start just off the screen
    private const float AnchorMargin = 40f;  // px at 1080p an anchor sits beyond the screen edge, whatever the tilt
    private const float TwitchAmp = 30f;  // px at 1080p a shock bends an arc sideways, at first

    private readonly record struct LayerSpec(int Count, float Height, float Depth, float SpanMin, float SpanMax,
                                             float BulgeMin, float BulgeMax, float Bright, float Width, float Stagger);

    // Heights are px toward the viewer (negative = behind the screen plane); span is in screen perimeters.
    private static readonly LayerSpec[] Layers =
    {
        new(3, -360f, 0.70f, 0.75f, 1.05f, 0.06f, 0.12f, 0.42f, 0.62f, 0.080f),  // far: hazy, thin, wide sweeps
        new(3, -170f, 0.38f, 0.50f, 0.80f, 0.05f, 0.10f, 0.70f, 0.80f, 0.050f),  // back
        new(4,    0f, 0.06f, 0.36f, 0.60f, 0.04f, 0.08f, 1.00f, 1.00f, 0.000f),  // the screen plane: the cage proper
        new(2,  150f, 0.00f, 0.28f, 0.46f, 0.03f, 0.06f, 1.15f, 1.25f, 0.020f),  // near: big, bright, crisp
    };
    private const int ArcCount = 12;

    // Re-strikes after the return stroke: delay (s), brightness, and the brightness of a current run.
    private static readonly float[] RestrikeGain = { 0.70f, 0.45f, 0.30f };
    private static readonly float[] RestrikeSurge = { 1.5f, 1.0f, 0.8f };

    // sprinkled on top
    private const int CrawlSlots = 16;
    private const int EdgeSlots = 4, EdgePoints = 28;  // bolts that surge along the screen's edges
    private const int PopSlots = 6;  // static pops at the edges
    private const int HazeCount = 8;
    private const float HazeHeightA = -430f, HazeHeightB = -260f;

    private static readonly uint FlashTint = StormColor.Flash;
    private static readonly uint NumbTint = DrawHelpers.Pack(0.050f, 0.025f, 0.004f);
    private static readonly uint StormLight = DrawHelpers.Pack(1.00f, 0.66f, 0.14f);

    // A shock's spasm: the view snaps to a new offset at each of these times (s) with these gains, then rings out.
    private static readonly float[] SpasmAt = { 0.000f, 0.032f, 0.080f, 0.135f, 0.205f, 0.290f };
    private static readonly float[] SpasmGain = { 1.00f, -0.85f, 0.65f, -0.50f, 0.32f, -0.18f };

    /// <summary>One shock, decided in advance.</summary>
    private struct Plan
    {
        public int Seed;
        public float Power;  // overall strength
        public float HitAngle;  // the direction it comes from (screen radians)
        public float Bias;  // how much the arcs facing HitAngle are favoured over those facing away
        public float Participation;  // roughly what share of the arcs it strikes
        public int Order;  // 0 by layer, 1 a sweep round the perimeter, 2 a wave from the hit, 3 all at once
        public float Sweep;  // seconds the sweep or wave takes to cross the screen
        public float SweepStart, SweepDir;
        public float Leader;  // seconds a leader takes to cross an arc
        public int Restrikes;  // re-strikes after the return stroke (1-3)
        public float FlashScale;  // 0..1 of the full-screen flash
        public bool ReFlash;  // whether the weaker second flash follows
        public float Violence;  // how thrashing the arcs are (a multiplier)
    }

    private struct Arc
    {
        public float P0, Span, Bulge, Height, Depth, Bright, Width, Stagger, WobblePhase, ZapStart, ZapPower, ZapLeader, RollPower, Facing;
        public int Seed, Layer, LayerIdx, ZapId, StruckId, ZapRestrikes;
        public bool Active, StreamerOn;
    }

    private readonly Arc[] _arcs = new Arc[ArcCount];
    private readonly StrandPath[] _paths = new StrandPath[ArcCount];
    private readonly float[] _arcScale = new float[ArcCount], _arcLevel = new float[ArcCount], _arcReveal = new float[ArcCount];
    private readonly float[] _visA = new float[ArcCount], _visB = new float[ArcCount];  // the span of each arc that is on screen, as fractions of its length (-1 if none)
    private readonly float[] _layerStart = new float[Layers.Length];
    private readonly float[] _spT = new float[SpasmAt.Length], _spG = new float[SpasmGain.Length];
    private readonly StrandPath[] _edgePaths = new StrandPath[EdgeSlots];
    private readonly MoteField _motes = new(34, 650f, -420f, (8f, 26f), (10f, 40f), (1.6f, 3.8f));

    private readonly CastTracker _cast = new(envelope: true);
    private CastFrame _f;
    private Plan _plan, _fired;  // _plan: the next shock (the arcs are laid out for it)
    private bool _dirty;  // the arcs need laying out again for _plan, as soon as they are all spent
    private int _seed, _shockCount;
    private float _shockAt, _nextShock, _nextStray, _spasmAngle;

    public ParalysisEffect() : base(DebuffKind.Paralysis, "Paralysis",
        "Bolts surge along the screen's edges, static builds, then a different shock slams arcs of electricity across the screen every few seconds.",
        7, "Paralysis",
        keywords: ["paralysis", "paralyzed", "paralysed", "shocked", "electrified", "twitching"])
    {
        for (int i = 0; i < ArcCount; i++) _paths[i] = new StrandPath(ArcSegments + 1);
        for (int i = 0; i < EdgeSlots; i++) _edgePaths[i] = new StrandPath(EdgePoints);
    }

    public override void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        var f = _f = _cast.Frame(screenSize, alpha, time, dt, colorOverride);
        if (f.Fresh) BeginCast(f.Seed, f.Size);

        // The schedule: a shock every ShockMin..ShockMax seconds, and between them an occasional stray arc
        if (!f.Leaving)
        {
            if (_dirty && AllSpent(f.Age)) RollAll(f.Size);
            if (f.Age >= _nextShock) Shock(f.Age);
            else if (f.Age >= _nextStray)
            {
                if (!_dirty && f.Age < _nextShock - ChargeLead * 0.9f && f.Age > _shockAt + 2.4f) Stray(f.Age, f.Size);
                _nextStray = f.Age + DrawHelpers.HashRange(_seed + _shockCount * 211 + 9, 1.6f, 3.4f);
            }
        }

        float tau = f.Age - _shockAt;  // seconds since the latest shock
        float charge = Charge(f.Age);
        float quiet = tau < 0f ? 1f : DrawHelpers.Smooth((tau - 0.8f) / 0.9f);  // the idle crackle gives way to a shock, then returns
        float light = DrawHelpers.Saturate(ShockLight(tau, out float flash) * _fired.Power * 0.9f + 0.12f * charge * charge);

        // The camera: nearly still between shocks; a shock yanks it toward the layers and throws it about.
        Vector2 spasm = Spasm(tau, out float punch);
        float aspect = f.Size.X / f.Size.Y;
        float tiltK = MathF.Min(1f, 1.78f / aspect);
        float lean = PunchTilt * punch * _fired.Power * (Bolt.Lattice(_seed, _shockCount, 5) < 0f ? -1f : 1f);
        _view.Update(in f, extraTilt: lean, shift: spasm, distanceScale: 1f - PunchDepth * punch * _fired.Power, tiltScale: tiltK);

        EmitAtmosphere(scene, tau, flash, light, charge);
        EmitHaze(scene, light, charge);
        EmitArcs(scene, tau, charge, quiet);
        EmitCrawlers(scene, tau, charge, quiet);
        EmitEdgeArcs(scene, charge, quiet);
        EmitPops(scene, charge, quiet);
        EmitMotes(scene, light, charge);
    }

    // the cycle

    private void BeginCast(int seed, Vector2 size)
    {
        _seed = seed;
        _view.Begin(seed);
        _shockCount = 0;

        int n = 0;
        for (int l = 0; l < Layers.Length; l++)
        {
            ref readonly LayerSpec L = ref Layers[l];
            for (int i = 0; i < L.Count; i++, n++)
                _arcs[n] = new Arc { Layer = l, LayerIdx = i, Height = L.Height, Depth = L.Depth, Bright = L.Bright, Width = L.Width, ZapStart = -100f };
        }

        _motes.Seed(seed);

        _shockAt = -100f;
        _nextShock = FirstShock;
        _nextStray = 0f;
        _plan = RollPlan(0, true);
        RollAll(size);
        Shock(FirstShock);  // the cast is the first shock, and a bigger one
    }

    /// <summary>Decides one shock: how strong, where from, how many arcs, in what order, how many times re-struck.</summary>
    private Plan RollPlan(int index, bool intro)
    {
        int s = _seed + index * 8191 + 101;
        return new Plan
        {
            Seed = s,
            Power = intro ? 1.2f : DrawHelpers.HashRange(s + 1, 0.85f, 1.2f),
            Order = intro ? 0 : (int)(DrawHelpers.Hash01(s + 2) * 4f) % 4,
            HitAngle = DrawHelpers.Hash01(s + 3) * MathF.Tau,
            Bias = intro ? 0.12f : DrawHelpers.HashRange(s + 4, 0.15f, 0.55f),
            Participation = intro ? 1f : DrawHelpers.HashRange(s + 5, 0.60f, 1.00f),
            Leader = DrawHelpers.HashRange(s + 6, 0.060f, 0.105f),
            Restrikes = intro ? 2 : 1 + (int)(DrawHelpers.Hash01(s + 7) * 3f) % 3,
            Sweep = DrawHelpers.HashRange(s + 8, 0.22f, 0.42f),
            SweepStart = DrawHelpers.Hash01(s + 9) * 4f,
            SweepDir = DrawHelpers.Sign(s + 10),
            FlashScale = intro ? 1f : DrawHelpers.HashRange(s + 11, 0.70f, 1.00f),
            ReFlash = intro || DrawHelpers.Hash01(s + 12) < 0.70f,
            Violence = DrawHelpers.HashRange(s + 13, 0.85f, 1.15f),
        };
    }

    private bool AllSpent(float age)
    {
        for (int i = 0; i < ArcCount; i++)
            if (age - _arcs[i].ZapStart <= ZapSeconds) return false;
        return true;
    }

    /// <summary>Lays the cage out afresh for _plan: each layer's arcs are spread round the perimeter from a new random start.</summary>
    private void RollAll(Vector2 size)
    {
        Plan p = _plan;
        Vector2 c = size * 0.5f;
        for (int l = 0; l < Layers.Length; l++) _layerStart[l] = DrawHelpers.Hash01(p.Seed + 30 + l) * 4f;

        int active = 0;
        for (int n = 0; n < ArcCount; n++)
        {
            ref Arc a = ref _arcs[n];
            ref readonly LayerSpec L = ref Layers[a.Layer];
            int s = p.Seed + n * 7919 + 13;
            float slot = 4f / L.Count;

            a.P0 = _layerStart[a.Layer] + slot * a.LayerIdx + DrawHelpers.HashRange(s + 1, -0.12f, 0.12f) * slot;
            a.Span = DrawHelpers.HashRange(s + 2, L.SpanMin, L.SpanMax);
            a.Bulge = DrawHelpers.HashRange(s + 3, L.BulgeMin, L.BulgeMax);
            a.WobblePhase = DrawHelpers.Hash01(s + 7) * MathF.Tau;
            a.Seed = s;

            // How squarely this arc faces the hit, 0 (away) to 1 (into it).
            Vector2 mid = ScreenEdges.FromPerimeter(size, a.P0 + a.Span * 0.5f, 0f, out _) - c;
            float angle = MathF.Atan2(mid.Y, mid.X);
            a.Facing = 0.5f + 0.5f * MathF.Cos(angle - p.HitAngle);
            float favour = p.Bias * (a.Facing * 2f - 1f);
            a.Active = DrawHelpers.Hash01(s + 9) < Math.Clamp(p.Participation + favour, 0.15f, 1f);
            a.RollPower = p.Power * (1f + favour);
            a.StreamerOn = DrawHelpers.Hash01(s + 10) < 0.55f;

            float jitter = DrawHelpers.Hash01(s + 4);
            a.Stagger = p.Order switch
            {
                1 => DrawHelpers.Wrap((a.P0 + a.Span * 0.5f - p.SweepStart) * p.SweepDir / 4f, 1f) * p.Sweep + jitter * 0.015f,  // round the perimeter
                2 => (1f - a.Facing) * p.Sweep + jitter * 0.015f,  // a wave from the hit
                3 => jitter * 0.02f,  // all at once
                _ => L.Stagger + 0.012f * a.LayerIdx + jitter * 0.02f,  // by layer
            };
            if (a.Active) active++;
        }

        // A shock always has body: if the roll left too few arcs, call up the ones facing the hit.
        while (active < MinActive)
        {
            int best = -1;
            for (int n = 0; n < ArcCount; n++)
                if (!_arcs[n].Active && (best < 0 || _arcs[n].Facing > _arcs[best].Facing)) best = n;
            if (best < 0) break;
            _arcs[best].Active = true;
            active++;
        }
        _dirty = false;
    }

    /// <summary>A shock: the planned arcs strike (a hair apart, in plan order) and the view is thrown away from the hit.</summary>
    private void Shock(float at)
    {
        _fired = _plan;
        _shockAt = at;
        _shockCount++;
        _spasmAngle = _fired.HitAngle + MathF.PI;  // thrown away from where it came from
        for (int k = 0; k < SpasmAt.Length; k++)
        {
            _spT[k] = SpasmAt[k] * DrawHelpers.HashRange(_fired.Seed + 70 + k, 0.85f, 1.15f);
            _spG[k] = SpasmGain[k] * DrawHelpers.HashRange(_fired.Seed + 80 + k, 0.75f, 1.25f);
        }
        _nextShock = at + DrawHelpers.HashRange(_seed + _shockCount * 97 + 6, ShockMin, ShockMax);
        _nextStray = at + 2.6f;

        for (int i = 0; i < ArcCount; i++)
        {
            if (!_arcs[i].Active) continue;
            _arcs[i].ZapStart = at + _arcs[i].Stagger;
            _arcs[i].ZapPower = _arcs[i].RollPower;
            _arcs[i].ZapLeader = _fired.Leader;
            _arcs[i].ZapRestrikes = _fired.Restrikes;
            _arcs[i].ZapId++;
        }

        _plan = RollPlan(_shockCount, false);
        _dirty = true;
    }

    /// <summary>A stray: one arc (not from the faint far layer) arcs on its own at about half strength; no flash, no spasm.</summary>
    private void Stray(float age, Vector2 size)
    {
        float shortSide = MathF.Min(size.X, size.Y);
        int first = (int)(DrawHelpers.Hash01(_seed + _shockCount * 331 + (int)(age * 10f)) * 9f) % 9;
        for (int k = 0; k < 9; k++)
        {
            int pick = 3 + (first + k) % 9;  // arcs 3-11: everything but the faint far layer
            if (VisibleLength(_paths[pick], size, out float reach) < 0.40f * shortSide || reach < 0.05f * shortSide) continue;
            _arcs[pick].ZapStart = age;
            _arcs[pick].ZapPower = 0.55f;
            _arcs[pick].ZapLeader = DefaultLeader;
            _arcs[pick].ZapRestrikes = 1;
            _arcs[pick].ZapId++;
            return;
        }
    }

    /// <summary>Length of the path in px on the screen, and how far in from the nearest edge its deepest point reaches.</summary>
    private static float VisibleLength(StrandPath path, Vector2 size, out float reach)
    {
        float len = 0f;
        reach = 0f;
        for (int i = 0; i < path.Count; i++)
        {
            Vector2 a = path.Points[i];
            float edge = MathF.Min(MathF.Min(a.X, size.X - a.X), MathF.Min(a.Y, size.Y - a.Y));
            reach = MathF.Max(reach, edge);
            if (i == 0) continue;
            Vector2 b = path.Points[i - 1];
            bool ia = a.X >= 0f && a.Y >= 0f && a.X <= size.X && a.Y <= size.Y;
            bool ib = b.X >= 0f && b.Y >= 0f && b.X <= size.X && b.Y <= size.Y;
            if (ia && ib) len += Vector2.Distance(a, b);
        }
        return len;
    }

    /// <summary>0 while the screen is quiet, rising to 1 over the ChargeLead seconds before the next shock.</summary>
    private float Charge(float age)
    {
        float c = DrawHelpers.Saturate(1f - (_nextShock - age) / ChargeLead);
        return c * c * (3f - 2f * c);
    }


    /// <summary>Lighting after a shock: a quick flash, usually one weaker re-flash (two in all, so it stays safe), then a slower wash.</summary>
    private float ShockLight(float tau, out float flash)
    {
        flash = 0f;
        if (tau < 0f) return 0f;
        float f1 = MathF.Exp(-tau / 0.07f);
        float f2 = _fired.ReFlash && tau >= 0.22f ? 0.45f * MathF.Exp(-(tau - 0.22f) / 0.07f) : 0f;
        flash = MathF.Max(f1, f2) * _fired.FlashScale;
        return flash + 0.35f * MathF.Exp(-tau / 0.60f);
    }

    /// <summary>The spasm: the view snaps to a new offset at each spasm time, ringing out after the last, with a shiver.</summary>
    private Vector2 Spasm(float tau, out float punch)
    {
        punch = 0f;
        if (tau < 0f || tau > 1.2f) return Vector2.Zero;
        punch = MathF.Exp(-tau / 0.10f);

        float amp = ShakeAmp * _f.Px * _fired.Power;
        int k = 0;
        while (k + 1 < _spT.Length && tau >= _spT[k + 1]) k++;
        Vector2 cur = Offset(k, amp);
        Vector2 prev = k > 0 ? Offset(k - 1, amp) : Vector2.Zero;
        float snap = DrawHelpers.Smooth((tau - _spT[k]) / 0.016f);
        Vector2 v = prev + (cur - prev) * snap;
        if (k == _spT.Length - 1) v *= MathF.Exp(-MathF.Max(0f, tau - _spT[k] - 0.016f) / 0.20f);

        float shiver = MathF.Exp(-tau / 0.40f) * Tremor * _f.Px;
        int tick = (int)(tau * 55f);
        v += new Vector2(Bolt.Lattice(_seed + 7, tick, _shockCount), Bolt.Lattice(_seed + 8, tick, _shockCount)) * shiver;
        return v;
    }

    private Vector2 Offset(int k, float amp)
    {
        float a = _spasmAngle + k * 2.4f;  // each snap goes somewhere new, not back and forth
        return new Vector2(MathF.Cos(a), MathF.Sin(a)) * (_spG[k] * amp);
    }

    /// <summary>One arc's strike, as a function of the seconds since it fired: a dim leader runs the channel</summary>
    private static void Envelope(float tau, float power, float leader, int restrikes, out float reveal, out float level, out float glow, out float agit, out float width, out float surgeAt, out float surgeGain)
    {
        reveal = level = glow = agit = 0f;
        width = 0.6f;
        surgeAt = surgeGain = 0f;
        if (tau < 0f || tau > ZapSeconds) return;

        reveal = DrawHelpers.Saturate(tau / leader);
        float since = tau - leader;
        if (since < 0f)
        {
            level = 0.30f * power;
            agit = 1f;
            return;
        }

        float re = 0f;
        for (int k = 0; k < restrikes && k < RestrikeGain.Length; k++)
        {
            float t = 0.13f + 0.19f * k;
            if (since >= t) re += RestrikeGain[k] * MathF.Exp(-(since - t) / 0.07f);
            if (since >= t && since < t + 0.04f)
            {
                float u = (since - t) / 0.04f;
                surgeAt = (k & 1) == 0 ? u : 1f - u;  // each re-strike runs the other way
                surgeGain = RestrikeSurge[k] * power;
            }
        }
        float dying = 1f - DrawHelpers.Smooth((since - 0.85f) / 0.55f);
        level = power * (MathF.Exp(-since / 0.11f) + 0.55f * MathF.Exp(-since / 0.55f) + re) * dying;
        glow = power * 0.9f * MathF.Exp(-since / 0.45f) * (1f - DrawHelpers.Smooth((since - 1.0f) / 0.8f));
        agit = DrawHelpers.Saturate(power * (0.25f + 0.75f * MathF.Exp(-since / 0.30f)));
        width = 0.60f + 0.40f * DrawHelpers.Saturate(level);

        if (since < 0.05f) { surgeAt = 1f - since / 0.05f; surgeGain = 2.4f * power; }  // the return stroke: far end back to the start
    }

    // atmosphere

    private void EmitAtmosphere(EffectScene scene, float tau, float flash, float light, float charge)
    {
        float castIn = DrawHelpers.Smooth(_f.Age / 0.40f);

        scene.RequestVignette(StormColor.Vignette, 0.14f, _f.Alpha * (0.14f + 0.20f * light + 0.10f * charge) * castIn, priority: 12, _f.Color);

        // Edge light follows the smoothed lighting of the whole view, never an individual flicker.
        scene.EdgeSpill(_f.Size, _f.Short * 0.12f, StormLight, 0.10f * _f.Alpha * castIn * (0.32f + 1.7f * light + 0.6f * charge * charge), _f.Color);

        if (tau < 0f) return;

        // The shock: a flash and (usually) a weaker re-flash, then the world going numb for a beat.
        scene.Wash(_f.Size, FlashTint, 0.26f * _fired.Power * flash * _f.Gate, _f.Color);

        float numb = DrawHelpers.Smooth(tau / 0.12f) * (1f - DrawHelpers.Smooth((tau - 0.20f) / 1.0f));
        scene.Wash(_f.Size, NumbTint, 0.18f * _fired.Power * numb * _f.Gate, _f.Color);
    }

    /// <summary>The far layer: a deck of dark cloud that only shows when the shock lights it from inside.</summary>
    private void EmitHaze(EffectScene scene, float light, float charge)
    {
        ref readonly LayerCamera cam = ref _view.Cam;
        float px = _f.Px, dist = CameraDistance * px;
        float ramp = DrawHelpers.Smooth(_f.Age / 0.40f) * (1f - DrawHelpers.Smooth(_f.Leave));
        if (ramp <= 0.002f) return;

        float shortSide = MathF.Min(_f.Size.X, _f.Size.Y);
        Vector2 c = _f.Size * 0.5f;
        for (int i = 0; i < HazeCount; i++)
        {
            int s = _seed + i * 4217 + 91;
            float h = DrawHelpers.HashRange(s + 1, HazeHeightA, HazeHeightB) * px;
            float pf = MathF.Max(0.12f, 1f - h / dist);
            float perimeter = (i + DrawHelpers.Hash01(s + 2) * 0.8f) * (4f / HazeCount);
            Vector2 at0 = ScreenEdges.FromPerimeter(_f.Size, perimeter, -shortSide * 0.07f, out _);
            Vector2 drift = new(MathF.Sin(_f.Age * 0.11f + i) * 26f * px, MathF.Cos(_f.Age * 0.09f + i * 1.7f) * 20f * px);
            Vector2 at = cam.Project((at0.X - c.X) * pf + drift.X, (at0.Y - c.Y) * pf + drift.Y, h, out float scale);

            float period = DrawHelpers.HashRange(s + 3, 8f, 13f);
            float cycle = _f.Age / period + DrawHelpers.Hash01(s + 4);
            cycle -= MathF.Floor(cycle);

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = DrawHelpers.HashRange(s + 5, 440f, 780f) * px * scale, AgeRatio = cycle,
                Brightness = _f.Alpha * ramp * (0.06f + 0.62f * light + 0.18f * charge), Seed = s, Role = PrimitiveRole.Haze, ColorOverride = _f.Color,
            });
        }
    }

    // the arcs

    private void EmitArcs(EffectScene scene, float tau, float charge, float quiet)
    {
        ref readonly LayerCamera cam = ref _view.Cam;
        float px = _f.Px, dist = CameraDistance * px;
        Vector2 c = _f.Size * 0.5f;
        float overhang = _f.Short * AnchorOverhang;
        float margin = AnchorMargin * px;
        float shockEnv = tau >= 0f ? MathF.Exp(-tau / 0.25f) * _fired.Power : 0f;
        int thrash = (int)(MathF.Max(tau, 0f) * 30f);
        // Idle streamers flicker at the edges even when nothing is about to happen
        float stream = (0.22f + 0.78f * DrawHelpers.Smooth(charge)) * quiet;

        for (int i = 0; i < ArcCount; i++)
        {
            ref Arc a = ref _arcs[i];
            StrandPath path = _paths[i];

            // Layout is written as what the viewer sees
            float h = a.Height * px;
            float pf = MathF.Max(0.12f, 1f - h / dist);
            Vector2 A = KeepOffscreen((ScreenEdges.FromPerimeter(_f.Size, a.P0, overhang, out _) - c) * pf, h, in cam, _f.Size, margin);
            Vector2 B = KeepOffscreen((ScreenEdges.FromPerimeter(_f.Size, a.P0 + a.Span, overhang, out _) - c) * pf, h, in cam, _f.Size, margin);
            Vector2 toCentre = -(A + B) * 0.5f;
            float tcl = toCentre.Length();
            Vector2 inward = tcl > 1e-3f ? toCentre / tcl : Vector2.UnitY;
            Vector2 bow = inward * (a.Bulge * _f.Short * pf * (1f + 0.10f * MathF.Sin(_f.Age * 0.9f + a.WobblePhase)));

            // A shock bends an arc hard every which way for a moment (random each 1/30 s, not a sine).
            float tw = Bolt.Lattice(a.Seed, 0, thrash) * TwitchAmp * px * pf * shockEnv;
            var wobble = new RingWobble(5f * px * pf + tw, 1, a.WobblePhase + 0.9f * _f.Age, 2f * px * pf + 0.4f * tw, 2, a.WobblePhase * 1.7f - 1.3f * _f.Age);
            SigilGeometry.Span(path, ArcSegments, in cam, A, B, bow, h, in wobble);
            float scale = cam.ScaleAt(h);
            _arcScale[i] = scale;
            MeasureVisible(i, path, _f.Size);

            // Where is this arc in its life?
            float arcTau = _f.Age - a.ZapStart;
            bool zapping = arcTau >= 0f && arcTau <= ZapSeconds;
            float leader = zapping ? a.ZapLeader : DefaultLeader;
            float reveal, level, glow, agit, widthMul, surgeAt, surgeGain;
            if (zapping)
            {
                Envelope(arcTau, a.ZapPower, leader, a.ZapRestrikes, out reveal, out level, out glow, out agit, out widthMul, out surgeAt, out surgeGain);
                agit = DrawHelpers.Saturate(agit * _fired.Violence);
            }
            else
            {
                // Between shocks: short dim streamers from the anchors of the arcs the next shock will strike
                reveal = level = glow = agit = surgeAt = surgeGain = 0f;
                widthMul = 0.6f;
                if (a.Active && a.StreamerOn && stream > 0.04f)
                {
                    int tick = (int)MathF.Floor(_f.Age * 9f + a.WobblePhase * 3f);
                    if (DrawHelpers.Hash01(a.Seed + tick * 37) < 0.12f + 0.65f * stream)
                    {
                        reveal = (0.15f + 0.20f * stream) * (0.5f + 0.5f * DrawHelpers.Hash01(a.Seed + tick * 53 + 1));
                        level = (0.14f + 0.50f * stream);
                        agit = 0.15f + 0.50f * stream;
                    }
                }
            }

            if (_f.Leave > 0f) reveal = MathF.Min(reveal, 1f - 0.85f * _f.Leave);
            _arcReveal[i] = reveal;
            _arcLevel[i] = level;
            if (reveal <= 0.001f || (level <= 0.01f && glow <= 0.01f)) continue;

            // Link count is pinned (width follows arc length), so links never re-space as the stack zooms or tilts.
            float width = a.Width * widthMul * path.Length / (1.2f * HalfLinks);
            float bright = _f.Alpha * a.Bright * level;

            if (level > 0.01f)
                scene.AddStroke(new StrokePrimitive
                {
                    Path = path, Role = PrimitiveRole.MainStroke, Reveal = reveal, WidthHint = width,
                    Brightness = bright, Seed = a.Seed + 22, Phase = i * 0.7f, Depth = a.Depth, Agitation = agit,
                    TipFlare = reveal < 0.999f ? 1f : 0f, SurgeAt = surgeAt, SurgeGain = surgeGain, ColorOverride = _f.Color,
                });

            // The afterglow: the channel's heat, which outlasts the bolt.
            if (reveal >= 0.999f && glow > 0.02f && _f.Leave <= 0f)
                scene.AddStroke(new StrokePrimitive
                {
                    Path = path, Role = PrimitiveRole.Halo, Reveal = 1f, WidthHint = width * HaloWidthOverArc,
                    Brightness = _f.Alpha * a.Bright * glow * 0.50f, Seed = a.Seed + 21, Phase = i * 0.9f + 2f, Depth = a.Depth, Agitation = 0.2f, ColorOverride = _f.Color,
                });

            // The anchors: a flare where each end meets the screen's edge, and on a strike a spray of sparks.
            if (level <= 0.01f) continue;
            bool haveA = ScreenEdges.TryFindEntry(path, _f.Size, out Vector2 pa, out Vector2 ia);
            bool streamer = reveal < 0.999f;
            Vector2 pb = default, ob = default;
            bool haveB = !streamer && ScreenEdges.TryFindExit(path, _f.Size, out pb, out ob);

            float strike = zapping ? MathF.Exp(-MathF.Max(0f, arcTau - leader) / 0.12f) : 0f;
            float flareSize = (streamer ? 7f : 9f + 40f * strike * a.ZapPower) * px * scale;
            float flareBright = _f.Alpha * a.Bright * (0.30f + 0.70f * MathF.Min(level, 1.6f));
            if (haveA) AddFlare(scene, pa + ia * (4f * px), flareSize, flareBright, a.Seed + 31, _f.Color);
            if (haveB) AddFlare(scene, pb - ob * (4f * px), flareSize, flareBright, a.Seed + 32, _f.Color);

            if (zapping && arcTau >= leader && a.StruckId != a.ZapId)
            {
                a.StruckId = a.ZapId;
                if (haveA) AddStrike(scene, pa, ia, 0.6f * a.ZapPower, _f.Alpha, a.Seed + 41 + a.ZapId, _f.Color);
                if (haveB) AddStrike(scene, pb, -ob, 0.4f * a.ZapPower, _f.Alpha, a.Seed + 42 + a.ZapId, _f.Color);
            }
        }
    }

    /// <summary>Records which stretch of an arc is on screen (fractions of its length), so crackles land where seen.</summary>
    private void MeasureVisible(int i, StrandPath path, Vector2 size)
    {
        int first = -1, last = -1;
        for (int k = 0; k < path.Count; k++)
        {
            Vector2 p = path.Points[k];
            if (p.X < 4f || p.Y < 4f || p.X > size.X - 4f || p.Y > size.Y - 4f) continue;
            if (first < 0) first = k;
            last = k;
        }
        if (first < 0 || last <= first) { _visA[i] = _visB[i] = -1f; return; }
        float inv = 1f / (path.Count - 1);
        _visA[i] = first * inv;
        _visB[i] = last * inv;
    }

    /// <summary>Pushes a plane point outward (away from the centre) until, as the camera projects it</summary>
    private static Vector2 KeepOffscreen(Vector2 plane, float h, in LayerCamera cam, Vector2 size, float margin)
    {
        for (int it = 0; it < 5; it++)
        {
            Vector2 s = cam.Project(plane.X, plane.Y, h, out float scale);
            float inside = MathF.Min(MathF.Min(s.X, size.X - s.X), MathF.Min(s.Y, size.Y - s.Y));  // above 0 = on screen
            float need = inside + margin;  // px still to travel outward
            if (need <= 0f) break;
            float len = plane.Length();
            if (len < 1e-3f) break;
            plane += plane / len * (need / MathF.Max(scale, 0.2f) * 1.05f);
        }
        return plane;
    }

    private static void AddFlare(EffectScene scene, Vector2 at, float size, float brightness, int seed, Vector4? color) =>
        scene.AddParticle(new ParticlePrimitive
        {
            Position = at, Size = size, AgeRatio = 0.5f, Brightness = brightness,
            Seed = seed, Role = PrimitiveRole.Flash, ColorOverride = color,
        });

    private static void AddStrike(EffectScene scene, Vector2 at, Vector2 inward, float strength, float alpha, int seed, Vector4? color) =>
        scene.AddImpact(new ImpactPrimitive
        {
            StrokeRole = PrimitiveRole.MainStroke, Position = at, Direction = inward, Strength = strength,
            Brightness = alpha, Seed = seed, ColorOverride = color,
        });

    // the idle crackle

    /// <summary>Short crackles that hop off the arcs and die, placed on the part of each arc that's on screen; stateless.</summary>
    private void EmitCrawlers(EffectScene scene, float tau, float charge, float quiet)
    {
        float ramp = DrawHelpers.Smooth((_f.Age - 0.20f) / 0.5f) * (1f - DrawHelpers.Smooth(_f.Leave));
        if (ramp <= 0.002f) return;
        float after = tau >= 0f ? MathF.Exp(-tau / 0.5f) * _fired.Power : 0f;
        float chance = 0.26f * quiet + 0.45f * charge + 0.90f * after;

        for (int slot = 0; slot < CrawlSlots; slot++)
        {
            float rate = 3.2f + 3.0f * DrawHelpers.Hash01(slot * 13 + 1);
            float x = _f.Age * rate + DrawHelpers.Hash01(slot * 7 + 2) * 10f;
            int epoch = (int)MathF.Floor(x);
            float f = x - epoch;
            if (f > 0.7f) continue;

            int hs = _seed + slot * 9173 + epoch * 7919;
            if (DrawHelpers.Hash01(hs + 1) > chance) continue;

            int ai = (int)(DrawHelpers.Hash01(hs + 2) * ArcCount) % ArcCount;
            for (int tries = 0; tries < ArcCount && _visB[ai] <= _visA[ai]; tries++) ai = (ai + 1) % ArcCount;
            if (_visB[ai] <= _visA[ai]) continue;
            StrandPath path = _paths[ai];

            // Near an end of the visible stretch while quiet (they hop off the edges), anywhere on it once the arc is lit.
            float lit = MathF.Min(_arcLevel[ai], 1.6f);
            float r = DrawHelpers.Hash01(hs + 3);
            float span = _visB[ai] - _visA[ai];
            float u = lit > 0.3f ? _visA[ai] + span * r
                                 : _visA[ai] + span * (r < 0.5f ? 0.28f * r * 2f : 1f - 0.28f * (r - 0.5f) * 2f);
            path.SampleAtArc(u * path.Length, out Vector2 pos, out Vector2 tan);

            float side = DrawHelpers.Sign(hs + 4);
            Vector2 dir = DrawHelpers.Rotate(tan, side * (0.9f + 0.9f * DrawHelpers.Hash01(hs + 5)));
            float half = (9f + 8f * DrawHelpers.Hash01(hs + 6)) * _f.Px * _arcScale[ai];

            scene.AddParticle(new ParticlePrimitive
            {
                Position = pos + dir * (half * 0.9f), Size = half, AgeRatio = f / 0.7f,
                Brightness = _f.Alpha * ramp * (0.30f * quiet + 0.50f * charge + 0.90f * lit),
                Seed = hs, Role = PrimitiveRole.Arc, Rotation = MathF.Atan2(dir.X, -dir.Y), ColorOverride = _f.Color,
            });
        }
    }

    /// <summary>Bolts surging along the screen edges: real lightning strands (same material, forks, glow and core as the arcs).</summary>
    private void EmitEdgeArcs(EffectScene scene, float charge, float quiet)
    {
        float ramp = DrawHelpers.Smooth((_f.Age - 0.6f) / 0.8f) * (1f - DrawHelpers.Smooth(_f.Leave)) * quiet;
        if (ramp <= 0.002f) return;
        float px = _f.Px;
        float perimeter = 2f * (_f.Size.X + _f.Size.Y);
        float margin = 30f * px;

        for (int slot = 0; slot < EdgeSlots; slot++)
        {
            float period = 1.7f + 1.3f * DrawHelpers.Hash01(slot * 17 + 3);
            float x = _f.Age / period + DrawHelpers.Hash01(slot * 29 + 5);
            int epoch = (int)MathF.Floor(x);
            float f = x - epoch;
            const float liveFrac = 0.50f;  // each cycle a bolt is up for this share of it
            if (f > liveFrac) continue;

            int hs = _seed + slot * 7717 + epoch * 104729;
            if (DrawHelpers.Hash01(hs + 1) > 0.70f + 0.25f * charge) continue;

            float t = f * period;  // seconds since it struck
            float life = liveFrac * period;  // seconds it lasts

            // Where: an edge (more often a long one), a stretch clear of the corners, and how far it bows in.
            float pick = DrawHelpers.Hash01(hs + 2) * perimeter;
            int edge = 0;
            float edgeLen = _f.Size.X;
            while (edge < 3 && pick >= edgeLen) { pick -= edgeLen; edge++; edgeLen = (edge & 1) == 0 ? _f.Size.X : _f.Size.Y; }
            float len = MathF.Min((280f + 340f * DrawHelpers.Hash01(hs + 3)) * px, edgeLen - 2f * margin - 1f);
            if (len < 60f * px) continue;
            float along0 = margin + DrawHelpers.Hash01(hs + 4) * (edgeLen - 2f * margin - len);
            bool flip = DrawHelpers.Hash01(hs + 5) < 0.5f;  // which end the leader sets out from
            float bow = (28f + 55f * DrawHelpers.Hash01(hs + 6)) * px;

            StrandPath path = _edgePaths[slot];
            for (int i = 0; i < EdgePoints; i++)
            {
                float u = i / (float)(EdgePoints - 1);
                float along = along0 + len * (flip ? 1f - u : u);
                float depth = -3f * px + bow * MathF.Pow(MathF.Max(0f, MathF.Sin(MathF.PI * u)), 0.8f);  // ends touch the border (sin(pi) is a hair negative; a negative base to 0.8 is NaN)
                path.Points[i] = EdgePoint(_f.Size, edge, along, depth);
            }
            path.Count = EdgePoints;
            path.BuildArc();

            // A quick leader, then crackle and pulse (a current surge every ~0.25 s, alternating direction), then it dies away.
            const float leader = 0.10f;
            float reveal = DrawHelpers.Saturate(t / leader);
            float fade = 1f - DrawHelpers.Smooth((t - (life - 0.38f)) / 0.38f);
            float flick = 0.75f + 0.25f * DrawHelpers.Hash01(hs * 13 + (int)(_f.Age * 14f));
            float level = (0.34f + 0.40f * charge) * ramp * fade * flick;
            if (level <= 0.01f) continue;

            float surgeAt = 0f, surgeGain = 0f;
            float ts = t - leader;
            if (ts >= 0f)
            {
                const float surgePeriod = 0.27f;
                int k = (int)(ts / surgePeriod);
                float u = (ts - k * surgePeriod) / surgePeriod;
                surgeAt = (k & 1) == 0 ? u : 1f - u;
                surgeGain = 0.9f + 0.4f * charge;
            }

            scene.AddStroke(new StrokePrimitive
            {
                Path = path, Role = PrimitiveRole.Edge, Reveal = reveal, WidthHint = (16f + 8f * DrawHelpers.Hash01(hs + 7)) * px,
                Brightness = _f.Alpha * level, Seed = hs, Phase = slot * 0.9f, Depth = 0f, Agitation = reveal < 0.999f ? 1f : 0.35f + 0.35f * charge,
                TipFlare = reveal < 0.999f ? 1f : 0f, SurgeAt = surgeAt, SurgeGain = surgeGain, ColorOverride = _f.Color,
            });

            // Anchored: a small flash of light where each end meets the border.
            float anchor = _f.Alpha * level * (0.9f + 0.6f * (1f - DrawHelpers.Smooth(t / 0.25f)));
            AddFlare(scene, EdgePoint(_f.Size, edge, along0, 4f * px), 11f * px, anchor, hs + 8, _f.Color);
            AddFlare(scene, EdgePoint(_f.Size, edge, along0 + len, 4f * px), 11f * px, anchor, hs + 9, _f.Color);
        }
    }

    /// <summary>A point on the screen's border: edge 0 top, 1 right, 2 bottom, 3 left (each clockwise).</summary>
    private static Vector2 EdgePoint(Vector2 size, int edge, float along, float depth) => edge switch
    {
        0 => new Vector2(along, depth),
        1 => new Vector2(size.X - depth, along),
        2 => new Vector2(size.X - along, size.Y - depth),
        _ => new Vector2(depth, size.Y - along),
    };

    /// <summary>Static pops at the edges: a small crackle and a 0.2 s flash of light somewhere along the border.</summary>
    private void EmitPops(EffectScene scene, float charge, float quiet)
    {
        float ramp = DrawHelpers.Smooth((_f.Age - 0.6f) / 0.8f) * (1f - DrawHelpers.Smooth(_f.Leave)) * quiet;
        if (ramp <= 0.002f) return;
        float px = _f.Px, perimeter = 2f * (_f.Size.X + _f.Size.Y);

        for (int slot = 0; slot < PopSlots; slot++)
        {
            float rate = 0.7f + 0.9f * DrawHelpers.Hash01(slot * 19 + 1);
            float x = _f.Age * rate + DrawHelpers.Hash01(slot * 23 + 2) * 10f;
            int epoch = (int)MathF.Floor(x);
            float f = x - epoch;
            if (f > 0.25f) continue;

            int hs = _seed + slot * 5381 + epoch * 7907;
            if (DrawHelpers.Hash01(hs + 1) > 0.55f + 0.40f * charge) continue;

            float inset = (8f + 40f * DrawHelpers.Hash01(hs + 3)) * px;
            Vector2 p = PerimeterPoint(_f.Size, DrawHelpers.Hash01(hs + 2) * perimeter, inset, out _);
            float ar = f / 0.25f;
            float k = _f.Alpha * ramp * (0.55f + 0.45f * charge);
            float ang = DrawHelpers.Hash01(hs + 4) * MathF.Tau;

            scene.AddParticle(new ParticlePrimitive
            {
                Position = p, Size = (16f + 16f * DrawHelpers.Hash01(hs + 5)) * px, AgeRatio = ar, Brightness = k,
                Seed = hs, Role = PrimitiveRole.Arc, Rotation = ang, ColorOverride = _f.Color,
            });
            scene.AddParticle(new ParticlePrimitive
            {
                Position = p, Size = (12f + 8f * DrawHelpers.Hash01(hs + 6)) * px, AgeRatio = ar, Brightness = k * 0.85f,
                Seed = hs + 3, Role = PrimitiveRole.Flash, ColorOverride = _f.Color,
            });
        }
    }

    /// <summary>A point inset px inside the screen's border, a distance s round it (clockwise from the top-left corner)</summary>
    private static Vector2 PerimeterPoint(Vector2 size, float s, float inset, out Vector2 tangent)
    {
        float w = size.X, h = size.Y, perimeter = 2f * (w + h);
        s %= perimeter;
        if (s < 0f) s += perimeter;
        if (s < w) { tangent = Vector2.UnitX; return new Vector2(s, inset); }
        s -= w;
        if (s < h) { tangent = Vector2.UnitY; return new Vector2(w - inset, s); }
        s -= h;
        if (s < w) { tangent = -Vector2.UnitX; return new Vector2(w - s, h - inset); }
        s -= w;
        tangent = -Vector2.UnitY;
        return new Vector2(inset, h - s);
    }

    // Static charge hanging at every depth: it twinkles, builds with the charge and flares with a shock.
    private void EmitMotes(EffectScene scene, float light, float charge)
    {
        ref readonly LayerCamera cam = ref _view.Cam;
        float ramp = DrawHelpers.Smooth((_f.Age - 0.20f) / 1.0f) * (1f - DrawHelpers.Smooth(_f.Leave));
        float energy = MathF.Min(1.3f, 0.26f + 0.60f * charge + 0.80f * light);
        _motes.Emit(scene, in cam, CameraDistance * _f.Px, _f.Size, _f.Px, _f.Age, _f.Alpha * ramp * energy, blurDim: 0.65f, twinkle: 0.35f, _f.Color);
    }
}

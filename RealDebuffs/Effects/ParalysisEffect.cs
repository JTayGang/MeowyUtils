using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Paralysis: being shocked rigid, over and over. Between shocks the screen is still electrified, but quietly: sparks run round its edges, small static pops go off, short streamers flicker at the edges and
/// the charged air twinkles. About two and a half seconds before each shock the charge builds (the streamers lengthen where the shock is about to land, crackles hop more, the static brightens); then it
/// lands. A dozen arcs hung between anchors at four depths are struck, each with a dim leader, a blinding return stroke and quick re-strikes; the view flashes, goes numb and is thrown about in a
/// stuttering spasm; the clouds light from inside; then the channels die, an afterglow lingers and the screen settles back to its idle crackle. A shock lands every 6-10 seconds (ShockMin, ShockMax).
///
/// No two shocks are alike. Each has a <see cref="Plan"/> rolled in advance (during the quiet before it, so the charge streamers show where it will land): a fresh layout of the cage, which arcs take part,
/// the direction it hits from (the arcs facing it strike hardest and the view is thrown the other way), what order the arcs strike in, and how strong, how long-led and how many times re-struck it is.
/// The arcs are the hero: made of chains, rope or tentacles they appear and discharge on the same beat, keeping the glow, and a particle in a "made of" phrase replaces the sparks and crackles.
/// </summary>
public sealed class ParalysisEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Paralysis;
    public string DisplayName => "Paralysis";
    public string Description => "Sparks crawl the screen's edges, static builds, then a different shock slams arcs of electricity across the screen every few seconds.";
    public int DrawOrder => 7;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Paralysis"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "paralysis", "paralyzed", "paralysed", "shocked", "electrified", "twitching"
    };

    // The arcs are what a "made of X" phrase replaces; a particle in that phrase also replaces what crackles and sparks off them. The static in the air stays amber (it is still a swappable slot): swapped, its
    // out-of-focus foreground specks become big blank discs.
    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke),
        new("Particle", PrimitiveRole.Arc),
        new("Particle", PrimitiveRole.Spark),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke, "Arcs",         "stroke.lightning"),
        new("Stroke",   PrimitiveRole.Halo,       "Arc afterglow", "stroke.corona"),
        new("Particle", PrimitiveRole.Arc,        "Crackle",      "particle.arc"),
        new("Particle", PrimitiveRole.Spark,      "Sparks",       "particle.spark"),
        new("Particle", PrimitiveRole.Flash,      "Flare",        "particle.flare"),
        new("Particle", PrimitiveRole.Mote,       "Static",       "particle.static"),
        new("Particle", PrimitiveRole.Haze,       "Thunderhead",  "particle.thunderhead"),
        new("Region",   PrimitiveRole.MainStroke, "Storm light",  "region.edge-glow", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke, "Strike flash", "region.flat-fill", "FlatFill"),
    };

    // ---- the cycle ----
    private const float ShockMin = 6f, ShockMax = 10f;  // seconds from one shock to the next
    private const float FirstShock = 0.08f;             // the cast itself is the first shock (a hair late, so the manager's fade-in has started)
    private const float ChargeLead = 2.6f;              // the static builds for this long before a shock
    private const float DefaultLeader = 0.075f;         // seconds a leader takes to run an arc's length (a shock rolls its own)
    private const float ZapSeconds = 2.0f;              // an arc is spent this long after its strike
    private const int MinActive = 6;                    // a shock always strikes at least this many arcs

    // ---- the camera ----
    private const float CameraDistance = 1500f;         // px at 1080p; smaller = stronger perspective
    private const float BaseTilt = 0.08f;               // radians the stack leans from the line of sight; it barely moves between shocks
    private const float TiltSway = 0.020f;
    private const float PrecessionRate = 0.10f;
    private const float PunchTilt = 0.06f;              // lean kicked by a shock
    private const float PunchDepth = 0.10f;             // fraction the camera is yanked toward the layers on a shock: near arcs surge at the lens, far ones recoil
    private const float ShakeAmp = 13f;                 // px at 1080p the first spasm throws the view
    private const float Tremor = 2.2f;                  // px of per-frame shiver that rides the spasm

    // ---- the arcs ----
    private const int ArcSegments = 40;
    private const float HalfLinks = 26f;                // a chain gets 2x this many links however long the arc is (see the width in EmitArcs)
    private const float HaloWidthOverArc = 1.8f;
    private const float AnchorOverhang = 0.05f;         // of the short side: anchors start just off the screen, and are pushed further out if the camera drags them in
    private const float AnchorMargin = 40f;             // px at 1080p an anchor must sit beyond the screen's edge, whatever the tilt, aspect or shake
    private const float TwitchAmp = 30f;                // px at 1080p a shock bends an arc sideways, at first

    private readonly record struct LayerSpec(int Count, float Height, float Depth, float SpanMin, float SpanMax,
                                             float BulgeMin, float BulgeMax, float Bright, float Width, float Stagger);

    // Heights are px toward the viewer (negative = behind the screen plane); span is in screen perimeters (1 = one edge), bulge a fraction of the short side.
    // Stagger is how long after the shock this layer's arcs strike in the by-layer order: the screen plane first, then the lens, then back into the distance.
    private static readonly LayerSpec[] Layers =
    {
        new(3, -360f, 0.70f, 0.75f, 1.05f, 0.06f, 0.12f, 0.42f, 0.62f, 0.080f),   // far: hazy, thin, wide sweeps
        new(3, -170f, 0.38f, 0.50f, 0.80f, 0.05f, 0.10f, 0.70f, 0.80f, 0.050f),   // back
        new(4,    0f, 0.06f, 0.36f, 0.60f, 0.04f, 0.08f, 1.00f, 1.00f, 0.000f),   // the screen plane: the cage proper
        new(2,  150f, 0.00f, 0.28f, 0.46f, 0.03f, 0.06f, 1.15f, 1.25f, 0.020f),   // near: big, bright, crisp
    };
    private const int ArcCount = 12;

    // Re-strikes after the return stroke: when (seconds after it), how bright, and how bright a run of current races the channel.
    private static readonly float[] RestrikeGain = { 0.70f, 0.45f, 0.30f };
    private static readonly float[] RestrikeSurge = { 1.5f, 1.0f, 0.8f };

    // ---- sprinkled on top ----
    private const int CrawlSlots = 16;
    private const int RunnerSlots = 4, RunnerTrail = 8;  // sparks that run round the screen's edges
    private const int PopSlots = 6;                      // static pops at the edges
    private const int MoteCount = 34;
    private const float MoteNear = 650f, MoteFar = -420f;
    private const int HazeCount = 8;
    private const float HazeHeightA = -430f, HazeHeightB = -260f;

    private static readonly uint FlashTint = StormColor.Flash;
    private static readonly uint NumbTint = DrawHelpers.Pack(0.050f, 0.025f, 0.004f);
    private static readonly uint StormLight = DrawHelpers.Pack(1.00f, 0.66f, 0.14f);

    // A shock's spasm: the view snaps to a new offset at each of these times (seconds) with these gains, then rings out. Stepped, not swung: a muscle, not a pendulum. Each shock jitters both.
    private static readonly float[] SpasmAt = { 0.000f, 0.032f, 0.080f, 0.135f, 0.205f, 0.290f };
    private static readonly float[] SpasmGain = { 1.00f, -0.85f, 0.65f, -0.50f, 0.32f, -0.18f };

    /// <summary>One shock, decided in advance. Rolled when the previous one is spent, so the arcs are already laid out for it (and the charge streamers already point at where it will land).</summary>
    private struct Plan
    {
        public int Seed;
        public float Power;           // overall strength
        public float HitAngle;        // the direction it comes from (screen radians)
        public float Bias;            // how much the arcs facing HitAngle are favoured over those facing away
        public float Participation;   // roughly what share of the arcs it strikes
        public int Order;             // 0 by layer, 1 a sweep round the perimeter, 2 a wave from the hit, 3 all at once
        public float Sweep;           // seconds the sweep or wave takes to cross the screen
        public float SweepStart, SweepDir;
        public float Leader;          // seconds a leader takes to cross an arc
        public int Restrikes;         // re-strikes after the return stroke (1-3)
        public float FlashScale;      // 0..1 of the full-screen flash
        public bool ReFlash;          // whether the weaker second flash follows
        public float Violence;        // how thrashing the arcs are (a multiplier)
    }

    private struct Arc
    {
        public float P0, Span, Bulge, Height, Depth, Bright, Width, Stagger, WobblePhase, ZapStart, ZapPower, ZapLeader, RollPower, Facing;
        public int Seed, Layer, LayerIdx, ZapId, StruckId, ZapRestrikes;
        public bool Active, StreamerOn;
    }

    private struct Mote
    {
        public float Fx, Fy, Z, Speed, Sway, Phase, Size;
        public int Seed;
    }

    private readonly Arc[] _arcs = new Arc[ArcCount];
    private readonly StrandPath[] _paths = new StrandPath[ArcCount];
    private readonly float[] _arcScale = new float[ArcCount], _arcLevel = new float[ArcCount], _arcReveal = new float[ArcCount];
    private readonly float[] _visA = new float[ArcCount], _visB = new float[ArcCount];     // the span of each arc that is on screen, as fractions of its length (-1 if none)
    private readonly float[] _layerStart = new float[Layers.Length];
    private readonly float[] _spT = new float[SpasmAt.Length], _spG = new float[SpasmGain.Length];
    private readonly Mote[] _motes = new Mote[MoteCount];

    private readonly CastTracker _cast = new();
    private Plan _plan, _fired;               // _plan: the next shock (the arcs are laid out for it); _fired: the one in progress or just spent
    private bool _dirty;                      // the arcs need laying out again for _plan, as soon as they are all spent
    private int _seed, _shockCount;
    private float _azimuth, _azimuthDir, _shockAt, _nextShock, _nextStray, _spasmAngle, _rampLead, _lastAlpha, _peakAlpha;
    private bool _leaving;
    private float _px = 1f;

    public ParalysisEffect()
    {
        for (int i = 0; i < ArcCount; i++) _paths[i] = new StrandPath(ArcSegments + 1);
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        if (_cast.Begin(time))
            BeginCast(unchecked((int)(_cast.Start * 1000f)), dt, screenSize);

        float age = time - _cast.Start;
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        _px = shortSide / 1080f;
        Vector2 center = screenSize * 0.5f;

        // Alpha only falls while the status is lifting; the cage then discharges instead of simply dimming.
        if (alpha > _lastAlpha + 1e-4f) _leaving = false;
        else if (alpha < _lastAlpha - 1e-4f) _leaving = true;
        _lastAlpha = alpha;
        if (!_leaving) _peakAlpha = MathF.Max(_peakAlpha, alpha);
        float leave = _leaving && _peakAlpha > 0.01f ? DrawHelpers.Saturate(1f - alpha / _peakAlpha) : 0f;

        // The manager fades alpha in from nothing over FadeInSeconds, which would dim the first strike. The fade is linear, so alpha * FadeInSeconds / rampAge is the full strength:
        // ride the ramp up to three times faster (punch), and let the flash ignore it (gate). The fade-out is left alone.
        float gate = alpha;
        if (!_leaving && age + _rampLead < EffectManager.FadeInSeconds)
        {
            float toFull = EffectManager.FadeInSeconds / MathF.Max(age + _rampLead, 0.001f);
            gate = MathF.Min(alpha * toFull, 1f);
            alpha *= MathF.Min(3f, toFull);
        }

        // The schedule: a shock every ShockMin..ShockMax seconds, and between them an occasional stray arc, never in the build-up to a shock and never while discharging.
        // Once every arc is spent the cage is laid out afresh for the shock after.
        if (!_leaving)
        {
            if (_dirty && AllSpent(age)) RollAll(screenSize);
            if (age >= _nextShock) Shock(age);
            else if (age >= _nextStray)
            {
                if (!_dirty && age < _nextShock - ChargeLead * 0.9f && age > _shockAt + 2.4f) Stray(age, screenSize);
                _nextStray = age + DrawHelpers.HashRange(_seed + _shockCount * 211 + 9, 1.6f, 3.4f);
            }
        }

        float tau = age - _shockAt;                                  // seconds since the latest shock
        float charge = Charge(age);
        float quiet = tau < 0f ? 1f : DrawHelpers.Smooth((tau - 0.8f) / 0.9f);        // the idle crackle gives way to a shock, then returns
        float light = Saturate(ShockLight(tau, out float flash) * _fired.Power * 0.9f + 0.12f * charge * charge);

        // The camera: nearly still between shocks; a shock yanks it toward the layers and throws it about.
        Vector2 spasm = Spasm(tau, out float punch);
        float aspect = screenSize.X / screenSize.Y;
        float tiltK = MathF.Min(1f, 1.78f / aspect);                // a wide screen reaches further from its centre: the same lean would drag its edges far inside
        float tilt = (BaseTilt + TiltSway * MathF.Sin(age * 0.27f + 1.1f) + PunchTilt * punch * _fired.Power * (Bolt.Lattice(_seed, _shockCount, 5) < 0f ? -1f : 1f)) * tiltK + 0.06f * leave;
        float azimuth = _azimuth + _azimuthDir * PrecessionRate * age;
        var cam = new LayerCamera(center + spasm, CameraDistance * _px * (1f - PunchDepth * punch * _fired.Power), tilt, azimuth);

        EmitAtmosphere(scene, screenSize, shortSide, alpha, gate, age, tau, flash, light, charge, colorOverride);
        EmitHaze(scene, in cam, screenSize, alpha, age, leave, light, charge, colorOverride);
        EmitArcs(scene, in cam, screenSize, shortSide, alpha, age, tau, leave, charge, quiet, colorOverride);
        EmitCrawlers(scene, alpha, age, screenSize, tau, leave, charge, quiet, colorOverride);
        EmitRunners(scene, alpha, age, screenSize, leave, charge, quiet, colorOverride);
        EmitPops(scene, alpha, age, screenSize, leave, charge, quiet, colorOverride);
        EmitMotes(scene, in cam, screenSize, alpha, age, leave, light, charge, colorOverride);
    }

    // ---- the cycle ----

    private void BeginCast(int seed, float dt, Vector2 size)
    {
        _seed = seed;
        _rampLead = dt;
        _azimuth = DrawHelpers.Hash01(seed + 1) * MathF.Tau;
        _azimuthDir = DrawHelpers.Hash01(seed + 2) < 0.5f ? -1f : 1f;
        _shockCount = 0;
        _leaving = false;
        _peakAlpha = 0f;
        _lastAlpha = 0f;

        int n = 0;
        for (int l = 0; l < Layers.Length; l++)
        {
            ref readonly LayerSpec L = ref Layers[l];
            for (int i = 0; i < L.Count; i++, n++)
                _arcs[n] = new Arc { Layer = l, LayerIdx = i, Height = L.Height, Depth = L.Depth, Bright = L.Bright, Width = L.Width, ZapStart = -100f };
        }

        // Charge in the air fills the view frustum at each depth (Fx, Fy are -1..1 across the screen there).
        for (int i = 0; i < MoteCount; i++)
        {
            int s = seed + i * 6151 + 11;
            _motes[i] = new Mote
            {
                Fx = DrawHelpers.HashRange(s + 2, -1.12f, 1.12f),
                Fy = DrawHelpers.HashRange(s + 3, -1.12f, 1.12f),
                Z = MoteFar + (MoteNear - MoteFar) * MathF.Pow(DrawHelpers.Hash01(s + 1), 1.5f),
                Speed = DrawHelpers.HashRange(s + 4, 8f, 26f),
                Sway = DrawHelpers.HashRange(s + 5, 10f, 40f),
                Phase = DrawHelpers.Hash01(s + 6) * MathF.Tau,
                Size = DrawHelpers.HashRange(s + 7, 1.6f, 3.8f),
                Seed = s,
            };
        }

        _shockAt = -100f;
        _nextShock = FirstShock;
        _nextStray = 0f;
        _plan = RollPlan(0, true);
        RollAll(size);
        Shock(FirstShock);                                          // the cast is the first shock, and a bigger one
    }

    /// <summary>Decides one shock: how strong, where from, how many arcs, in what order, how many times re-struck. The cast (intro) is always a full, strong strike by layer.</summary>
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
            SweepDir = DrawHelpers.Hash01(s + 10) < 0.5f ? -1f : 1f,
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

    /// <summary>
    /// Lays the whole cage out afresh for <see cref="_plan"/>: each layer's arcs are spread round the perimeter from a new random start (so the screen is always covered evenly, never the same way twice),
    /// each is given a new span, bow and crackle seed, the plan decides which take part (favouring those facing its hit), their strength, and the order they will strike in.
    /// </summary>
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
                1 => ((((a.P0 + a.Span * 0.5f - p.SweepStart) * p.SweepDir / 4f) % 1f + 1f) % 1f) * p.Sweep + jitter * 0.015f,   // round the perimeter
                2 => (1f - a.Facing) * p.Sweep + jitter * 0.015f,                                                                  // a wave from the hit
                3 => jitter * 0.02f,                                                                                               // all at once
                _ => L.Stagger + 0.012f * a.LayerIdx + jitter * 0.02f,                                                             // by layer
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

    /// <summary>A shock: the planned arcs are struck (a hair apart, in the plan's order), the view is thrown away from the hit, the next shock is scheduled and the next plan rolled.</summary>
    private void Shock(float at)
    {
        _fired = _plan;
        _shockAt = at;
        _shockCount++;
        _spasmAngle = _fired.HitAngle + MathF.PI;                    // thrown away from where it came from
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

    /// <summary>
    /// A stray: one arc (not from the faint far layer) arcs on its own, at about half strength. No flash, no spasm. Only an arc with a decent length actually on the screen is chosen
    /// (some hug an edge from outside and barely show), judged by where its path was last frame.
    /// </summary>
    private void Stray(float age, Vector2 size)
    {
        float shortSide = MathF.Min(size.X, size.Y);
        int first = (int)(DrawHelpers.Hash01(_seed + _shockCount * 331 + (int)(age * 10f)) * 9f) % 9;
        for (int k = 0; k < 9; k++)
        {
            int pick = 3 + (first + k) % 9;                          // arcs 3-11: everything but the faint far layer
            if (VisibleLength(_paths[pick], size, out float reach) < 0.40f * shortSide || reach < 0.05f * shortSide) continue;
            _arcs[pick].ZapStart = age;
            _arcs[pick].ZapPower = 0.55f;
            _arcs[pick].ZapLeader = DefaultLeader;
            _arcs[pick].ZapRestrikes = 1;
            _arcs[pick].ZapId++;
            return;
        }
    }

    /// <summary>Length of the path, in px, that lies on the screen, and how far in from the nearest screen edge its deepest point reaches.</summary>
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

    private static float Saturate(float v) => DrawHelpers.Saturate(v);

    /// <summary>The whole view's lighting after a shock: a quick flash and (usually) one weaker re-flash (two in all: enough to read as lightning, few enough to be safe), then a slower wash.</summary>
    private float ShockLight(float tau, out float flash)
    {
        flash = 0f;
        if (tau < 0f) return 0f;
        float f1 = MathF.Exp(-tau / 0.07f);
        float f2 = _fired.ReFlash && tau >= 0.22f ? 0.45f * MathF.Exp(-(tau - 0.22f) / 0.07f) : 0f;
        flash = MathF.Max(f1, f2) * _fired.FlashScale;
        return flash + 0.35f * MathF.Exp(-tau / 0.60f);
    }

    /// <summary>The spasm: the view snaps to a new offset at each of this shock's spasm times, ringing out after the last, with a shiver riding it; punch is a 0..1 jolt that fades in about a tenth of a second.</summary>
    private Vector2 Spasm(float tau, out float punch)
    {
        punch = 0f;
        if (tau < 0f || tau > 1.2f) return Vector2.Zero;
        punch = MathF.Exp(-tau / 0.10f);

        float amp = ShakeAmp * _px * _fired.Power;
        int k = 0;
        while (k + 1 < _spT.Length && tau >= _spT[k + 1]) k++;
        Vector2 cur = Offset(k, amp);
        Vector2 prev = k > 0 ? Offset(k - 1, amp) : Vector2.Zero;
        float snap = DrawHelpers.Smooth((tau - _spT[k]) / 0.016f);
        Vector2 v = prev + (cur - prev) * snap;
        if (k == _spT.Length - 1) v *= MathF.Exp(-MathF.Max(0f, tau - _spT[k] - 0.016f) / 0.20f);

        float shiver = MathF.Exp(-tau / 0.40f) * Tremor * _px;
        int tick = (int)(tau * 55f);
        v += new Vector2(Bolt.Lattice(_seed + 7, tick, _shockCount), Bolt.Lattice(_seed + 8, tick, _shockCount)) * shiver;
        return v;
    }

    private Vector2 Offset(int k, float amp)
    {
        float a = _spasmAngle + k * 2.4f;                            // each snap goes somewhere new, not back and forth
        return new Vector2(MathF.Cos(a), MathF.Sin(a)) * (_spG[k] * amp);
    }

    /// <summary>
    /// One arc's strike, as a function of the seconds since it fired: a dim leader runs the channel; the return stroke slams back along it (a blinding run of current) and the channel blows out; the shock's
    /// re-strikes follow; the channel cools and dies, leaving an afterglow that outlasts it. Level is the bolt's brightness, glow the afterglow's, width a multiplier on the channel.
    /// </summary>
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
                surgeAt = (k & 1) == 0 ? u : 1f - u;                 // each re-strike runs the other way
                surgeGain = RestrikeSurge[k] * power;
            }
        }
        float dying = 1f - DrawHelpers.Smooth((since - 0.85f) / 0.55f);
        level = power * (MathF.Exp(-since / 0.11f) + 0.55f * MathF.Exp(-since / 0.55f) + re) * dying;
        glow = power * 0.9f * MathF.Exp(-since / 0.45f) * (1f - DrawHelpers.Smooth((since - 1.0f) / 0.8f));
        agit = DrawHelpers.Saturate(power * (0.25f + 0.75f * MathF.Exp(-since / 0.30f)));
        width = 0.60f + 0.40f * DrawHelpers.Saturate(level);

        if (since < 0.05f) { surgeAt = 1f - since / 0.05f; surgeGain = 2.4f * power; }        // the return stroke: far end back to the start
    }

    // ---- atmosphere ----

    private void EmitAtmosphere(EffectScene scene, Vector2 size, float shortSide, float alpha, float gate, float age, float tau,
                                float flash, float light, float charge, Vector4? color)
    {
        float castIn = DrawHelpers.Smooth(age / 0.40f);

        scene.RequestVignette(StormColor.Vignette, 0.14f, alpha * (0.14f + 0.20f * light + 0.10f * charge) * castIn, priority: 12, color);

        // Light from the arcs spills onto the screen's edges. It follows the smoothed lighting of the whole view (the shock, then its wash, then the charge), never an individual flicker.
        float depth = shortSide * 0.12f;
        float a = 0.10f * alpha * castIn * (0.32f + 1.7f * light + 0.6f * charge * charge);
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new Vector2(size.X, depth), Tint = StormLight, Alpha = a, Top = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(0f, size.Y - depth), Max = size, Tint = StormLight, Alpha = a, Bottom = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new Vector2(depth, size.Y), Tint = StormLight, Alpha = a, Left = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(size.X - depth, 0f), Max = size, Tint = StormLight, Alpha = a, Right = true, ColorOverride = color });

        if (tau < 0f) return;

        // The shock: a flash and (usually) a weaker re-flash, then the world going numb for a beat. (Gate rather than alpha: on the cast the manager's fade-in would swallow the flash.)
        if (flash > 0.01f)
            scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = FlashTint, Alpha = 0.26f * _fired.Power * flash * gate, ColorOverride = color });

        float numb = DrawHelpers.Smooth(tau / 0.12f) * (1f - DrawHelpers.Smooth((tau - 0.20f) / 1.0f));
        if (numb > 0.01f)
            scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = NumbTint, Alpha = 0.18f * _fired.Power * numb * gate, ColorOverride = color });
    }

    /// <summary>The far layer: a deck of dark cloud that only shows when the shock lights it from inside. Puffs hang behind the screen at different depths, so they slide against the arcs as the stack tilts.</summary>
    private void EmitHaze(EffectScene scene, in LayerCamera cam, Vector2 size, float alpha, float age, float leave, float light, float charge, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;
        float ramp = DrawHelpers.Smooth(age / 0.40f) * (1f - DrawHelpers.Smooth(leave));
        if (ramp <= 0.002f) return;

        float shortSide = MathF.Min(size.X, size.Y);
        Vector2 c = size * 0.5f;
        for (int i = 0; i < HazeCount; i++)
        {
            int s = _seed + i * 4217 + 91;
            float h = DrawHelpers.HashRange(s + 1, HazeHeightA, HazeHeightB) * px;
            float pf = MathF.Max(0.12f, 1f - h / dist);
            float perimeter = (i + DrawHelpers.Hash01(s + 2) * 0.8f) * (4f / HazeCount);
            Vector2 at0 = ScreenEdges.FromPerimeter(size, perimeter, -shortSide * 0.07f, out _);
            Vector2 drift = new(MathF.Sin(age * 0.11f + i) * 26f * px, MathF.Cos(age * 0.09f + i * 1.7f) * 20f * px);
            Vector2 at = cam.Project((at0.X - c.X) * pf + drift.X, (at0.Y - c.Y) * pf + drift.Y, h, out float scale);

            float period = DrawHelpers.HashRange(s + 3, 8f, 13f);
            float cycle = age / period + DrawHelpers.Hash01(s + 4);
            cycle -= MathF.Floor(cycle);

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = DrawHelpers.HashRange(s + 5, 440f, 780f) * px * scale, AgeRatio = cycle,
                Brightness = alpha * ramp * (0.06f + 0.62f * light + 0.18f * charge), Seed = s, Role = PrimitiveRole.Haze, ColorOverride = color,
            });
        }
    }

    // ---- the arcs ----

    private void EmitArcs(EffectScene scene, in LayerCamera cam, Vector2 size, float shortSide, float alpha, float age, float tau,
                          float leave, float charge, float quiet, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;
        Vector2 c = size * 0.5f;
        float overhang = shortSide * AnchorOverhang;
        float margin = AnchorMargin * px;
        float shockEnv = tau >= 0f ? MathF.Exp(-tau / 0.25f) * _fired.Power : 0f;
        int thrash = (int)(MathF.Max(tau, 0f) * 30f);
        // Idle streamers flicker at the edges even when nothing is about to happen; the charge lengthens and brightens them where the next shock will land.
        float stream = (0.22f + 0.78f * DrawHelpers.Smooth(charge)) * quiet;

        for (int i = 0; i < ArcCount; i++)
        {
            ref Arc a = ref _arcs[i];
            StrandPath path = _paths[i];

            // Layout is written as what the viewer sees; the plane coordinates that land it there are screen offsets scaled by the layer's perspective.
            // The anchors are then pushed out until they really are off-screen, however the camera has tilted, shaken or punched.
            float h = a.Height * px;
            float pf = MathF.Max(0.12f, 1f - h / dist);
            Vector2 A = KeepOffscreen((ScreenEdges.FromPerimeter(size, a.P0, overhang, out _) - c) * pf, h, in cam, size, margin);
            Vector2 B = KeepOffscreen((ScreenEdges.FromPerimeter(size, a.P0 + a.Span, overhang, out _) - c) * pf, h, in cam, size, margin);
            Vector2 toCentre = -(A + B) * 0.5f;
            float tcl = toCentre.Length();
            Vector2 inward = tcl > 1e-3f ? toCentre / tcl : Vector2.UnitY;
            Vector2 bow = inward * (a.Bulge * shortSide * pf * (1f + 0.10f * MathF.Sin(age * 0.9f + a.WobblePhase)));

            // The shock bends an arc hard and every which way for a moment (random each thirtieth of a second, not a sine); the rest of the time it only sways.
            float tw = Bolt.Lattice(a.Seed, 0, thrash) * TwitchAmp * px * pf * shockEnv;
            var wobble = new RingWobble(5f * px * pf + tw, 1, a.WobblePhase + 0.9f * age, 2f * px * pf + 0.4f * tw, 2, a.WobblePhase * 1.7f - 1.3f * age);
            SigilGeometry.Span(path, ArcSegments, in cam, A, B, bow, h, in wobble);
            float scale = cam.ScaleAt(h);
            _arcScale[i] = scale;
            MeasureVisible(i, path, size);

            // Where is this arc in its life?
            float arcTau = age - a.ZapStart;
            bool zapping = arcTau >= 0f && arcTau <= ZapSeconds;
            float leader = zapping ? a.ZapLeader : DefaultLeader;
            float reveal, level, glow, agit, widthMul, surgeAt, surgeGain;
            if (zapping)
            {
                Envelope(arcTau, a.ZapPower, leader, a.ZapRestrikes, out reveal, out level, out glow, out agit, out widthMul, out surgeAt, out surgeGain);
                agit = Saturate(agit * _fired.Violence);
            }
            else
            {
                // Between shocks: short dim streamers from the anchors of the arcs the next shock will strike, which come and go and lengthen as the charge climbs.
                reveal = level = glow = agit = surgeAt = surgeGain = 0f;
                widthMul = 0.6f;
                if (a.Active && a.StreamerOn && stream > 0.04f)
                {
                    int tick = (int)MathF.Floor(age * 9f + a.WobblePhase * 3f);
                    if (DrawHelpers.Hash01(a.Seed + tick * 37) < 0.12f + 0.65f * stream)
                    {
                        reveal = (0.15f + 0.20f * stream) * (0.5f + 0.5f * DrawHelpers.Hash01(a.Seed + tick * 53 + 1));
                        level = (0.14f + 0.50f * stream);
                        agit = 0.15f + 0.50f * stream;
                    }
                }
            }

            if (leave > 0f) reveal = MathF.Min(reveal, 1f - 0.85f * leave);
            _arcReveal[i] = reveal;
            _arcLevel[i] = level;
            if (reveal <= 0.001f || (level <= 0.01f && glow <= 0.01f)) continue;

            // Link count is pinned: the width follows the arc's length, so a chain never re-spaces its links as the stack zooms or tilts.
            float width = a.Width * widthMul * path.Length / (1.2f * HalfLinks);
            float bright = alpha * a.Bright * level;

            if (level > 0.01f)
                scene.AddStroke(new StrokePrimitive
                {
                    Path = path, Role = PrimitiveRole.MainStroke, Reveal = reveal, WidthHint = width,
                    Brightness = bright, Seed = a.Seed + 22, Phase = i * 0.7f, Depth = a.Depth, Agitation = agit,
                    TipFlare = reveal < 0.999f ? 1f : 0f, SurgeAt = surgeAt, SurgeGain = surgeGain, ColorOverride = color,
                });

            // The afterglow: the channel's heat, which outlasts the bolt. (Never on a partial strand: a glow cut off mid-screen shows its end.)
            if (reveal >= 0.999f && glow > 0.02f && leave <= 0f)
                scene.AddStroke(new StrokePrimitive
                {
                    Path = path, Role = PrimitiveRole.Halo, Reveal = 1f, WidthHint = width * HaloWidthOverArc,
                    Brightness = alpha * a.Bright * glow * 0.50f, Seed = a.Seed + 21, Phase = i * 0.9f + 2f, Depth = a.Depth, Agitation = 0.2f, ColorOverride = color,
                });

            // The anchors: a flare where each end meets the screen's edge, and on a strike a spray of sparks.
            if (level <= 0.01f) continue;
            bool haveA = ScreenEdges.TryFindEntry(path, size, out Vector2 pa, out Vector2 ia);
            bool streamer = reveal < 0.999f;
            Vector2 pb = default, ob = default;
            bool haveB = !streamer && ScreenEdges.TryFindExit(path, size, out pb, out ob);

            float strike = zapping ? MathF.Exp(-MathF.Max(0f, arcTau - leader) / 0.12f) : 0f;
            float flareSize = (streamer ? 7f : 9f + 40f * strike * a.ZapPower) * px * scale;
            float flareBright = alpha * a.Bright * (0.30f + 0.70f * MathF.Min(level, 1.6f));
            if (haveA) AddFlare(scene, pa + ia * (4f * px), flareSize, flareBright, a.Seed + 31, color);
            if (haveB) AddFlare(scene, pb - ob * (4f * px), flareSize, flareBright, a.Seed + 32, color);

            if (zapping && arcTau >= leader && a.StruckId != a.ZapId)
            {
                a.StruckId = a.ZapId;
                if (haveA) AddStrike(scene, pa, ia, 0.6f * a.ZapPower, alpha, a.Seed + 41 + a.ZapId, color);
                if (haveB) AddStrike(scene, pb, -ob, 0.4f * a.ZapPower, alpha, a.Seed + 42 + a.ZapId, color);
            }
        }
    }

    /// <summary>Records which stretch of an arc is on the screen (as fractions of its length, so crackles can be placed where they will be seen), or -1 if none of it is.</summary>
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

    /// <summary>
    /// Pushes a plane point outward (away from the centre) until, as the camera projects it, it sits at least <paramref name="margin"/> px beyond the nearest edge of the screen.
    /// The layout puts anchors just off-screen, but a tilted camera swings a point by its distance from the centre times the tilt, which on a wide screen (or in a shake) is enough to drag it far inside.
    /// </summary>
    private static Vector2 KeepOffscreen(Vector2 plane, float h, in LayerCamera cam, Vector2 size, float margin)
    {
        for (int it = 0; it < 5; it++)
        {
            Vector2 s = cam.Project(plane.X, plane.Y, h, out float scale);
            float inside = MathF.Min(MathF.Min(s.X, size.X - s.X), MathF.Min(s.Y, size.Y - s.Y));        // above 0 = on screen
            float need = inside + margin;                                                               // px still to travel outward
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

    // ---- the idle crackle ----

    /// <summary>
    /// Short crackles that hop off the arcs and die, placed on the part of each arc that is actually on screen: stateless, each slot re-rolls where it is every few hundred milliseconds.
    /// A few while the screen idles (near the edges, where the arcs come in), more as the charge builds, a swarm after a shock.
    /// </summary>
    private void EmitCrawlers(EffectScene scene, float alpha, float age, Vector2 size, float tau, float leave, float charge, float quiet, Vector4? color)
    {
        float ramp = DrawHelpers.Smooth((age - 0.20f) / 0.5f) * (1f - DrawHelpers.Smooth(leave));
        if (ramp <= 0.002f) return;
        float after = tau >= 0f ? MathF.Exp(-tau / 0.5f) * _fired.Power : 0f;
        float chance = 0.26f * quiet + 0.45f * charge + 0.90f * after;

        for (int slot = 0; slot < CrawlSlots; slot++)
        {
            float rate = 3.2f + 3.0f * DrawHelpers.Hash01(slot * 13 + 1);
            float x = age * rate + DrawHelpers.Hash01(slot * 7 + 2) * 10f;
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

            float side = DrawHelpers.Hash01(hs + 4) < 0.5f ? -1f : 1f;
            Vector2 dir = Bolt.Rotate(tan, side * (0.9f + 0.9f * DrawHelpers.Hash01(hs + 5)));
            float half = (9f + 8f * DrawHelpers.Hash01(hs + 6)) * _px * _arcScale[ai];

            scene.AddParticle(new ParticlePrimitive
            {
                Position = pos + dir * (half * 0.9f), Size = half, AgeRatio = f / 0.7f,
                Brightness = alpha * ramp * (0.30f * quiet + 0.50f * charge + 0.90f * lit),
                Seed = hs, Role = PrimitiveRole.Arc, Rotation = MathF.Atan2(dir.X, -dir.Y), ColorOverride = color,
            });
        }
    }

    /// <summary>
    /// Sparks that run round the edges of the screen: each is a few zigzag crackles in a row, a head and a fading tail, racing along the border a finger's width inside it, for about a second.
    /// A couple are always going while the screen idles; the charge sends more.
    /// </summary>
    private void EmitRunners(EffectScene scene, float alpha, float age, Vector2 size, float leave, float charge, float quiet, Vector4? color)
    {
        float ramp = DrawHelpers.Smooth((age - 0.6f) / 0.8f) * (1f - DrawHelpers.Smooth(leave)) * quiet;
        if (ramp <= 0.002f) return;
        float px = _px, perimeter = 2f * (size.X + size.Y);

        for (int slot = 0; slot < RunnerSlots; slot++)
        {
            float period = 2.2f + 1.6f * DrawHelpers.Hash01(slot * 17 + 3);
            float x = age / period + DrawHelpers.Hash01(slot * 29 + 5);
            int epoch = (int)MathF.Floor(x);
            float f = x - epoch;
            const float runFrac = 0.55f;                         // each cycle it runs for this share of the time, and rests for the rest
            if (f > runFrac) continue;

            int hs = _seed + slot * 7717 + epoch * 104729;
            if (DrawHelpers.Hash01(hs + 1) > 0.65f + 0.30f * charge) continue;

            float t = f / runFrac;
            float runSeconds = runFrac * period;
            float dir = DrawHelpers.Hash01(hs + 3) < 0.5f ? -1f : 1f;
            float speed = (520f + 520f * DrawHelpers.Hash01(hs + 2)) * px;
            float s = DrawHelpers.Hash01(hs + 4) * perimeter + dir * speed * t * runSeconds;
            float env = DrawHelpers.Smooth(t / 0.15f) * (1f - DrawHelpers.Smooth((t - 0.70f) / 0.30f));
            float inset = (6f + 10f * DrawHelpers.Hash01(hs + 5)) * px;
            float gap = 17f * px;
            float baseBright = alpha * ramp * env * (0.60f + 0.40f * charge);

            for (int k = 0; k < RunnerTrail; k++)
            {
                Vector2 p = PerimeterPoint(size, s - dir * k * gap, inset, out Vector2 tangent);
                float fall = MathF.Pow(1f - k / (float)RunnerTrail, 1.3f);
                scene.AddParticle(new ParticlePrimitive
                {
                    Position = p, Size = (13f + 2.9f * DrawHelpers.Hash01(hs + 6 + k)) * px, AgeRatio = 0.35f,
                    Brightness = baseBright * fall, Seed = hs + k * 131, Role = PrimitiveRole.Arc,
                    Rotation = MathF.Atan2(tangent.X, -tangent.Y), ColorOverride = color,
                });
            }
            AddFlare(scene, PerimeterPoint(size, s, inset, out _), 17f * px, baseBright * 0.9f, hs + 7, color);
        }
    }

    /// <summary>Static pops at the edges: a small crackle and a flash of light that lasts a fifth of a second, somewhere along the border, now and then.</summary>
    private void EmitPops(EffectScene scene, float alpha, float age, Vector2 size, float leave, float charge, float quiet, Vector4? color)
    {
        float ramp = DrawHelpers.Smooth((age - 0.6f) / 0.8f) * (1f - DrawHelpers.Smooth(leave)) * quiet;
        if (ramp <= 0.002f) return;
        float px = _px, perimeter = 2f * (size.X + size.Y);

        for (int slot = 0; slot < PopSlots; slot++)
        {
            float rate = 0.7f + 0.9f * DrawHelpers.Hash01(slot * 19 + 1);
            float x = age * rate + DrawHelpers.Hash01(slot * 23 + 2) * 10f;
            int epoch = (int)MathF.Floor(x);
            float f = x - epoch;
            if (f > 0.25f) continue;

            int hs = _seed + slot * 5381 + epoch * 7907;
            if (DrawHelpers.Hash01(hs + 1) > 0.55f + 0.40f * charge) continue;

            float inset = (8f + 40f * DrawHelpers.Hash01(hs + 3)) * px;
            Vector2 p = PerimeterPoint(size, DrawHelpers.Hash01(hs + 2) * perimeter, inset, out Vector2 tangent);
            float ar = f / 0.25f;
            float k = alpha * ramp * (0.55f + 0.45f * charge);
            float ang = DrawHelpers.Hash01(hs + 4) * MathF.Tau;

            scene.AddParticle(new ParticlePrimitive
            {
                Position = p, Size = (16f + 16f * DrawHelpers.Hash01(hs + 5)) * px, AgeRatio = ar, Brightness = k,
                Seed = hs, Role = PrimitiveRole.Arc, Rotation = ang, ColorOverride = color,
            });
            scene.AddParticle(new ParticlePrimitive
            {
                Position = p, Size = (12f + 8f * DrawHelpers.Hash01(hs + 6)) * px, AgeRatio = ar, Brightness = k * 0.85f,
                Seed = hs + 3, Role = PrimitiveRole.Flash, ColorOverride = color,
            });
        }
    }

    /// <summary>A point <paramref name="inset"/> px inside the screen's border, a distance s round it (clockwise from the top-left corner), and the border's direction there.</summary>
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

    /// <summary>Static charge hanging in the air at every depth: tiny sharp specks far off, big out-of-focus discs close to the lens. They twinkle, brighten as the charge builds and flare with a shock.</summary>
    private void EmitMotes(EffectScene scene, in LayerCamera cam, Vector2 size, float alpha, float age, float leave, float light, float charge, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;
        float ramp = DrawHelpers.Smooth((age - 0.20f) / 1.0f) * (1f - DrawHelpers.Smooth(leave));
        if (ramp <= 0.002f) return;
        float energy = MathF.Min(1.3f, 0.26f + 0.60f * charge + 0.80f * light);

        for (int i = 0; i < MoteCount; i++)
        {
            ref readonly Mote m = ref _motes[i];
            float z = m.Z * px;
            float s = dist / MathF.Max(dist * 0.12f, dist - z);

            // Motes rise at a world speed, so what is near crosses the screen faster; the frustum is wrapped in screen fractions.
            float halfH = size.Y * 0.5f;
            float fy = m.Fy - m.Speed * px * s * age / halfH;
            fy = ((fy + 1.12f) % 2.24f + 2.24f) % 2.24f - 1.12f;
            float fx = m.Fx + (m.Sway * px * s / (size.X * 0.5f)) * 0.5f * MathF.Sin(age * 0.43f + m.Phase);

            Vector2 at = cam.Project(fx * size.X * 0.5f / s, fy * halfH / s, z, out float scale);
            if (at.X < -80f || at.Y < -80f || at.X > size.X + 80f || at.Y > size.Y + 80f) continue;

            float blur = DrawHelpers.Smooth(150f, MoteNear, m.Z);
            float far = DrawHelpers.Smooth(MoteFar, 0f, m.Z);
            float twinkle = 0.65f + 0.35f * DrawHelpers.Hash01(m.Seed * 13 + (int)(age * 11f + m.Phase * 3f));
            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = m.Size * px * scale * (1f + 3.4f * blur), AgeRatio = 0.5f,
                Brightness = alpha * ramp * energy * twinkle * (0.35f + 0.65f * far) * (1f - 0.65f * blur), Seed = m.Seed, Role = PrimitiveRole.Mote, ColorOverride = color,
            });
        }
    }
}

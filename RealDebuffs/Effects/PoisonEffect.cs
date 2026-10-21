using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Poison: a living acid slime that spits onto the glass, then stays. Three globs fly in from the middle of the picture, growing as they near, and burst against the glass;
/// the splats spread into pools along the screen's edges and run down it. From the pools, tendrils of slime creep inward, swell at their heads, branch and draw back, and
/// ahead of all of it the glass is eaten dark and ragged. Bubbles blister in the thick of it and pop; fumes rise; thick liquid hangs from every underside and drips, and
/// long viscous threads stretch from the mass until they thin and snap. Every three seconds, the beat of a poison tick, it flares, swells and lunges.
/// </summary>
/// <remarks>
/// The mass itself is a <see cref="LiquidField"/>, which this effect animates and region.acid-slime draws; everything else is primitives placed on it by asking the field
/// where it is. A cast rolls a plan (where the spits land, which edges flood first, how many tendrils and where, how the pools lump) from its seed, so no two casts match.
/// </remarks>
public sealed class PoisonEffect : ISceneEffect, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Poison;
    public string DisplayName => "Poison";
    public string Description => "Acid slime spits onto the glass, then lives there: it pools along the edges, creeps inward in tendrils, eats the screen dark, and drips.";
    public int DrawOrder => 5;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Poison"] = 1.0f,
        ["Toxicosis"] = 1.0f,
        ["Accursed Poison"] = 1.0f,
        ["Allagan Venom"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "poison", "poisoned", "poisonous", "venom", "venomous", "toxic", "toxin", "toxins", "corrosive", "corrosion",
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke, "Acid threads",   "stroke.acid-thread", Hero: true),
        new("Stroke",   PrimitiveRole.Lip,        "Drip edges",     "stroke.drip-line"),
        new("Particle", PrimitiveRole.Goop,       "Acid",           "particle.acid", Hero: true),
        new("Particle", PrimitiveRole.Bubble,     "Bubbles",        "particle.bubble"),
        new("Particle", PrimitiveRole.Miasma,     "Fumes",          "particle.fume"),
        new("Region",   PrimitiveRole.MainStroke, "Acid mass",      "region.acid-slime", "EdgeGlow"),
    };

    // ---- how it plays ----
    private const float TickSeconds = 3.0f;                 // a poison tick lands every three seconds in this game
    private const float FlightSeconds = 0.40f;              // a spit's flight from the middle of the picture to the glass
    private const int MaxArms = 9, MaxBubbles = 40, MaxPops = 24, MaxFumes = 28, MaxDrops = 16;
    private const int ThreadCount = 5, ThreadNodes = 12, ThreadSamples = 40, LipCount = 10, LipPoints = 8;

    private readonly CastTracker _cast = new(envelope: true);
    private readonly LiquidField _field = new();
    private bool _leaving;                                      // the status is lifting: nothing new should start
    private int _seed;
    private Vector2 _layoutSize;

    // ---- the plan a cast rolls ----
    private readonly float[] _edgeSalt = new float[4], _edgeBase = new float[4];
    private readonly float[][] _seedU = { new float[3], new float[3], new float[3], new float[3] };
    private readonly float[][] _seedT = { new float[3], new float[3], new float[3], new float[3] };

    private struct Splat
    {
        public Vector2 Pos, From;
        public float Radius, Land, CrownPhase, RunLen, RunDrift;
        public int CrownA, CrownB;
        public bool Landed;
    }
    private readonly Splat[] _splats = new Splat[4];
    private int _splatCount;

    private struct Arm
    {
        public Vector2 Root, Dir, Head, HeadVel, Mid;
        public float Length, RootR, ShaftR, HeadR, Delay;
        public float W1, P1, P2, LatAmp, Bend, BendW, WaveRate, WaveCycles, WaveAmp;
        public float Reach, ReachVel, ReachGoal, NextGoal;
        public int Parent;               // -1 for a tendril off the pool; otherwise a branch off that arm
        public float ParentAt, Angle;    // where along the parent, and how far it turns from it
        public int Seed;
    }
    private readonly Arm[] _arms = new Arm[MaxArms];
    private int _armCount;

    // ---- the beat ----
    private float _nextTick, _tickAt = -99f, _tickStrength, _pulse, _flash;
    private Vector2 _tickOrigin;
    private int _tickCount;

    // ---- things that live on the mass ----
    private struct Bubble { public Vector2 Pos; public float Radius, Born, Life; public int Seed; public bool Live; }
    private readonly Bubble[] _bubbles = new Bubble[MaxBubbles];
    private struct Pop { public Vector2 Pos; public float Radius, Born; public int Seed; public bool Live; }
    private readonly Pop[] _pops = new Pop[MaxPops];
    private struct Fume { public Vector2 Pos, Vel; public float Size, Born, Life; public int Seed; public bool Live; }
    private readonly Fume[] _fumes = new Fume[MaxFumes];
    private struct Drop { public Vector2 Pos, Vel; public float Size, Born, Life; public int Seed; public bool Live; }
    private readonly Drop[] _drops = new Drop[MaxDrops];

    private sealed class Lip
    {
        public readonly StrandPath Path = new(LipPoints);
        public readonly float[] Y = new float[LipPoints];
        public float X, Half, Fade, NextSeat;
        public bool Active;
        public int Seed;
    }
    private readonly Lip[] _lips = new Lip[LipCount];

    private sealed class ThreadRig
    {
        public readonly VerletStrand Strand = new(ThreadNodes);
        public readonly StrandPath Path = new(ThreadSamples);
        public readonly Vector2[] Pose = new Vector2[ThreadNodes];
        public Vector2 Anchor;
        public float L, Lmax, Speed, W0, Fade, Recoil, NextSeat;
        public bool Active, Snapped;
        public int Seed;
    }
    private readonly ThreadRig[] _threads = new ThreadRig[ThreadCount];

    private float _bubbleDebt, _fumeDebt;

    public PoisonEffect()
    {
        for (int i = 0; i < LipCount; i++) _lips[i] = new Lip();
        for (int i = 0; i < ThreadCount; i++) _threads[i] = new ThreadRig();
    }

    private static float ShortSide(Vector2 size) => MathF.Min(size.X, size.Y);

    // ================================================================ per frame ================================================================

    public void Emit(EffectScene scene, Vector2 size, float alpha, float time, float dt, Vector4? colorOverride)
    {
        if (size.X < 64f || size.Y < 64f) return;
        dt = MathF.Min(dt, 0.05f);

        // Use the current CastFrame contract so the effect follows the post-refactor lifecycle and fade envelope.
        var f = _cast.Frame(size, alpha, time, dt, colorOverride);
        if (f.Fresh || f.Resized)
        {
            _seed = f.Seed;
            BuildLayout(_seed, size);
        }

        float age = f.Age;
        alpha = f.Alpha;                                      // the envelope speeds the first spit through the manager's fade-in
        float gate = f.Gate;                                  // full strength from the first frame, for the cast's own flash
        _leaving = f.Leaving;
        float px = ShortSide(size) / 1080f;
        float recede = 1f - DrawHelpers.Smooth(f.Leave);      // as the status lifts the mass draws back, rather than just dimming

        StepBeat(age, size, px);
        StepSplats(scene, age, size, px, alpha, colorOverride);
        FillPools(age, size, px, recede);
        FillBlobsAndArms(age, dt, recede);

        _field.Time = age;
        _field.WarpAmp = (22f + 9f * _pulse) * px;
        _field.WarpFreq = 1f / (240f * px); _field.CrinkleFreq = 1f / (64f * px);
        _field.EtchGain = (0.82f + 0.48f * (1f - MathF.Exp(-age / 16f)) + 0.55f * _pulse) * (0.4f + 0.6f * recede);
        _field.Throb = MathF.Max(_pulse * 0.9f, _flash * 0.7f);
        _field.Solve(size, 15f * px);

        scene.AddRegion(new RegionPrimitive
        {
            Min = Vector2.Zero, Max = size, Bottom = true, Alpha = alpha,
            Tint = ToxinColor.Vignette, ColorOverride = colorOverride, State = _field,
        });
        scene.Wash(size, ToxinColor.Flash, 0.16f * _flash * gate, colorOverride);

        StepLips(scene, age, dt, px, alpha, colorOverride);
        StepThreads(scene, age, dt, size, px, alpha, colorOverride);
        StepBubbles(scene, age, dt, size, px, alpha, colorOverride);
        StepFumes(scene, age, dt, size, px, alpha, colorOverride);
        StepDrops(scene, age, dt, size, px, alpha, colorOverride);

        float vig = 0.30f * DrawHelpers.Saturate(age / 0.9f) + 0.20f * _pulse + 0.20f * _flash;
        scene.RequestVignette(ToxinColor.Vignette, 0.14f, alpha * vig, priority: 20, colorOverride);
        _flash *= MathF.Exp(-dt / 0.16f);
    }

    // ================================================================ the plan ================================================================

    private void BuildLayout(int seed, Vector2 size)
    {
        _layoutSize = size;
        float ss = ShortSide(size);
        float H(int salt, float lo, float hi) => DrawHelpers.HashRange(seed + salt, lo, hi);

        // Pools: the bottom lies deepest (liquid runs down), the top shallowest; each edge lumps differently.
        float[] edgeMul = { 0.78f, 0.95f, 1.30f, 0.95f };       // Top, Right, Bottom, Left (ScreenEdge order)
        for (int e = 0; e < 4; e++)
        {
            _edgeSalt[e] = H(10 + e, 0f, 200f);
            _edgeBase[e] = 0.046f * edgeMul[e] * H(20 + e, 0.8f, 1.3f);
        }

        // Spits: where the globs land, kept out of the middle of the picture, spread so they never pile up.
        _splatCount = H(150, 0f, 1f) < 0.35f ? 4 : 3;
        float startAng = H(151, 0f, MathF.Tau);
        for (int i = 0; i < _splatCount; i++)
        {
            int s = seed + 200 + i * 41;
            float ang = startAng + MathF.Tau * i / _splatCount + DrawHelpers.HashRange(s, -0.55f, 0.55f);
            float rad = DrawHelpers.HashRange(s + 1, 0.32f, 0.46f);
            Vector2 land = size * 0.5f + new Vector2(MathF.Cos(ang) * size.X, MathF.Sin(ang) * size.Y) * rad;
            land = Vector2.Clamp(land, size * 0.08f, size * 0.92f);
            _splats[i] = new Splat
            {
                Pos = land,
                From = size * 0.5f + new Vector2(DrawHelpers.HashRange(s + 8, -0.06f, 0.06f) * size.X, DrawHelpers.HashRange(s + 9, -0.08f, 0.04f) * size.Y),
                Radius = ss * DrawHelpers.HashRange(s + 2, 0.058f, 0.098f),
                Land = 0.42f + i * DrawHelpers.HashRange(s + 3, 0.16f, 0.30f),
                CrownPhase = DrawHelpers.HashRange(s + 4, 0f, MathF.Tau),
                CrownA = 6 + (int)(DrawHelpers.Hash01(s + 5) * 4f), CrownB = 11 + (int)(DrawHelpers.Hash01(s + 6) * 5f),
                RunLen = ss * DrawHelpers.HashRange(s + 7, 0.09f, 0.20f),
                RunDrift = DrawHelpers.HashRange(s + 10, -0.12f, 0.12f),
            };
        }

        // Which edge floods from where: late seeds so no edge is left dry, then the nearest edge to each spit takes its seed from the spit's own point.
        for (int e = 0; e < 4; e++)
            for (int k = 0; k < 3; k++) { _seedU[e][k] = H(30 + e * 3 + k, 0.05f, 0.95f); _seedT[e][k] = 1.2f + k * 0.9f + H(50 + e * 3 + k, 0f, 1.4f); }
        for (int i = 0; i < _splatCount; i++)
        {
            ScreenEdge edge = ScreenEdges.Nearest(size, _splats[i].Pos);
            bool horiz = edge == ScreenEdge.Top || edge == ScreenEdge.Bottom;
            _seedU[(int)edge][0] = horiz ? _splats[i].Pos.X / size.X : _splats[i].Pos.Y / size.Y;
            _seedT[(int)edge][0] = _splats[i].Land + 0.05f;
        }

        // Tendrils: off different edges where it can, at an angle, long or short; branches off the longest.
        _armCount = 0;
        int firstEdge = (int)(H(399, 0f, 1f) * 4f) % 4;
        int main = H(398, 0f, 1f) < 0.5f ? 5 : 6;
        for (int i = 0; i < main; i++)
        {
            int s = seed + 400 + i * 53;
            // The first four tendrils each take a different edge, so the screen is not lopsided; any more go where they like.
            ScreenEdge edge = (ScreenEdge)(i < 4 ? (firstEdge + i) % 4 : (int)(DrawHelpers.Hash01(s + 30) * 4f) % 4);
            bool side = edge == ScreenEdge.Left || edge == ScreenEdge.Right;
            float overhang = ss * 0.10f;
            ref Arm a = ref _arms[_armCount++];
            a = default;
            a.Seed = s; a.Parent = -1;
            a.Root = ScreenEdges.Anchor(size, edge, DrawHelpers.HashRange(s + 1, 0.12f, 0.88f), overhang);
            Vector2 inward = ScreenEdges.Inward(edge);
            float turn = DrawHelpers.HashRange(s + 20, -0.55f, 0.55f);
            a.Dir = new Vector2(inward.X * MathF.Cos(turn) - inward.Y * MathF.Sin(turn), inward.X * MathF.Sin(turn) + inward.Y * MathF.Cos(turn));
            bool longOne = i < 2;
            a.Length = ss * (longOne ? DrawHelpers.HashRange(s + 2, 0.32f, side ? 0.42f : 0.36f) : DrawHelpers.HashRange(s + 2, 0.17f, 0.28f)) + overhang;
            a.ShaftR = ss * DrawHelpers.HashRange(s + 3, 0.0125f, 0.0200f);
            a.HeadR = a.ShaftR * DrawHelpers.HashRange(s + 4, 1.25f, 1.65f);
            a.RootR = ss * DrawHelpers.HashRange(s + 5, 0.040f, 0.062f);
            a.Delay = DrawHelpers.HashRange(s + 6, 1.0f, 2.8f);
            a.W1 = DrawHelpers.HashRange(s + 7, 0.34f, 0.62f);
            a.P1 = DrawHelpers.HashRange(s + 9, 0f, MathF.Tau); a.P2 = DrawHelpers.HashRange(s + 10, 0f, MathF.Tau);
            a.LatAmp = a.Length * DrawHelpers.HashRange(s + 11, 0.08f, 0.20f);
            a.Bend = DrawHelpers.HashRange(s + 12, 0.10f, 0.22f) * (DrawHelpers.Hash01(s + 21) < 0.5f ? -1f : 1f);
            a.BendW = DrawHelpers.HashRange(s + 13, 0.28f, 0.52f);
            a.WaveRate = DrawHelpers.HashRange(s + 14, 1.1f, 2.0f);
            a.WaveCycles = DrawHelpers.HashRange(s + 15, 1.5f, 2.5f);
            a.WaveAmp = DrawHelpers.HashRange(s + 16, 0.09f, 0.17f);
            a.Reach = 0.55f; a.ReachGoal = DrawHelpers.HashRange(s + 17, 0.65f, 1f);
            a.NextGoal = a.Delay + DrawHelpers.HashRange(s + 18, 3f, 6f);
            a.Head = a.Root;
        }
        // Branches: a smaller tendril off each of the two longest, turned away from it.
        for (int p = 0; p < 2 && _armCount < MaxArms; p++)
        {
            int s = seed + 700 + p * 61;
            ref Arm parent = ref _arms[p];
            ref Arm a = ref _arms[_armCount++];
            a = default;
            a.Seed = s; a.Parent = p;
            a.ParentAt = DrawHelpers.HashRange(s + 1, 0.42f, 0.70f);
            a.Angle = DrawHelpers.HashRange(s + 2, 0.55f, 0.95f) * (DrawHelpers.Hash01(s + 3) < 0.5f ? -1f : 1f);
            a.Length = parent.Length * DrawHelpers.HashRange(s + 4, 0.34f, 0.52f);
            a.ShaftR = parent.ShaftR * DrawHelpers.HashRange(s + 5, 0.66f, 0.82f);
            a.HeadR = a.ShaftR * DrawHelpers.HashRange(s + 6, 1.25f, 1.55f);
            a.RootR = a.ShaftR * 1.5f;
            a.Delay = parent.Delay + DrawHelpers.HashRange(s + 7, 1.6f, 3.4f);
            a.W1 = DrawHelpers.HashRange(s + 8, 0.40f, 0.70f);
            a.P1 = DrawHelpers.HashRange(s + 9, 0f, MathF.Tau); a.P2 = DrawHelpers.HashRange(s + 10, 0f, MathF.Tau);
            a.LatAmp = a.Length * DrawHelpers.HashRange(s + 11, 0.10f, 0.22f);
            a.Bend = DrawHelpers.HashRange(s + 12, 0.08f, 0.18f) * (DrawHelpers.Hash01(s + 21) < 0.5f ? -1f : 1f);
            a.BendW = DrawHelpers.HashRange(s + 13, 0.30f, 0.55f);
            a.WaveRate = DrawHelpers.HashRange(s + 14, 1.2f, 2.2f); a.WaveCycles = DrawHelpers.HashRange(s + 15, 1.2f, 2.0f); a.WaveAmp = DrawHelpers.HashRange(s + 16, 0.08f, 0.15f);
            a.Reach = 0.4f; a.ReachGoal = DrawHelpers.HashRange(s + 17, 0.55f, 1f);
            a.NextGoal = a.Delay + DrawHelpers.HashRange(s + 18, 3f, 6f);
        }

        // The beat: the first tick lands a little after the spits have spread.
        _nextTick = H(600, 2.5f, 3.3f); _tickAt = -99f; _pulse = 0f; _flash = 0f; _tickCount = 0;

        Array.Clear(_bubbles); Array.Clear(_pops); Array.Clear(_fumes); Array.Clear(_drops);
        for (int i = 0; i < LipCount; i++)
        {
            _lips[i].Active = false; _lips[i].Fade = 0f;
            _lips[i].NextSeat = H(610, 1.6f, 2.8f) + i * 0.15f;
            _lips[i].Seed = unchecked(seed * 17 + i * 7919 + 11);
        }
        for (int i = 0; i < ThreadCount; i++)
        {
            _threads[i].Active = false; _threads[i].Fade = 0f;
            _threads[i].NextSeat = H(620, 3.5f, 6.5f) + i * 1.7f;
            _threads[i].Seed = unchecked(seed * 29 + i * 6151 + 5);
        }
        _bubbleDebt = _fumeDebt = 0f;
    }

    // ================================================================ the beat ================================================================

    private void StepBeat(float age, Vector2 size, float px)
    {
        if (age >= _nextTick && !_leaving) FireTick(age, size, px);

        float tau = age - _tickAt;
        _pulse = tau >= 0f && tau < 4f ? _tickStrength * (1f - MathF.Exp(-tau / 0.05f)) * MathF.Exp(-tau / 0.55f) : 0f;
    }

    /// <summary>One poison tick. A cast rolls its own plan for it: where it starts, how hard, and whether one tendril bites or the whole mass lunges.</summary>
    private void FireTick(float age, Vector2 size, float px)
    {
        int s = unchecked(_seed + 900 + _tickCount * 131);
        _tickCount++;
        _tickAt = age;
        _tickStrength = DrawHelpers.HashRange(s, 0.62f, 1.0f);
        _nextTick = age + TickSeconds + DrawHelpers.HashRange(s + 1, -0.22f, 0.22f);

        // Where it starts: a tendril's head, or a stretch of pool.
        int live = 0, pick = -1;
        for (int i = 0; i < _armCount; i++) if (_arms[i].Reach > 0.3f && age > _arms[i].Delay + 1f) live++;
        if (live > 0 && DrawHelpers.Hash01(s + 2) < 0.65f)
        {
            int want = (int)(DrawHelpers.Hash01(s + 3) * live) % live, n = 0;
            for (int i = 0; i < _armCount; i++)
                if (_arms[i].Reach > 0.3f && age > _arms[i].Delay + 1f) { if (n++ == want) { pick = i; break; } }
        }
        bool bite = pick >= 0 && DrawHelpers.Hash01(s + 4) < 0.55f;
        if (pick >= 0) _tickOrigin = _arms[pick].Head;
        else
        {
            int e = (int)(DrawHelpers.Hash01(s + 5) * 4f) % 4;
            _tickOrigin = ScreenEdges.Anchor(size, (ScreenEdge)e, DrawHelpers.HashRange(s + 6, 0.1f, 0.9f), -ShortSide(size) * 0.05f);
        }

        // The mass lunges: every tendril a little, or the one that was chosen, hard.
        for (int i = 0; i < _armCount; i++)
        {
            ref Arm a = ref _arms[i];
            if (bite) { if (i == pick || a.Parent == pick) { a.ReachVel += 1.6f * _tickStrength; a.ReachGoal = 1f; a.NextGoal = age + 3f; } }
            else a.ReachVel += 0.55f * _tickStrength * DrawHelpers.HashRange(s + 20 + i, 0.5f, 1.2f);
        }

        // A burst of bubbles where it started, a puff of fumes, a shake through the threads.
        int bubbles = 5 + (int)(DrawHelpers.Hash01(s + 7) * 5f);
        for (int i = 0; i < bubbles; i++)
        {
            for (int tries = 0; tries < 6; tries++)
            {
                float ang = DrawHelpers.HashRange(s + 30 + i * 3 + tries, 0f, MathF.Tau), rad = DrawHelpers.HashRange(s + 31 + i * 3 + tries, 0f, 70f * px);
                Vector2 at = _tickOrigin + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rad;
                if (_field.Bilinear(at) < 0.95f) continue;
                SpawnBubble(at, DrawHelpers.HashRange(s + 32 + i, 4.5f, 12f) * px, age + DrawHelpers.HashRange(s + 33 + i, 0f, 0.35f), DrawHelpers.HashRange(s + 34 + i, 0.45f, 1.05f), s + i);
                break;
            }
        }
        for (int i = 0; i < 4; i++) SpawnFumeAtRim(age, _tickOrigin, 110f * px, px, s + 50 + i * 17, 1.15f);
        foreach (var t in _threads)
            if (t.Active && !t.Snapped)
                for (int n = 3; n < ThreadNodes; n++) t.Strand.Impulse(n, new Vector2(DrawHelpers.HashRange(s + 60 + n, -1f, 1f) * 240f * px * _tickStrength, 0f));
        _flash = MathF.Max(_flash, 0.50f * _tickStrength);
    }

    // ================================================================ the spits ================================================================

    private void StepSplats(EffectScene scene, float age, Vector2 size, float px, float alpha, Vector4? color)
    {
        float ss = ShortSide(size);
        for (int i = 0; i < _splatCount; i++)
        {
            ref Splat sp = ref _splats[i];
            float flightStart = sp.Land - FlightSeconds;

            // A glob in flight: small and far at first, swelling as it comes to the glass, streaked along its path.
            if (age >= flightStart && age < sp.Land)
            {
                float tau = (age - flightStart) / FlightSeconds;
                float e = MathF.Pow(tau, 2.1f);
                Vector2 pos = Vector2.Lerp(sp.From, sp.Pos, e);
                Vector2 vel = (sp.Pos - sp.From) * (0.75f * MathF.Pow(tau, 1.1f) / FlightSeconds);       // streaked, but a glob, not a tadpole
                scene.AddParticle(new ParticlePrimitive
                {
                    Position = pos, Velocity = vel, Size = px * (5f + 44f * MathF.Pow(tau, 1.5f)) * (sp.Radius / (ss * 0.078f)),
                    Brightness = alpha * (0.35f + 0.65f * tau), Seed = unchecked(_seed + i * 977), Role = PrimitiveRole.Goop, ColorOverride = color,
                });
            }

            // The splat: it bursts, the glass flashes, and a fan of droplets leaves the point of impact.
            if (!sp.Landed && age >= sp.Land)
            {
                sp.Landed = true;
                _flash = MathF.Max(_flash, 0.9f);
                for (int k = 0; k < 3; k++)
                {
                    float a = sp.CrownPhase + k * MathF.Tau / 3f;
                    scene.AddImpact(new ImpactPrimitive
                    {
                        StrokeRole = PrimitiveRole.MainStroke, Position = sp.Pos, Direction = new Vector2(MathF.Cos(a), MathF.Sin(a)),
                        Strength = 1f, Brightness = alpha, Seed = unchecked(_seed + i * 313 + k), ColorOverride = color,
                    });
                }
                for (int k = 0; k < 5; k++) SpawnFumeAtRim(age, sp.Pos, sp.Radius * 1.5f, px, unchecked(_seed + i * 71 + k * 13), 1.3f);
            }
        }
    }

    // ================================================================ the mass ================================================================

    private void FillPools(float age, Vector2 size, float px, float recede)
    {
        float ss = ShortSide(size);
        float creep = 0.80f + 0.20f * (1f - MathF.Exp(-age / 14f));
        float swell = 1f + 0.26f * _pulse;
        for (int e = 0; e < 4; e++)
        {
            float edgeLen = e % 2 == 0 ? size.X : size.Y;
            float[] d = _field.PoolDepth[e];
            for (int k = 0; k < LiquidField.PoolSamples; k++)
            {
                float u = k / (float)(LiquidField.PoolSamples - 1);
                // Mostly shallow, with deep lobes; deeper still where two edges meet, since that is where liquid gathers.
                float n = 0.5f + 0.5f * Noise.Fbm(u * 2.6f + _edgeSalt[e], age * 0.05f + _edgeSalt[e] * 0.31f, 2);
                float lobe = DrawHelpers.Smooth(0.40f, 0.82f, n);
                float lobes = 2.6f * lobe * MathF.Sqrt(lobe);                // nothing between the lobes: a pool is a few heavy masses, not a hem
                float corner = 1f + 1.6f * (MathF.Exp(-u * 7f) + MathF.Exp(-(1f - u) * 7f));
                float breathe = 1f + 0.10f * MathF.Sin(age * 0.9f + _edgeSalt[e] + u * 5f);

                // The front of the flood, travelling along the edge from where a spit landed.
                float spread = 0f;
                for (int s = 0; s < 3; s++)
                {
                    float front = (age - _seedT[e][s]) * 780f * px;
                    float dpx = MathF.Abs(u - _seedU[e][s]) * edgeLen;
                    // The front races along the edge as a thin film and the liquid thickens behind it far more slowly: it is viscous, and a pool should not swell by a hand's width a frame.
                    spread = MathF.Max(spread, DrawHelpers.Smooth((front - dpx) / (560f * px)));
                }
                d[k] = _edgeBase[e] * ss * lobes * corner * breathe * spread * creep * swell * recede;
            }
        }
    }

    private void FillBlobsAndArms(float age, float dt, float recede)
    {
        // ---- splats, and the streaks they run down the glass ----
        _field.BlobCount = 0;
        for (int i = 0; i < _splatCount; i++)
        {
            ref Splat s = ref _splats[i];
            float tau = age - s.Land;
            if (tau < 0f) continue;
            float burst = 1f - MathF.Pow(1f - DrawHelpers.Saturate(tau / 0.30f), 3f);
            float over = 1f + 0.18f * MathF.Exp(-tau / 0.25f) * MathF.Sin(tau * 14f);
            float fade = 1f - DrawHelpers.Smooth((tau - 5.5f) / 5f);
            float gain = recede * fade * DrawHelpers.Saturate(tau / 0.06f);
            // It spreads, then thins as it runs: the middle of the picture is not where this lives for long.
            float thin = 1f - 0.45f * DrawHelpers.Smooth((tau - 2.5f) / 5f);
            float rad = s.Radius * (0.30f + 0.70f * burst) * over * (1f + 0.22f * (1f - MathF.Exp(-tau / 2.5f))) * thin;

            _field.Blobs[_field.BlobCount++] = new LiquidField.Blob
            {
                Center = s.Pos, Tail = s.Pos, Radius = rad, Gain = gain,
                CrownAmp = 0.20f + 0.50f * MathF.Exp(-tau / 0.9f), CrownPhase = s.CrownPhase + tau * 0.03f, CrownA = s.CrownA, CrownB = s.CrownB,
            };

            // What runs: a thin trail from the splat that ends in a swelling head, sliding down and a little to one side.
            float run = s.RunLen * (1f - MathF.Exp(-tau / 3.0f)) * DrawHelpers.Smooth(tau / 0.5f);
            if (run > 3f && _field.BlobCount < _field.Blobs.Length)
            {
                _field.Blobs[_field.BlobCount++] = new LiquidField.Blob
                {
                    Center = s.Pos + new Vector2(0f, rad * 0.2f), Tail = s.Pos + new Vector2(s.RunDrift * run, run * 1.05f + rad * 0.2f),
                    Radius = s.Radius * 0.20f, TailTaper = -1.30f, Gain = gain * DrawHelpers.Smooth(tau / 0.9f),
                };
            }
        }

        // ---- tendrils ----
        _field.ArmCount = 0;
        for (int i = 0; i < _armCount; i++)
        {
            ref Arm a = ref _arms[i];
            float t = age - a.Delay;
            if (t < 0f) continue;
            StepArm(ref a, t, dt, age);

            float grow = DrawHelpers.Smooth(t / 1.5f) * recede;
            if (a.Parent >= 0) grow *= DrawHelpers.Smooth(_arms[a.Parent].Reach / 0.5f);
            Vector2 chord = a.Head - a.Root;
            float cl = chord.Length();
            Vector2 perp = cl > 1e-3f ? new Vector2(-chord.Y, chord.X) / cl : Vector2.UnitX;
            a.Mid = (a.Root + a.Head) * 0.5f + perp * (a.Bend * MathF.Sin(a.BendW * t + a.P1) * cl);

            float swell = 1f + 0.16f * _pulse;
            _field.Arms[_field.ArmCount++] = new LiquidField.Arm
            {
                Root = a.Root, Mid = a.Mid, Head = a.Head,
                RootRadius = a.RootR, ShaftRadius = a.ShaftR * swell, HeadRadius = a.HeadR * swell,
                WaveAmp = a.WaveAmp, WaveCycles = a.WaveCycles, WavePhase = a.WaveRate * age + a.P2,
                Gain = grow,
            };
        }
    }

    private void StepArm(ref Arm a, float t, float dt, float age)
    {
        // A branch grows off its parent and follows it.
        if (a.Parent >= 0)
        {
            ref readonly Arm p = ref _arms[a.Parent];
            Vector2 at = PolyPoint(p.Root, p.Mid, p.Head, a.ParentAt);
            Vector2 heading = p.Head - p.Root;
            float hl = heading.Length();
            heading = hl > 1e-3f ? heading / hl : p.Dir;
            float c = MathF.Cos(a.Angle), s = MathF.Sin(a.Angle);
            a.Root = at;
            a.Dir = new Vector2(heading.X * c - heading.Y * s, heading.X * s + heading.Y * c);
            if (a.Head == default) a.Head = at;
        }

        if (age >= a.NextGoal)
        {
            int salt = a.Seed + (int)(age * 7f);
            a.ReachGoal = DrawHelpers.HashRange(salt, 0.32f, 1.0f);
            a.NextGoal = age + DrawHelpers.HashRange(salt + 3, 3.5f, 8f);
        }
        // A spring: it lunges, overshoots a little and settles, rather than easing mechanically.
        const float k = 2.4f;
        a.ReachVel += ((a.ReachGoal - a.Reach) * k * k - a.ReachVel * 2f * k) * dt;
        a.Reach = Math.Clamp(a.Reach + a.ReachVel * dt, 0.05f, 1.25f);
        float reach = DrawHelpers.Smooth(t / 1.7f) * a.Reach;

        Vector2 perp = new(-a.Dir.Y, a.Dir.X);
        float lat = a.LatAmp * (0.8f * MathF.Sin(a.W1 * t + a.P1) + 0.35f * MathF.Sin(a.W1 * 2.3f * t + a.P2));
        // The head is pulled a little down the glass: slime sags.
        Vector2 goal = a.Root + a.Dir * (a.Length * reach) + perp * lat * reach + new Vector2(0f, a.Length * 0.09f * reach);
        goal = KeepOutOfMiddle(goal);
        const float w = 2.0f;
        a.HeadVel += ((goal - a.Head) * (w * w) - a.HeadVel * (2f * w)) * dt;
        a.Head += a.HeadVel * dt;
    }

    /// <summary>The middle of the picture is where the player is looking: a tendril's head is turned aside at the edge of an ellipse round it.</summary>
    private Vector2 KeepOutOfMiddle(Vector2 p)
    {
        Vector2 c = _layoutSize * 0.5f, r = _layoutSize * 0.23f;
        Vector2 q = (p - c) / r;
        float l = q.Length();
        if (l >= 1f) return p;
        return c + (l > 1e-3f ? q / l : Vector2.UnitY) * r;
    }

    private static Vector2 PolyPoint(Vector2 a, Vector2 m, Vector2 h, float t) =>
        t < 0.5f ? Vector2.Lerp(a, m, t * 2f) : Vector2.Lerp(m, h, (t - 0.5f) * 2f);

    // ================================================================ what hangs from it ================================================================

    /// <summary>The y where, moving down the column x from y0, the mass ends (inside above, outside below) nearest nearY; false if it does not end there.</summary>
    private bool FindBottomEdge(float x, float y0, float y1, float nearY, out float y)
    {
        y = 0f;
        bool found = false; float best = float.MaxValue;
        const float step = 3f;
        float prev = _field.Bilinear(new Vector2(x, y0));
        for (float yy = y0 + step; yy <= y1; yy += step)
        {
            float cur = _field.Bilinear(new Vector2(x, yy));
            if (prev >= LiquidField.Iso && cur < LiquidField.Iso)
            {
                float cy = yy - step + (LiquidField.Iso - prev) / (cur - prev) * step;
                float d = MathF.Abs(cy - nearY);
                if (d < best) { best = d; y = cy; found = true; }
            }
            prev = cur;
        }
        return found;
    }

    private void StepLips(EffectScene scene, float age, float dt, float px, float alpha, Vector4? color)
    {
        foreach (var lip in _lips)
        {
            if (!lip.Active)
            {
                if (age >= lip.NextSeat && !_leaving) SeatLip(lip, age, px);
                continue;
            }

            // Follow the underside of the mass as it moves: each point of the path re-finds the edge near where it was.
            int lost = 0;
            for (int i = 0; i < LipPoints; i++)
            {
                float x = lip.X + (i / (float)(LipPoints - 1) * 2f - 1f) * lip.Half;
                if (FindBottomEdge(x, lip.Y[i] - 46f * px, lip.Y[i] + 46f * px, lip.Y[i], out float y)) lip.Y[i] += (y - lip.Y[i]) * MathF.Min(1f, dt * 14f);
                else lost++;
            }
            bool fading = lost > 1;
            lip.Fade = DrawHelpers.Saturate(lip.Fade + (fading ? -dt * 4f : dt * 2f));
            if (fading && lip.Fade <= 0f)
            {
                lip.Active = false;
                lip.NextSeat = age + DrawHelpers.HashRange(lip.Seed + (int)(age * 3f), 0.4f, 2.2f);
                continue;
            }

            for (int i = 0; i < LipPoints; i++)
                lip.Path.Points[i] = new Vector2(lip.X + (i / (float)(LipPoints - 1) * 2f - 1f) * lip.Half, lip.Y[i] + 1.4f * px);
            lip.Path.Count = LipPoints;
            lip.Path.BuildArc();

            scene.AddStroke(new StrokePrimitive
            {
                Path = lip.Path, Role = PrimitiveRole.Lip, Reveal = lip.Fade > 0.5f ? 1f : 0f, WidthHint = 3f * px,
                Brightness = alpha * lip.Fade, Seed = lip.Seed, ColorOverride = color,
            });
        }
    }

    private void SeatLip(Lip lip, float age, float px)
    {
        int s = unchecked(lip.Seed + (int)(age * 10f));
        lip.NextSeat = age + 0.35f;
        float x = DrawHelpers.HashRange(s, _layoutSize.X * 0.05f, _layoutSize.X * 0.95f);

        // Every place down this column where the mass ends: take one.
        Span<float> found = stackalloc float[4];
        int n = 0;
        float prev = _field.Bilinear(new Vector2(x, 0f));
        for (float y = 4f; y < _layoutSize.Y * 0.95f && n < 4; y += 4f)
        {
            float cur = _field.Bilinear(new Vector2(x, y));
            if (prev >= LiquidField.Iso && cur < LiquidField.Iso) found[n++] = y - 4f + 4f * (LiquidField.Iso - prev) / (cur - prev);
            prev = cur;
        }
        if (n == 0) return;

        float yEdge = found[(int)(DrawHelpers.Hash01(s + 1) * n) % n];
        // It must be an underside: the edge faces down, not sideways.
        float gy = _field.Bilinear(new Vector2(x, yEdge + 8f)) - _field.Bilinear(new Vector2(x, yEdge - 8f));
        float gx = _field.Bilinear(new Vector2(x + 14f * px, yEdge)) - _field.Bilinear(new Vector2(x - 14f * px, yEdge));
        if (gy > -0.04f || MathF.Abs(gx) > 1.4f * MathF.Abs(gy)) return;

        foreach (var other in _lips)
            if (other != lip && other.Active && MathF.Abs(other.X - x) < other.Half + 36f * px && MathF.Abs(other.Y[LipPoints / 2] - yEdge) < 80f * px) return;

        lip.X = x; lip.Half = DrawHelpers.HashRange(s + 2, 26f, 78f) * px;
        for (int i = 0; i < LipPoints; i++)
        {
            float xi = x + (i / (float)(LipPoints - 1) * 2f - 1f) * lip.Half;
            lip.Y[i] = FindBottomEdge(xi, yEdge - 40f * px, yEdge + 40f * px, yEdge, out float ye) ? ye : yEdge;
        }
        lip.Active = true; lip.Fade = 0f;
    }

    // ---- threads: the viscous strings the mass pulls out of itself ----

    private static float ThreadWidth(ThreadRig t, float px) => MathF.Max(2.0f * px, t.W0 * px * MathF.Sqrt(24f * px / MathF.Max(24f * px, t.L)));

    private void StepThreads(EffectScene scene, float age, float dt, Vector2 size, float px, float alpha, Vector4? color)
    {
        foreach (var t in _threads)
        {
            if (!t.Active)
            {
                if (age >= t.NextSeat && !_leaving) SeatThread(t, age, size, px);
                continue;
            }

            if (!t.Snapped)
            {
                // A thread draws out slowly, then faster as it thins: the heavier it gets, the quicker it gives.
                t.L += t.Speed * px * (1f + t.L / (110f * px)) * dt;
                t.Fade = DrawHelpers.Saturate(t.Fade + dt * 1.6f);
                // It clings to the lip it hangs from, and goes with it as the mass moves; if the mass pulls away altogether, it lets go.
                bool orphaned = !FindBottomEdge(t.Anchor.X, t.Anchor.Y - 40f * px, t.Anchor.Y + 70f * px, t.Anchor.Y + 8f * px, out float lipY);
                if (!orphaned) t.Anchor.Y += (lipY - 8f * px - t.Anchor.Y) * MathF.Min(1f, dt * 9f);
                float w = t.W0 * px * MathF.Sqrt(24f * px / MathF.Max(24f * px, t.L));
                if (t.L >= t.Lmax * px || w <= 2.1f * px || orphaned) Snap(t, age, px, w);
            }
            else
            {
                // It lets go: what was below falls, and what is left snaps back up into the mass.
                t.Recoil += dt;
                t.L *= MathF.Exp(-dt / 0.085f);
                t.Fade = DrawHelpers.Saturate(1f - t.Recoil / 0.40f);
                if (t.Recoil > 0.45f)
                {
                    t.Active = false;
                    t.NextSeat = age + DrawHelpers.HashRange(t.Seed + (int)(age * 5f), 1.2f, 4.2f);
                    continue;
                }
            }

            t.Strand.Length = MathF.Max(8f * px, t.L);
            t.Strand.SetEnds(t.Anchor, t.Anchor + new Vector2(0f, t.L));
            t.Strand.Step(dt);
            t.Strand.FillPath(t.Path, ThreadSamples);

            float stretch = t.Lmax > 0f ? DrawHelpers.Saturate(t.L / (t.Lmax * px)) : 0f;
            scene.AddStroke(new StrokePrimitive
            {
                Path = t.Path, Role = PrimitiveRole.MainStroke, Reveal = 1f, WidthHint = t.Snapped ? t.W0 * px * 0.25f : ThreadWidth(t, px),
                Brightness = alpha * t.Fade, TipFlare = t.Snapped ? 0f : 0.25f + 0.75f * stretch * stretch,
                Seed = t.Seed, Phase = age * 0.15f, Agitation = t.Snapped ? 1f : 0f, ColorOverride = color,
            });
        }
    }

    private void SeatThread(ThreadRig t, float age, Vector2 size, float px)
    {
        int s = unchecked(t.Seed + (int)(age * 10f));
        t.NextSeat = age + 0.5f;
        float x = DrawHelpers.HashRange(s, size.X * 0.06f, size.X * 0.94f);

        // The first underside from the top of the screen down that faces the floor.
        float prev = _field.Bilinear(new Vector2(x, 0f));
        for (float y = 4f; y < size.Y * 0.78f; y += 4f)
        {
            float cur = _field.Bilinear(new Vector2(x, y));
            if (prev >= LiquidField.Iso && cur < LiquidField.Iso)
            {
                float yEdge = y - 4f + 4f * (LiquidField.Iso - prev) / (cur - prev);
                float gy = _field.Bilinear(new Vector2(x, yEdge + 8f)) - _field.Bilinear(new Vector2(x, yEdge - 8f));
                float gx = _field.Bilinear(new Vector2(x + 14f * px, yEdge)) - _field.Bilinear(new Vector2(x - 14f * px, yEdge));
                if (gy > -0.05f || MathF.Abs(gx) > 1.2f * MathF.Abs(gy)) return;
                foreach (var o in _threads) if (o != t && o.Active && MathF.Abs(o.Anchor.X - x) < 150f * px) return;

                t.Anchor = new Vector2(x, yEdge - 8f * px);
                t.L = 22f * px; t.Lmax = DrawHelpers.HashRange(s + 1, 120f, 330f); t.Speed = DrawHelpers.HashRange(s + 2, 11f, 21f);
                t.W0 = DrawHelpers.HashRange(s + 3, 8f, 13f);
                t.Fade = 0f; t.Recoil = 0f; t.Snapped = false; t.Active = true;
                t.Strand.Gravity = new Vector2(0f, 1500f * px); t.Strand.Drag = 2.4f; t.Strand.BendStiffness = 0.18f; t.Strand.Iterations = 8;
                t.Strand.PinStart = true; t.Strand.PinEnd = false;
                for (int i = 0; i < ThreadNodes; i++) t.Pose[i] = t.Anchor + new Vector2(0f, t.L * i / (ThreadNodes - 1));
                t.Strand.Place(t.Pose);
                return;
            }
            prev = cur;
        }
    }

    private void Snap(ThreadRig t, float age, float px, float width)
    {
        t.Snapped = true; t.Recoil = 0f;
        Vector2 tip = t.Strand.Pos[ThreadNodes - 1];
        for (int i = 0; i < MaxDrops; i++)
            if (!_drops[i].Live)
            {
                _drops[i] = new Drop
                {
                    Live = true, Pos = tip, Vel = new Vector2(DrawHelpers.HashRange(t.Seed + 9, -18f, 18f) * px, 80f * px), Born = age, Life = 2.2f,
                    Size = MathF.Max(2.4f * px, width * 1.05f), Seed = t.Seed + 5,
                };
                break;
            }
    }

    // ---- bubbles ----

    private void SpawnBubble(Vector2 at, float radius, float born, float life, int seed)
    {
        for (int i = 0; i < MaxBubbles; i++)
            if (!_bubbles[i].Live) { _bubbles[i] = new Bubble { Live = true, Pos = at, Radius = radius, Born = born, Life = life, Seed = seed }; return; }
    }

    private void StepBubbles(EffectScene scene, float age, float dt, Vector2 size, float px, float alpha, Vector4? color)
    {
        // A steady blister of them: mostly flecks of foam, now and then a big one, and often a few together.
        if (age > 1.2f)
        {
            _bubbleDebt += _leaving ? 0f : dt * 3.1f * alpha;
            while (_bubbleDebt >= 1f)
            {
                _bubbleDebt -= 1f;
                int s = unchecked(_seed + (int)(age * 977f));
                for (int tries = 0; tries < 8; tries++)
                {
                    Vector2 at = new(DrawHelpers.HashRange(s + tries * 3, 0.02f, 0.98f) * size.X, DrawHelpers.HashRange(s + tries * 3 + 1, 0.02f, 0.98f) * size.Y);
                    if (_field.Bilinear(at) < 0.95f) continue;
                    float kind = DrawHelpers.Hash01(s + tries * 3 + 2);
                    float r = (kind < 0.10f ? DrawHelpers.HashRange(s + 91, 12f, 22f) : kind < 0.40f ? DrawHelpers.HashRange(s + 92, 5f, 9.5f) : DrawHelpers.HashRange(s + 94, 2.2f, 4.4f)) * px;
                    float life = DrawHelpers.HashRange(s + 93, 1.3f, 3.6f);
                    SpawnBubble(at, r, age, life, s + tries);
                    // Foam gathers: a few smaller ones beside it.
                    if (DrawHelpers.Hash01(s + 95) < 0.38f)
                        for (int k = 0, n = 2 + (int)(DrawHelpers.Hash01(s + 96) * 3f); k < n; k++)
                        {
                            float ang = DrawHelpers.HashRange(s + 100 + k, 0f, MathF.Tau), dist = (r + DrawHelpers.HashRange(s + 110 + k, 3f, 14f) * px);
                            Vector2 nb = at + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * dist;
                            if (_field.Bilinear(nb) >= 0.95f) SpawnBubble(nb, DrawHelpers.HashRange(s + 120 + k, 1.8f, 5.2f) * px, age, life * DrawHelpers.HashRange(s + 130 + k, 0.6f, 1.1f), s + 140 + k);
                        }
                    break;
                }
            }
        }

        for (int i = 0; i < MaxBubbles; i++)
        {
            ref Bubble b = ref _bubbles[i];
            if (!b.Live) continue;
            float tau = age - b.Born;
            if (tau < 0f) continue;
            float ratio = tau / b.Life;
            // A bubble needs the mass under it: if the liquid draws back, it bursts early.
            bool orphan = _field.Bilinear(b.Pos) < 0.78f;
            if (ratio >= 1f || orphan || alpha < 0.15f)
            {
                BurstBubble(scene, in b, age, alpha, color);
                b.Live = false;
                continue;
            }
            // The surface carries them slowly along.
            b.Pos += new Vector2(Noise.Value(b.Pos.X * 0.006f + age * 0.1f, b.Pos.Y * 0.006f) * 7f, Noise.Value(b.Pos.X * 0.006f, b.Pos.Y * 0.006f - age * 0.1f) * 7f + 3f) * px * dt;
            scene.AddParticle(new ParticlePrimitive
            {
                Position = b.Pos, Size = b.Radius, AgeRatio = ratio, Brightness = alpha, Seed = b.Seed, Role = PrimitiveRole.Bubble, Variant = 0, ColorOverride = color,
            });
        }

        for (int i = 0; i < MaxPops; i++)
        {
            ref Pop p = ref _pops[i];
            if (!p.Live) continue;
            float k = (age - p.Born) / 0.22f;
            if (k >= 1f) { p.Live = false; continue; }
            scene.AddParticle(new ParticlePrimitive
            {
                Position = p.Pos, Size = p.Radius, AgeRatio = k, Brightness = alpha, Seed = p.Seed, Role = PrimitiveRole.Bubble, Variant = 1, ColorOverride = color,
            });
        }
    }

    private void BurstBubble(EffectScene scene, in Bubble b, float age, float alpha, Vector4? color)
    {
        for (int i = 0; i < MaxPops; i++)
            if (!_pops[i].Live) { _pops[i] = new Pop { Live = true, Pos = b.Pos, Radius = b.Radius, Born = age, Seed = b.Seed }; break; }
        // A bubble throws a little liquid as it goes: more for a big one.
        scene.AddImpact(new ImpactPrimitive
        {
            StrokeRole = PrimitiveRole.MainStroke, Position = b.Pos, Direction = new Vector2(DrawHelpers.HashRange(b.Seed, -0.5f, 0.5f), -1f),
            Strength = Math.Clamp(0.12f + b.Radius * 0.012f, 0.1f, 0.5f), Brightness = alpha, Seed = b.Seed, ColorOverride = color,
        });
    }

    // ---- fumes ----

    private void SpawnFume(float age, Vector2 at, float px, int seed, float sizeScale)
    {
        for (int i = 0; i < MaxFumes; i++)
            if (!_fumes[i].Live)
            {
                _fumes[i] = new Fume
                {
                    Live = true, Pos = at, Born = age, Seed = seed,
                    Vel = new Vector2(DrawHelpers.HashRange(seed, -9f, 9f), DrawHelpers.HashRange(seed + 1, -26f, -10f)) * px,
                    Size = DrawHelpers.HashRange(seed + 2, 20f, 40f) * px * sizeScale, Life = DrawHelpers.HashRange(seed + 3, 2.4f, 4.4f),
                };
                return;
            }
    }

    /// <summary>Fumes rise off the edge of the liquid, not out of the middle of it: tries a few points round <paramref name="around"/> for one on the rim.</summary>
    private void SpawnFumeAtRim(float age, Vector2 around, float spread, float px, int seed, float sizeScale)
    {
        for (int tries = 0; tries < 8; tries++)
        {
            float ang = DrawHelpers.HashRange(seed + tries * 3, 0f, MathF.Tau), rad = DrawHelpers.HashRange(seed + tries * 3 + 1, 0.3f, 1f) * spread;
            Vector2 at = around + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rad;
            float f = _field.Bilinear(at);
            if (f < 0.30f || f > 0.75f) continue;
            SpawnFume(age, at, px, seed + tries, sizeScale);
            return;
        }
    }

    private void StepFumes(EffectScene scene, float age, float dt, Vector2 size, float px, float alpha, Vector4? color)
    {
        if (age > 1.6f)
        {
            _fumeDebt += _leaving ? 0f : dt * 2.2f * alpha;
            while (_fumeDebt >= 1f)
            {
                _fumeDebt -= 1f;
                int s = unchecked(_seed + (int)(age * 613f));
                for (int tries = 0; tries < 8; tries++)
                {
                    Vector2 at = new(DrawHelpers.HashRange(s + tries * 2, 0.02f, 0.98f) * size.X, DrawHelpers.HashRange(s + tries * 2 + 1, 0.02f, 0.98f) * size.Y);
                    float f = _field.Bilinear(at);
                    if (f < 0.35f || f > 0.7f) continue;       // up off the edge of the mass, not out of the middle of it
                    SpawnFume(age, at, px, s + tries, 1f);
                    break;
                }
            }
        }

        for (int i = 0; i < MaxFumes; i++)
        {
            ref Fume f = ref _fumes[i];
            if (!f.Live) continue;
            float ratio = (age - f.Born) / f.Life;
            if (ratio >= 1f) { f.Live = false; continue; }
            f.Pos += f.Vel * dt;
            f.Vel *= MathF.Exp(-dt * 0.35f);
            scene.AddParticle(new ParticlePrimitive
            {
                Position = f.Pos, Size = f.Size, AgeRatio = ratio, Brightness = alpha, Seed = f.Seed, Role = PrimitiveRole.Miasma, ColorOverride = color,
            });
        }
    }

    // ---- drops that have let go of a thread ----

    private void StepDrops(EffectScene scene, float age, float dt, Vector2 size, float px, float alpha, Vector4? color)
    {
        for (int i = 0; i < MaxDrops; i++)
        {
            ref Drop d = ref _drops[i];
            if (!d.Live) continue;
            if (age - d.Born > d.Life || d.Pos.Y > size.Y + 60f) { d.Live = false; continue; }
            d.Vel += new Vector2(0f, 1900f * px) * dt;
            d.Vel *= MathF.Exp(-dt * 1.2f);
            d.Pos += d.Vel * dt;
            scene.AddParticle(new ParticlePrimitive
            {
                Position = d.Pos, Velocity = d.Vel, Size = d.Size, Brightness = alpha, Seed = d.Seed, Role = PrimitiveRole.Goop, ColorOverride = color,
            });
        }
    }
}

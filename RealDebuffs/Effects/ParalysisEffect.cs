using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Paralysis: a cage of live electricity closing round the screen. A dozen arcs hang between anchors on the screen's edge at four depths (hazy and far away behind the screen, crisp and huge in front of it) and slide over one another as the stack tilts; the shock slams them on from the edges, then they crackle on, surge by turns, and every 6-10 seconds the whole cage convulses. The arcs are the hero: made of chains, rope or tentacles they keep the glow, the twitch and the sparks, and a particle in a "made of" phrase replaces the sparks, crackles and static.</summary>
public sealed class ParalysisEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Paralysis;
    public string DisplayName => "Paralysis";
    public string Description => "A cage of crackling electricity snaps shut around the screen, then keeps arcing and convulsing: arcs at four depths, sparks, static, amber light.";
    public int DrawOrder => 7;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Paralysis"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "paralysis", "paralyzed", "paralysed", "shocked", "electrified", "twitching"
    };

    // The arcs are what a "made of X" phrase replaces; a particle in that phrase also replaces what crackles and sparks off them. The static in the air stays amber (it is still a swappable slot): swapped, its out-of-focus foreground specks become big blank discs.
    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke),
        new("Particle", PrimitiveRole.Arc),
        new("Particle", PrimitiveRole.Spark),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke, "Arcs",         "stroke.lightning"),
        new("Stroke",   PrimitiveRole.Halo,       "Arc glow",     "stroke.corona"),
        new("Particle", PrimitiveRole.Arc,        "Crackle",      "particle.arc"),
        new("Particle", PrimitiveRole.Spark,      "Sparks",       "particle.spark"),
        new("Particle", PrimitiveRole.Mote,       "Static",       "particle.static"),
        new("Particle", PrimitiveRole.Haze,       "Thunderhead",  "particle.thunderhead"),
        new("Region",   PrimitiveRole.MainStroke, "Storm light",  "region.edge-glow", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke, "Strike flash", "region.flat-fill", "FlatFill"),
    };

    // ---- the camera ----
    private const float CameraDistance = 1500f;     // px at 1080p; smaller = stronger perspective
    private const float BaseTilt = 0.10f;           // radians the stack leans from the line of sight
    private const float TiltSway = 0.030f;
    private const float PrecessionRate = 0.30f;     // rad/s the lean swings round
    private const float LurchTilt = 0.16f;          // extra lean as the shock lands, settling over LurchSeconds
    private const float LurchSeconds = 0.70f;
    private const float JoltTilt = 0.045f;          // lean kicked by a convulsion, ringing out in a quarter second
    private const float JoltShake = 9f;             // px at 1080p the whole cage is thrown sideways by a convulsion
    private const float IntroShake = 14f;

    // ---- the cast ----
    private const float LockSeconds = 0.44f;        // the last arc is on: the cage locks and convulses for the first time
    private const float IntroDur = 0.16f;           // seconds a leader takes to run an arc's length
    private const float SpasmDecay = 0.55f;         // how long a convulsion's flare takes to die to 1/e
    private const float SpasmMin = 6f, SpasmMax = 10f;

    // ---- the arcs ----
    private const int ArcSegments = 40;
    private const float HalfLinks = 26f;            // a chain gets 2x this many links however long the arc is (see ArcWidth)
    private const float HaloWidthOverArc = 2.2f;
    private const float AnchorOverhang = 0.05f;     // of the short side: anchors sit just off the screen
    private const float TwitchAmp = 30f;            // px at 1080p a convulsion bends an arc sideways, at first

    private readonly record struct LayerSpec(int Count, float Height, float Depth, float SpanMin, float SpanMax,
                                             float BulgeMin, float BulgeMax, float Bright, float Width, float Delay);

    // Heights are px toward the viewer (negative = behind the screen plane); span is in screen perimeters (1 = one edge), bulge a fraction of the short side.
    private static readonly LayerSpec[] Layers =
    {
        new(3, -360f, 0.70f, 0.75f, 1.05f, 0.06f, 0.12f, 0.42f, 0.62f, 0.34f),   // far: hazy, thin, wide sweeps
        new(3, -170f, 0.38f, 0.50f, 0.80f, 0.05f, 0.10f, 0.70f, 0.80f, 0.20f),   // back
        new(4,    0f, 0.06f, 0.36f, 0.60f, 0.04f, 0.08f, 1.00f, 1.00f, 0.02f),   // the screen plane: the cage proper
        new(2,  150f, 0.00f, 0.28f, 0.46f, 0.03f, 0.06f, 1.15f, 1.25f, 0.10f),   // near: big, bright, crisp
    };
    private const int ArcCount = 12;

    // ---- sprinkled on top ----
    private const int CrawlSlots = 16;
    private const int MoteCount = 34;
    private const float MoteNear = 650f, MoteFar = -420f;
    private const int HazeCount = 8;
    private const float HazeHeightA = -430f, HazeHeightB = -260f;

    private static readonly uint FlashTint = StormColor.Flash;
    private static readonly uint NumbTint = DrawHelpers.Pack(0.050f, 0.025f, 0.004f);
    private static readonly uint StormLight = DrawHelpers.Pack(1.00f, 0.66f, 0.14f);

    private struct Arc
    {
        public float P0, Span, Bulge, Height, Depth, Bright, Width, Delay, Period, SurgePhase, WobblePhase;
        public int Seed;
        public bool StruckA, StruckB;
    }

    private struct Mote
    {
        public float Fx, Fy, Z, Speed, Sway, Phase, Size;
        public int Seed;
    }

    private readonly Arc[] _arcs = new Arc[ArcCount];
    private readonly StrandPath[] _paths = new StrandPath[ArcCount];
    private readonly float[] _arcScale = new float[ArcCount], _arcBright = new float[ArcCount], _arcReveal = new float[ArcCount];
    private readonly Mote[] _motes = new Mote[MoteCount];

    private readonly CastTracker _cast = new();
    private int _seed, _spasmCount;
    private float _azimuth, _azimuthDir, _nextSpasm, _spasmAt, _spasmAngle, _rampLead, _lastAlpha, _peakAlpha;
    private bool _leaving, _spasmPending;
    private float _px = 1f;

    public ParalysisEffect()
    {
        for (int i = 0; i < ArcCount; i++) _paths[i] = new StrandPath(ArcSegments + 1);
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        if (_cast.Begin(time))
            BeginCast(unchecked((int)(_cast.Start * 1000f)), dt);

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

        // The manager fades alpha in from nothing over FadeInSeconds, which would hide the strike. The fade is linear, so alpha * FadeInSeconds / rampAge is the full strength:
        // ride the ramp up to three times faster (punch), and let the cast's own flash ignore it (gate). The fade-out is left alone.
        float gate = alpha;
        if (!_leaving && age + _rampLead < EffectManager.FadeInSeconds)
        {
            float toFull = EffectManager.FadeInSeconds / MathF.Max(age + _rampLead, 0.001f);
            gate = MathF.Min(alpha * toFull, 1f);
            alpha *= MathF.Min(3f, toFull);
        }

        // The cage locks at LockSeconds, then convulses again every SpasmMin..SpasmMax seconds. Not while it is discharging.
        if (!_leaving && age >= _nextSpasm)
        {
            _spasmAt = age;
            _spasmPending = true;
            _spasmAngle = DrawHelpers.Hash01(_seed + _spasmCount * 61 + 4) * MathF.Tau;
            _spasmCount++;
            _nextSpasm = age + DrawHelpers.HashRange(_seed + _spasmCount * 97 + 6, SpasmMin, SpasmMax);
        }
        float sinceSpasm = age - _spasmAt;
        float spasm = sinceSpasm >= 0f ? MathF.Exp(-sinceSpasm / SpasmDecay) : 0f;

        // The camera: it leans in on the shock, and a convulsion throws it sideways and rings its tilt.
        float jolt = sinceSpasm >= 0f ? MathF.Exp(-sinceSpasm / 0.14f) : 0f;
        Vector2 kick = new Vector2(MathF.Cos(_spasmAngle), MathF.Sin(_spasmAngle)) * (MathF.Sin(sinceSpasm * 36f) * jolt * JoltShake * _px);
        Vector2 introShake = new Vector2(MathF.Cos(_seed * 0.01f), MathF.Sin(_seed * 0.01f)) * (MathF.Sin(age * 38f) * MathF.Exp(-age / 0.18f) * IntroShake * _px);
        float tilt = BaseTilt + TiltSway * MathF.Sin(age * 0.27f + 1.1f)
                   + LurchTilt * MathF.Exp(-age / LurchSeconds) + 0.08f * leave
                   + (sinceSpasm >= 0f ? JoltTilt * MathF.Exp(-sinceSpasm / 0.25f) * MathF.Cos(sinceSpasm * 22f) : 0f);
        float azimuth = _azimuth + _azimuthDir * PrecessionRate * age;
        var cam = new LayerCamera(center + kick + introShake, CameraDistance * _px, tilt, azimuth);

        float distant = DistantFlash(age);

        EmitAtmosphere(scene, screenSize, shortSide, alpha, gate, age, spasm, distant, colorOverride);
        EmitHaze(scene, in cam, screenSize, alpha, age, leave, distant, colorOverride);
        EmitArcs(scene, in cam, screenSize, shortSide, alpha, age, leave, spasm, sinceSpasm, colorOverride);
        EmitCrawlers(scene, alpha, age, screenSize, leave, colorOverride);
        EmitMotes(scene, in cam, screenSize, alpha, age, leave, colorOverride);

        _spasmPending = false;
    }

    // ---- the cast ----

    private void BeginCast(int seed, float dt)
    {
        _seed = seed;
        _rampLead = dt;
        _azimuth = DrawHelpers.Hash01(seed + 1) * MathF.Tau;
        _azimuthDir = DrawHelpers.Hash01(seed + 2) < 0.5f ? -1f : 1f;
        _spasmCount = 0;
        _spasmAt = -100f;
        _spasmPending = false;
        _nextSpasm = LockSeconds;
        _leaving = false;
        _peakAlpha = 0f;
        _lastAlpha = 0f;

        // Each layer's arcs are spread round the whole perimeter from a random start, so the screen is always covered evenly and never the same way twice.
        int n = 0;
        for (int l = 0; l < Layers.Length; l++)
        {
            ref readonly LayerSpec L = ref Layers[l];
            float start = DrawHelpers.Hash01(seed + 30 + l) * 4f;
            for (int i = 0; i < L.Count; i++, n++)
            {
                int s = seed + n * 7919 + 13;
                float slot = 4f / L.Count;
                _arcs[n] = new Arc
                {
                    P0 = start + slot * i + DrawHelpers.HashRange(s + 1, -0.12f, 0.12f) * slot,
                    Span = DrawHelpers.HashRange(s + 2, L.SpanMin, L.SpanMax),
                    Bulge = DrawHelpers.HashRange(s + 3, L.BulgeMin, L.BulgeMax),
                    Height = L.Height,
                    Depth = L.Depth,
                    Bright = L.Bright,
                    Width = L.Width,
                    Delay = L.Delay + 0.045f * i + DrawHelpers.Hash01(s + 4) * 0.03f,
                    Period = DrawHelpers.HashRange(s + 5, 2.4f, 6.0f),
                    SurgePhase = DrawHelpers.Hash01(s + 6) * 6f,
                    WobblePhase = DrawHelpers.Hash01(s + 7) * MathF.Tau,
                    Seed = s,
                };
            }
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
    }

    /// <summary>Sheet lightning somewhere far off in the cloud deck: a flash and a weaker afterglow, at most one every 2.6 s and never in step with anything else.</summary>
    private float DistantFlash(float age)
    {
        float x = age / 2.6f + DrawHelpers.Hash01(_seed + 5) * 5f;
        int e = (int)MathF.Floor(x);
        if (DrawHelpers.Hash01(_seed + e * 31 + 7) > 0.62f) return 0f;

        float t = (x - e) * 2.6f - DrawHelpers.Hash01(_seed + e * 31 + 8) * 1.5f;
        if (t < 0f) return 0f;
        float main = MathF.Exp(-t / 0.12f);
        float after = t > 0.17f ? 0.55f * MathF.Exp(-(t - 0.17f) / 0.12f) : 0f;
        return MathF.Max(main, after);
    }

    // ---- atmosphere ----

    private void EmitAtmosphere(EffectScene scene, Vector2 size, float shortSide, float alpha, float castGate, float age,
                                float spasm, float distant, Vector4? color)
    {
        float castIn = DrawHelpers.Smooth(age / 0.45f);

        scene.RequestVignette(StormColor.Vignette, 0.14f, alpha * 0.30f * castIn, priority: 12, color);

        // Light from the arcs spills onto the screen's edges. It follows a smoothed measure of how much has recently flashed, never an individual flicker.
        float light = DrawHelpers.Saturate(0.16f + 0.30f * MathF.Exp(-age / 0.55f) + 0.22f * spasm + 0.30f * distant);
        float depth = shortSide * 0.12f;
        float a = 0.10f * alpha * castIn * (0.55f + 1.6f * light);
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new Vector2(size.X, depth), Tint = StormLight, Alpha = a, Top = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(0f, size.Y - depth), Max = size, Tint = StormLight, Alpha = a, Bottom = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new Vector2(depth, size.Y), Tint = StormLight, Alpha = a, Left = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(size.X - depth, 0f), Max = size, Tint = StormLight, Alpha = a, Right = true, ColorOverride = color });

        // The shock itself: one flash, then the world going numb for a beat. (The cast, so castGate rather than alpha: the manager's fade-in would swallow it.)
        float flash = age < 0.40f ? MathF.Exp(-age / 0.09f) : 0f;
        if (flash > 0.01f)
            scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = FlashTint, Alpha = 0.30f * flash * castGate, ColorOverride = color });

        float numb = DrawHelpers.Smooth(age / 0.15f) * (1f - DrawHelpers.Smooth((age - 0.25f) / 0.9f));
        if (numb > 0.01f)
            scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = NumbTint, Alpha = 0.26f * numb * castGate, ColorOverride = color });

        // A convulsion flares the whole view, softly.
        if (spasm > 0.02f)
            scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = FlashTint, Alpha = 0.07f * spasm * alpha, ColorOverride = color });
    }

    /// <summary>The far layer: a deck of dark cloud lit from inside by distant flashes. Puffs hang behind the screen at different depths, so they slide against the arcs as the stack tilts.</summary>
    private void EmitHaze(EffectScene scene, in LayerCamera cam, Vector2 size, float alpha, float age, float leave, float distant, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;
        float ramp = DrawHelpers.Smooth((age - 0.45f) / 1.2f) * (1f - DrawHelpers.Smooth(leave));
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
                Position = at, Size = DrawHelpers.HashRange(s + 5, 230f, 400f) * px * scale, AgeRatio = cycle,
                Brightness = alpha * ramp * (0.36f + 0.80f * distant), Seed = s, Role = PrimitiveRole.Haze, ColorOverride = color,
            });
        }
    }

    // ---- the arcs ----

    private void EmitArcs(EffectScene scene, in LayerCamera cam, Vector2 size, float shortSide, float alpha, float age, float leave,
                          float spasm, float sinceSpasm, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;
        Vector2 c = size * 0.5f;
        float overhang = shortSide * AnchorOverhang;
        float twitch = sinceSpasm >= 0f ? MathF.Sin(sinceSpasm * 28f) * MathF.Exp(-sinceSpasm / 0.28f) * TwitchAmp * px : 0f;

        for (int i = 0; i < ArcCount; i++)
        {
            ref Arc a = ref _arcs[i];
            StrandPath path = _paths[i];

            // Layout is written as what the viewer sees; the plane coordinates that land it there are screen offsets scaled by the layer's perspective.
            float h = a.Height * px;
            float pf = MathF.Max(0.12f, 1f - h / dist);
            Vector2 A = (ScreenEdges.FromPerimeter(size, a.P0, overhang, out _) - c) * pf;
            Vector2 B = (ScreenEdges.FromPerimeter(size, a.P0 + a.Span, overhang, out _) - c) * pf;
            Vector2 toCentre = -(A + B) * 0.5f;
            float tcl = toCentre.Length();
            Vector2 inward = tcl > 1e-3f ? toCentre / tcl : Vector2.UnitY;
            Vector2 bow = inward * (a.Bulge * shortSide * pf * (1f + 0.10f * MathF.Sin(age * 0.9f + a.WobblePhase)));

            // At rest an arc sways a little; a convulsion bends it hard and rings out.
            float idle1 = 7f * px * pf, idle2 = 3f * px * pf, tw = twitch * pf;
            var wobble = new RingWobble(idle1 + tw, 1, a.WobblePhase + 0.9f * age, idle2 + 0.4f * tw, 2, a.WobblePhase * 1.7f - 1.3f * age);
            SigilGeometry.Span(path, ArcSegments, in cam, A, B, bow, h, in wobble);

            // The strike: a leader runs the arc's length; a flare follows it, then the arc settles. While the cage discharges the arc is reeled back in.
            float reveal = DrawHelpers.Saturate((age - a.Delay) / IntroDur);
            if (leave > 0f) reveal = MathF.Min(reveal, 1f - 0.85f * leave);
            float scale = cam.ScaleAt(h);
            _arcScale[i] = scale; _arcReveal[i] = reveal;
            if (reveal <= 0.001f) { _arcBright[i] = 0f; continue; }

            float sinceLanded = age - a.Delay - IntroDur;
            float landFlare = sinceLanded >= 0f ? MathF.Exp(-sinceLanded / 0.16f) : 0f;
            float surgeT = (age + a.SurgePhase) % a.Period;
            float surge = age > 0.9f ? MathF.Exp(-surgeT * 3.0f) : 0f;
            float level = a.Bright * (1f + 1.2f * landFlare + 0.7f * surge + 0.9f * spasm);
            float bright = alpha * level;
            float agit = DrawHelpers.Saturate(0.15f + 0.9f * MathF.Max(0.7f * surge, spasm) + 0.8f * landFlare);
            _arcBright[i] = level;

            // Link count is pinned: the width follows the arc's length, so a chain never re-spaces its links as the stack zooms or tilts.
            float width = a.Width * path.Length / (1.2f * HalfLinks);

            scene.AddStroke(new StrokePrimitive
            {
                Path = path, Role = PrimitiveRole.Halo, Reveal = reveal, WidthHint = width * HaloWidthOverArc,
                Brightness = bright * 0.55f, Seed = a.Seed + 21, Phase = i * 0.9f + 2f, Depth = a.Depth, Agitation = agit, ColorOverride = color,
            });
            scene.AddStroke(new StrokePrimitive
            {
                Path = path, Role = PrimitiveRole.MainStroke, Reveal = reveal, WidthHint = width,
                Brightness = bright, Seed = a.Seed + 22, Phase = i * 0.7f, Depth = a.Depth, Agitation = agit,
                TipFlare = reveal < 0.999f ? 1f : 0f, ColorOverride = color,
            });

            if (reveal < 0.999f || leave > 0f) continue;

            // Where each end touches the screen's edge: a blazing node, and a spray of sparks the first time the arc lands and on some convulsions.
            bool haveA = ScreenEdges.TryFindEntry(path, size, out Vector2 pa, out Vector2 ia);
            bool haveB = ScreenEdges.TryFindExit(path, size, out Vector2 pb, out Vector2 ob);
            float nodeFlare = MathF.Max(landFlare, spasm);
            float nodeSize = (5.5f + 5f * nodeFlare) * px * scale;
            float nodeBright = alpha * a.Bright * (0.75f + 0.5f * nodeFlare);
            if (haveA) AddNode(scene, pa + ia * (6f * px), nodeSize, nodeBright, a.Seed + 31, color);
            if (haveB) AddNode(scene, pb - ob * (6f * px), nodeSize, nodeBright, a.Seed + 32, color);

            if (haveA && !a.StruckA) { a.StruckA = true; AddStrike(scene, pa, ia, 0.5f, alpha, a.Seed + 41, color); }
            if (haveB && !a.StruckB) { a.StruckB = true; AddStrike(scene, pb, -ob, 0.35f, alpha, a.Seed + 42, color); }

            if (_spasmPending && (i + _spasmCount) % 3 == 0)
            {
                if (((i + _spasmCount) & 1) == 0) { if (haveA) AddStrike(scene, pa, ia, 0.40f, alpha, a.Seed + 51 + _spasmCount, color); }
                else if (haveB) AddStrike(scene, pb, -ob, 0.40f, alpha, a.Seed + 52 + _spasmCount, color);
            }
        }
    }

    private static void AddNode(EffectScene scene, Vector2 at, float size, float brightness, int seed, Vector4? color) =>
        scene.AddParticle(new ParticlePrimitive
        {
            Position = at, Size = size, AgeRatio = 0.5f, Brightness = brightness,
            Seed = seed * 8, Role = PrimitiveRole.Mote, ColorOverride = color,      // a multiple of 8: a mote draws its flare when Seed & 7 == 0, and an anchor always should
        });

    private static void AddStrike(EffectScene scene, Vector2 at, Vector2 inward, float strength, float alpha, int seed, Vector4? color) =>
        scene.AddImpact(new ImpactPrimitive
        {
            StrokeRole = PrimitiveRole.MainStroke, Position = at, Direction = inward, Strength = strength,
            Brightness = alpha, Seed = seed, ColorOverride = color,
        });

    // ---- sprinkled on the arcs ----

    /// <summary>Short crackles that hop off the arcs and die: stateless, each slot re-rolls where it is every few hundred milliseconds.</summary>
    private void EmitCrawlers(EffectScene scene, float alpha, float age, Vector2 size, float leave, Vector4? color)
    {
        float ramp = DrawHelpers.Smooth((age - 0.35f) / 0.5f) * (1f - DrawHelpers.Smooth(leave));
        if (ramp <= 0.002f) return;

        for (int slot = 0; slot < CrawlSlots; slot++)
        {
            float rate = 3.2f + 3.0f * DrawHelpers.Hash01(slot * 13 + 1);
            float x = age * rate + DrawHelpers.Hash01(slot * 7 + 2) * 10f;
            int epoch = (int)MathF.Floor(x);
            float f = x - epoch;
            if (f > 0.7f) continue;

            int hs = _seed + slot * 9173 + epoch * 7919;
            if (DrawHelpers.Hash01(hs + 1) > 0.55f) continue;

            int ai = (int)(DrawHelpers.Hash01(hs + 2) * ArcCount) % ArcCount;
            StrandPath path = _paths[ai];
            if (_arcReveal[ai] < 0.999f || path.Count < 2) continue;

            path.SampleAtArc((0.10f + 0.80f * DrawHelpers.Hash01(hs + 3)) * path.Length, out Vector2 pos, out Vector2 tan);
            if (pos.X < -20f || pos.Y < -20f || pos.X > size.X + 20f || pos.Y > size.Y + 20f) continue;

            float side = DrawHelpers.Hash01(hs + 4) < 0.5f ? -1f : 1f;
            Vector2 dir = Bolt.Rotate(tan, side * (0.9f + 0.9f * DrawHelpers.Hash01(hs + 5)));
            float half = (9f + 8f * DrawHelpers.Hash01(hs + 6)) * _px * _arcScale[ai];

            scene.AddParticle(new ParticlePrimitive
            {
                Position = pos + dir * (half * 0.9f), Size = half, AgeRatio = f / 0.7f,
                Brightness = alpha * ramp * 0.9f * MathF.Min(_arcBright[ai], 1.6f),
                Seed = hs, Role = PrimitiveRole.Arc, Rotation = MathF.Atan2(dir.X, -dir.Y), ColorOverride = color,
            });
        }
    }

    /// <summary>Static charge hanging in the air at every depth: tiny sharp specks far off, big out-of-focus discs close to the lens.</summary>
    private void EmitMotes(EffectScene scene, in LayerCamera cam, Vector2 size, float alpha, float age, float leave, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;
        float ramp = DrawHelpers.Smooth((age - 0.35f) / 1.2f) * (1f - DrawHelpers.Smooth(leave));
        if (ramp <= 0.002f) return;

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
            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = m.Size * px * scale * (1f + 3.4f * blur), AgeRatio = 0.5f,
                Brightness = alpha * ramp * (0.35f + 0.65f * far) * (1f - 0.65f * blur), Seed = m.Seed, Role = PrimitiveRole.Mote, ColorOverride = color,
            });
        }
    }
}

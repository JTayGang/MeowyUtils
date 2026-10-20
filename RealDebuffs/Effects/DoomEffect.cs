using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Doom: a summoning.</summary>
public sealed class DoomEffect : ISceneEffect, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Doom;
    public string DisplayName => "Doom";
    public string Description => "A blood-ritual summoning circle is drawn over the screen and ignited: hellfire, candles, drifting embers and smoke, and a slow toll that shakes it every few seconds.";
    public int DrawOrder => 8;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Doom"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "doom", "doomed", "demonic", "damned", "condemned"
    };

    // The blood ring is what a "made of X" phrase replaces; a particle in that phrase also replaces the hellfire.
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke,  "Blood ring",      "stroke.blood", Hero: true),
        new("Stroke",   PrimitiveRole.Halo,        "Ring glow",       "stroke.hellglow"),
        new("Stroke",   PrimitiveRole.Edge,        "Star and circle", "stroke.magma"),
        new("Stroke",   PrimitiveRole.Inscription, "Outer rings",     "stroke.brand"),
        new("Stroke",   PrimitiveRole.Horn,        "Horns",           "stroke.horn"),
        new("Particle", PrimitiveRole.Ember,       "Hellfire",        "particle.hellfire", Hero: true),
        new("Particle", PrimitiveRole.Cinder,      "Cinders",         "particle.cinder"),
        new("Particle", PrimitiveRole.Smoke,       "Smoke",           "particle.brimstone"),
        new("Particle", PrimitiveRole.Rune,        "Runes",           "particle.hellrune"),
        new("Particle", PrimitiveRole.Mote,        "Floating embers", "particle.hellmote"),
        new("Particle", PrimitiveRole.Goop,        "Blood",           "particle.blood"),
        new("Particle", PrimitiveRole.Candle,      "Candles",         "particle.candle"),
        new("Region",   PrimitiveRole.MainStroke,  "Firelight",       "region.hellglow", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke,  "Hell wash",       "region.flat-fill", "FlatFill"),
    };

    // the camera
    private const float CameraDistance = 1500f;  // px at 1080p; smaller = stronger perspective
    private const float BaseTilt = 0.17f;  // radians the stack leans from the line of sight
    private const float TiltSway = 0.030f;
    private const float PrecessionRate = 0.17f;  // rad/s the lean swings round
    private const float LurchTilt = 0.22f;  // extra lean as the cast lands, settling over LurchSeconds
    private const float LurchSeconds = 1.0f;

    // layout: what the viewer sees: Radii are fractions of the short side as they land on screen
    private const float FarHeight = -360f, BloodHeight = -150f, BandHeight = -55f, CircleHeight = 40f;
    private const float FarRadius = 0.84f, BloodRadius = 0.605f, BandOuter = 0.545f, BandInner = 0.480f, RuneRadius = 0.5125f, CircleRadius = 0.435f;

    // Spin, rad/s, per layer (signs alternate so neighbours turn against each other).
    private const float FarSpin = 0.035f, BandSpin = 0.045f, CircleSpin = -0.040f;

    // the hero ring

    // line weights, px at 1080p (the glowing width; a physical material reads them as diameters)
    private const float FarWidth = 9f, BandWidth = 8f, CircleWidth = 12f, StarWidth = 11f, FireGlowWidth = 66f;

    // the cast: when each piece starts being drawn, and how long the pen takes
    private const float BloodStart = 0.06f,  BloodSecs = 1.35f;
    private const float FarStart = 0.12f,    FarSecs = 1.15f;
    private const float BandStart = 0.35f,   BandSecs = 1.00f;
    private const float CircleStart = 0.60f, CircleSecs = 1.00f;
    private const float StarStart = 0.85f,   StarSecs = 1.15f;
    private const float IgniteAt = StarStart + StarSecs;  // the star closes: the circle takes light

    // runes
    private const float RuneSize = 25f;  // half-height in px at 1080p
    private const int   MaxRunes = 40;

    // candles
    private const float CandleSize = 13f;  // the pillar's half-width in px at 1080p
    private const float LightGap = 0.14f;  // seconds between one candle taking and the next

    // hellfire: tongues round the circle
    private const int   FireSites = 26;
    private static readonly FlameLayer FlameBack = new(FlameLayer.Back, 1.65f, 1, 23f, 33f);
    private static readonly FlameLayer FlameBody = new(FlameLayer.Body, 1.10f, 2, 17f, 26f);
    private static readonly FlameLayer FlameFront = new(FlameLayer.Front, 0.82f, 2, 11f, 17f);

    // horns: rising from the bottom corners
    private const float HornStart = IgniteAt + 0.35f, HornSecs = 1.6f;
    private const int   HornPoints = 22;

    // the toll: every few seconds the circle charges and then tolls
    private const float ChargeSecs = 0.9f;  // the build-up before it lands
    private const float EchoDelay = 0.42f;  // seconds after the toll that a weaker echo can follow
    private const float TollSpan = 3.2f;  // a toll has finished with by then; the next is rolled

    // floating embers
    private const int   MoteCount = 36;

    private static readonly uint Wash = DrawHelpers.Pack(0.070f, 0.004f, 0.010f);

    private const int RingSegments = 128, BloodSegments = 160, StarMax = 9, Pens = 6;

    private readonly StrandPath _far = new(RingSegments + 1);
    private readonly StrandPath _blood = new(BloodSegments + 1);
    private readonly StrandPath _bandOuter = new(RingSegments + 1), _bandInner = new(RingSegments + 1);
    private readonly StrandPath _circle = new(RingSegments + 1);
    private readonly StrandPath _star = new(StarMax + 1);
    private readonly StrandPath _hornL = new(HornPoints), _hornR = new(HornPoints);
    private float _hornLen, _hornCurl, _hornSkew, _hornBase;

    private readonly CastTracker _cast = new(envelope: true);
    private CastFrame _f;
    private LayerCamera _cam;
    private float _breath;

    private readonly FireDrift _drift;
    private readonly Func<int, Vector2> _ringPos;

    private struct Mote { public float X, Phase, Speed, Sway, Size, Z; public int Seed; }
    private readonly Mote[] _motes = new Mote[MoteCount];

    // The toll in progress (see UpdateToll): when it lands, how hard, which corner of the star it shakes from
    private struct Toll { public float Charge, Hit, Echo, Power; public int Vertex; }
    private Toll _toll;
    private float _tollAt, _tollPower;
    private int _tollIndex, _tollVertex, _tollFired = -1;
    private bool _tollEcho;

    // Sparks thrown off the pen while it draws (burst only; the pen is wherever each reveal has got to).
    private readonly ParticleEmitter _sparks = new(maxParticles: 72, seedSalt: 0xD0031);
    private readonly Func<int, Vector2> _sparkPos, _sparkVel;
    private readonly Vector2[] _pen = new Vector2[Pens];
    private readonly bool[] _penOn = new bool[Pens];
    private Vector2 _sparkAt;
    private float _sparkNext;

    // per-cast layout
    private int _seed;
    private int _starN, _starK, _runeCount;
    private float _azimuth, _azimuthDir;
    private float _farAng, _bloodSeam, _bandAng, _circleAng;

    public DoomEffect()
    {
        _sparks.Gravity = new Vector2(0f, 900f);
        _sparks.Drag = 0.9f;

        // Cinders and smoke leave the circle itself, wherever it is on screen now.
        _ringPos = seed =>
        {
            int i = (int)((uint)seed % (uint)Math.Max(1, _circle.Count - 1));
            return _circle.Points[i] + new Vector2(DrawHelpers.HashRange(seed + 7, -14f, 14f), DrawHelpers.HashRange(seed + 8, -10f, 6f)) * _f.Px;
        };
        _drift = new FireDrift(0xD0041, cinderMax: 40, smokeMax: 24, _ringPos, _ringPos);

        _sparkPos = _ => _sparkAt;
        _sparkVel = seed =>
        {
            float a = DrawHelpers.Hash01(seed + 5) * MathF.Tau;
            float sp = DrawHelpers.HashRange(seed + 6, 70f, 340f) * _f.Px;
            return new Vector2(MathF.Cos(a) * sp, MathF.Sin(a) * sp - 120f * _f.Px);
        };
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        var f = _f = _cast.Frame(screenSize, alpha, time, dt, colorOverride);
        if (f.Fresh) BeginCast(f.Seed);
        _drift.Px = f.Px;

        _toll = UpdateToll(f.Age);
        float tilt = BaseTilt + TiltSway * MathF.Sin(f.Age * 0.23f + 1.3f) + LurchTilt * MathF.Exp(-f.Age / LurchSeconds) + 0.10f * f.Leave
                   + 0.11f * _toll.Power * _toll.Hit + 0.04f * _toll.Echo;  // the toll thumps the whole stack
        float azimuth = _azimuth + _azimuthDir * PrecessionRate * f.Age;
        _cam = new LayerCamera(f.Center, CameraDistance * f.Px, tilt, azimuth);
        _breath = 0.92f + 0.08f * MathF.Sin(f.Age * 1.9f + 0.7f);

        Span<ShockFront> fronts = stackalloc ShockFront[5];
        int frontCount = BuildFronts(fronts, f.Age, f.Short);

        EmitAtmosphere(scene);
        EmitCircle(scene, fronts[..frontCount]);

        float fire = FireLevel();
        EmitHorns(scene, fire);

        // Particles draw over every stroke, in the order they are submitted: far to near.
        EmitRunes(scene);
        _drift.EmitSmoke(scene, f.Time, f.Dt, f.Short, f.Alpha, fire, f.Color);
        EmitFlames(scene, fire, back: true);
        EmitCandles(scene);
        EmitFlames(scene, fire, back: false);
        EmitSparks(scene);
        EmitCinders(scene, fire);
        EmitMotes(scene);
    }

    // the cast

    private void BeginCast(int seed)
    {
        _seed = seed;

        _azimuth = DrawHelpers.Hash01(seed + 1) * MathF.Tau;
        _azimuthDir = DrawHelpers.Hash01(seed + 2) < 0.5f ? -1f : 1f;

        // The star: usually the classic pentagram, now and then a heptagram or an octagram.
        float r = DrawHelpers.Hash01(seed + 3);
        (_starN, _starK) = r < 0.55f ? (5, 2) : r < 0.72f ? (7, 3) : r < 0.88f ? (7, 2) : (8, 3);

        _runeCount = 26 + (int)(DrawHelpers.Hash01(seed + 4) * 9f);

        // The horns: how long, how tightly they curl in, how much longer one is than the other, how thick.
        _hornLen = DrawHelpers.HashRange(seed + 40, 0.48f, 0.62f);
        _hornCurl = DrawHelpers.HashRange(seed + 41, 0.60f, 1.05f);
        _hornSkew = DrawHelpers.HashRange(seed + 42, -0.12f, 0.12f);
        _hornBase = DrawHelpers.HashRange(seed + 43, 0.085f, 0.115f);
        _farAng = DrawHelpers.Hash01(seed + 5) * MathF.Tau;
        _bloodSeam = DrawHelpers.Hash01(seed + 6) < 0.5f ? -MathF.PI * 0.5f : MathF.PI * 0.5f;
        _bandAng = DrawHelpers.Hash01(seed + 7) * MathF.Tau;
        _circleAng = DrawHelpers.Hash01(seed + 8) * MathF.Tau;

        _tollIndex = 0;
        _tollFired = -1;
        _tollAt = IgniteAt + 3.2f + 2.6f * DrawHelpers.Hash01(seed + 50);
        _tollPower = 1f;
        _tollVertex = (int)(DrawHelpers.Hash01(seed + 51) * _starN) % _starN;
        _tollEcho = true;

        _sparks.Clear();
        _sparkNext = 0f;
        _drift.Clear();

        // Floating embers: a fixed set, each with its own depth, drift and sway, so no two casts fill the screen the same way.
        for (int i = 0; i < MoteCount; i++)
        {
            int ms = seed + i * 6151 + 11;
            _motes[i] = new Mote
            {
                X = DrawHelpers.HashRange(ms + 2, -0.06f, 1.06f),
                Phase = DrawHelpers.Hash01(ms + 3),
                Z = DrawHelpers.Hash01(ms + 4),
                Speed = DrawHelpers.HashRange(ms + 5, 14f, 46f),
                Sway = DrawHelpers.HashRange(ms + 6, 10f, 38f),
                Size = DrawHelpers.HashRange(ms + 7, 1.8f, 4.6f),
                Seed = ms,
            };
        }
    }

    /// <summary>The shocks running through the stack: the circle igniting (outward); a toll charging (drawn in toward the middle)</summary>
    private int BuildFronts(Span<ShockFront> into, float age, float shortSide)
    {
        int n = 0;
        float ti = age - IgniteAt;
        if (ti > 0f && ti < 1.2f)
            into[n++] = new ShockFront(shortSide * (0.04f + 1.5f * ti), shortSide * 0.07f, 1.2f * MathF.Pow(1f - ti / 1.2f, 1.4f));

        if (_toll.Charge > 0.01f)
            into[n++] = new ShockFront(shortSide * (0.80f - 0.70f * _toll.Charge), shortSide * 0.08f, 0.55f * _toll.Charge * _toll.Power);

        float t = age - _tollAt;
        if (t >= 0f && t < 1.5f)
            into[n++] = new ShockFront(shortSide * (0.03f + 1.55f * t), shortSide * 0.09f, 1.3f * _toll.Power * MathF.Pow(1f - t / 1.5f, 1.5f));
        float te = t - EchoDelay;
        if (_tollEcho && te >= 0f && te < 1.3f)
            into[n++] = new ShockFront(shortSide * (0.03f + 1.45f * te), shortSide * 0.08f, 0.60f * _toll.Power * MathF.Pow(1f - te / 1.3f, 1.5f));
        return n;
    }

    // the toll

    /// <summary>Where the circle is in its cycle: charging (0 to 1 over the second before it lands), just struck (decaying), or at rest.</summary>
    private Toll UpdateToll(float age)
    {
        if (age > _tollAt + TollSpan) RollToll(age);

        float t = age - _tollAt;
        var r = new Toll { Power = _tollPower, Vertex = _tollVertex };
        if (t < 0f)
            r.Charge = DrawHelpers.Smooth(DrawHelpers.Saturate((t + ChargeSecs) / ChargeSecs));
        else
        {
            r.Hit = MathF.Exp(-t / 0.50f) * DrawHelpers.Smooth(t / 0.03f);
            float te = t - EchoDelay;
            if (_tollEcho && te > 0f) r.Echo = 0.55f * MathF.Exp(-te / 0.50f) * DrawHelpers.Smooth(te / 0.03f);
        }
        return r;
    }

    private void RollToll(float age)
    {
        _tollIndex++;
        int s = _seed + _tollIndex * 7919;
        _tollAt = age + DrawHelpers.HashRange(s + 1, 6.5f, 10.5f);
        _tollPower = DrawHelpers.HashRange(s + 2, 0.75f, 1.25f);
        _tollVertex = (int)(DrawHelpers.Hash01(s + 3) * _starN) % _starN;
        _tollEcho = DrawHelpers.Hash01(s + 4) < 0.6f;
    }

    private static float Prog(float age, float start, float secs) => DrawHelpers.Smooth(DrawHelpers.Saturate((age - start) / secs));

    // atmosphere

    private void EmitAtmosphere(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        float castIn = DrawHelpers.Smooth(f.Age / 0.45f);
        float lit = DrawHelpers.Smooth((f.Age - IgniteAt + 0.2f) / 1.0f);

        // The vignette closes in as a toll charges and pumps as it lands.
        scene.RequestVignette(HellColor.Vignette, 0.20f + 0.05f * _toll.Charge, MathF.Min(1f, f.Alpha * 0.80f * castIn * (_breath + 0.30f * _toll.Charge + 0.30f * _toll.Hit)), priority: 22, f.Color);

        // A flat wash keeps the glow readable on a bright scene.
        scene.Wash(f.Size, Wash, 0.22f * f.Alpha * castIn, f.Color);

        // The cast's flash uses the f.Gate so the fade-in can't swallow it; the toll lights the screen for an instant.
        float flash = f.Age < 0.5f ? MathF.Exp(-f.Age / 0.10f) : 0f;
        scene.Wash(f.Size, HellColor.Flash, 0.46f * flash * f.Gate, f.Color);
        scene.Wash(f.Size, HellColor.Flash, (0.18f * _toll.Power * _toll.Hit + 0.07f * _toll.Echo) * f.Alpha, f.Color);

        // Firelight thrown up from below once the circle has caught.
        scene.Pool(f.Size, f.Short * 0.36f, 0u, MathF.Min(1f, 0.55f * f.Alpha * lit * (_breath + 0.45f * _toll.Hit)), f.Color);
    }

    // the circle

    private void EmitCircle(EffectScene scene, ReadOnlySpan<ShockFront> fronts)
    {
        ref readonly CastFrame f = ref _f;
        ref readonly LayerCamera cam = ref _cam;
        float px = _f.Px, dist = CameraDistance * px;
        Array.Clear(_penOn);

        // Far: one thin ring, nearly off the screen, that only shows at the sides.
        {
            float h = FarHeight * px, scale = cam.ScaleAt(h);
            float rs = FarRadius * f.Short;
            float reveal = Prog(f.Age, FarStart, FarSecs);
            float excite = ShockFront.Excite(fronts, rs);
            SigilGeometry.Ring(_far, RingSegments, in cam, SigilGeometry.PlaneRadius(rs, h, dist), h, _farAng + FarSpin * f.Age, RingWobble.None);
            scene.Ring(_far, PrimitiveRole.Inscription, _seed, 1, FarWidth * px * scale, f.Alpha * _breath * 0.55f * (1f + 0.5f * excite), 0.65f, excite, reveal, 0f, f.Color);
        }

        // Blood: the hero ring, under a halo of its own so any material keeps the hell-light.
        {
            float h = BloodHeight * px;
            float rs = BloodRadius * f.Short;
            float reveal = Prog(f.Age, BloodStart, BloodSecs);
            float excite = ShockFront.Excite(fronts, rs);
            float bright = f.Alpha * _breath;

            var wobble = new RingWobble(0.008f + 0.014f * excite, 3, _seed * 0.0007f - 0.30f * f.Age, 0.005f, 5, _seed * 0.0011f + 0.42f * f.Age);
            // The ring is round, so turning it shows only in its wobble, which already moves
            SigilGeometry.Ring(_blood, BloodSegments, in cam, SigilGeometry.PlaneRadius(rs, h, dist), h, _bloodSeam, in wobble);

            scene.HeroRing(_blood, _seed, reveal, bright, 0.95f, 0.30f, 0.06f, DrawHelpers.Saturate(excite * 0.8f), reveal < 0.999f ? 1f : 0f, f.Color, haloScale: 2.6f);
            TrackPen(0, _blood, reveal);
        }

        // Band: two thin rings with the runes between them (EmitRunes).
        {
            float h = BandHeight * px, scale = cam.ScaleAt(h);
            float eo = BandOuter * f.Short, ei = BandInner * f.Short;
            float ro = Prog(f.Age, BandStart, BandSecs), ri = Prog(f.Age, BandStart + 0.10f, BandSecs);
            float xo = ShockFront.Excite(fronts, eo), xi = ShockFront.Excite(fronts, ei);
            float bright = f.Alpha * _breath;
            SigilGeometry.Ring(_bandOuter, RingSegments, in cam, SigilGeometry.PlaneRadius(eo, h, dist), h, _bandAng + BandSpin * f.Age, RingWobble.None);
            SigilGeometry.Ring(_bandInner, RingSegments, in cam, SigilGeometry.PlaneRadius(ei, h, dist), h, _bandAng + BandSpin * f.Age, RingWobble.None);
            scene.Ring(_bandOuter, PrimitiveRole.Inscription, _seed, 3, BandWidth * px * scale, bright * 0.80f * (1f + 0.5f * xo), 0.10f, xo, ro, 0f, f.Color);
            scene.Ring(_bandInner, PrimitiveRole.Inscription, _seed, 4, BandWidth * px * scale, bright * 0.80f * (1f + 0.5f * xi), 0.10f, xi, ri, 0f, f.Color);
            TrackPen(1, _bandOuter, ro);
            TrackPen(2, _bandInner, ri);
        }

        // Circle and star: the inner ring, and the star drawn in one stroke inside it.
        {
            float h = CircleHeight * px, scale = cam.ScaleAt(h);
            float rs = CircleRadius * f.Short;
            float planeR = SigilGeometry.PlaneRadius(rs, h, dist);
            float rc = Prog(f.Age, CircleStart, CircleSecs), rst = Prog(f.Age, StarStart, StarSecs);
            float excite = ShockFront.Excite(fronts, rs * 0.8f);
            float bright = f.Alpha * _breath;
            float ang = _circleAng + CircleSpin * f.Age;

            SigilGeometry.Ring(_circle, RingSegments, in cam, planeR, h, ang, RingWobble.None);
            SigilGeometry.Star(_star, in cam, _starN, _starK, planeR, h, ang + MathF.PI * 0.5f);

            // Under the ring of fire, a glow the flames stand in.
            float fire = FireLevel();
            if (fire > 0.01f)
                scene.AddStroke(new StrokePrimitive
                {
                    Path = _circle, Role = PrimitiveRole.Halo, Reveal = 1f, WidthHint = FireGlowWidth * px * scale,
                    Brightness = MathF.Min(1f, f.Alpha * fire) * 0.80f, Seed = _seed + 23, Phase = 3f, Depth = 0f, Agitation = DrawHelpers.Saturate(excite), Closed = true, ColorOverride = f.Color,
                });

            scene.Ring(_circle, PrimitiveRole.Edge, _seed, 5, CircleWidth * px * scale, bright * 0.95f, 0f, excite, rc, 0f, f.Color, agitGain: 0.8f);
            scene.Ring(_star, PrimitiveRole.Edge, _seed, 6, StarWidth * px * scale, bright, 0f, excite, rst, 0f, f.Color, agitGain: 0.8f);
            TrackPen(3, _circle, rc);
            TrackPen(4, _star, rst);
        }
    }

    /// <summary>Remembers where the pen of this stroke is while it is still being drawn.</summary>
    private void TrackPen(int i, StrandPath path, float reveal)
    {
        if (reveal <= 0.001f || reveal >= 0.999f) return;
        path.SampleAtArc(path.Length * reveal, out _pen[i], out _);
        _penOn[i] = true;
    }

    // runes

    private void EmitRunes(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        ref readonly LayerCamera cam = ref _cam;
        float px = _f.Px, dist = CameraDistance * px;
        float h = BandHeight * px;
        float shortSide = px * 1080f;
        float planeR = SigilGeometry.PlaneRadius(RuneRadius * shortSide, h, dist);
        float ang = _bandAng + BandSpin * f.Age;
        float reveal = Prog(f.Age, BandStart + 0.05f, BandSecs);
        int count = Math.Min(_runeCount, MaxRunes);

        for (int i = 0; i < count; i++)
        {
            // A rune lights as the pen reaches it, in the order it is drawn.
            float lit = DrawHelpers.Saturate(reveal * count - i + 0.5f);
            if (lit <= 0.001f) continue;

            float a = ang + MathF.Tau * i / count;
            Vector2 at = SigilGeometry.PointOn(in cam, planeR, h, a, out float scale, out Vector2 outward);
            int seed = _seed + i * 7919 + 13;

            // As a toll charges a bright run of runes travels once round the band; as it lands they all flare.
            float d = MathF.Abs(i - _toll.Charge * count); d = MathF.Min(d, count - d);
            float boost = 1f + 0.95f * MathF.Exp(-d * d / 6f) * MathF.Sin(MathF.PI * _toll.Charge) + 0.80f * _toll.Hit + 0.35f * _toll.Echo;
            float size = RuneSize * px * scale * DrawHelpers.HashRange(seed + 1, 0.86f, 1.12f);

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, AgeRatio = 0.14f + 0.36f * lit, Size = size,
                Brightness = MathF.Min(1f, f.Alpha * _breath * 0.92f * lit * boost), Seed = seed, Role = PrimitiveRole.Rune, ColorOverride = f.Color,
                Variant = 1, Rotation = MathF.Atan2(outward.X, -outward.Y),
            });
        }
    }

    // sparks off the pen

    private void EmitSparks(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        _sparks.Step(f.Time, f.Dt);

        if (f.Age < IgniteAt + 0.05f && f.Time >= _sparkNext)
        {
            _sparkNext = f.Time + 0.045f;
            for (int i = 0; i < Pens; i++)
            {
                if (!_penOn[i]) continue;
                _sparkAt = _pen[i];
                _sparks.Burst(1, f.Time + i * 0.0137f, _sparkPos, _sparkVel, 0.5f, 1.1f, 1.8f * _f.Px, 3.4f * _f.Px);
            }
        }
        _sparks.Emit(scene, f.Time, PrimitiveRole.Cinder, brightnessMul: f.Alpha, colorOverride: f.Color);
    }

    // horns

    /// <summary>Two horns rising out of the bottom corners and curling in over the circle, as if what is being summoned were climbing up into view.</summary>
    private void EmitHorns(EffectScene scene, float fire)
    {
        ref readonly CastFrame f = ref _f;
        for (int side = 0; side < 2; side++)
        {
            float sgn = side == 0 ? -1f : 1f;  // -1: left horn (curls right), +1: right horn (curls left)
            float grow = Prog(f.Age, HornStart + 0.12f * side, HornSecs) * (1f - DrawHelpers.Smooth(f.Leave * 1.4f));
            if (grow <= 0.001f) continue;

            float len = (_hornLen + _hornSkew * sgn) * f.Size.Y;
            float curl = _hornCurl + 0.12f * sgn * _hornSkew;
            Vector2 b = new(f.Size.X * (side == 0 ? 0.050f : 0.950f), f.Size.Y * 1.05f);
            Vector2 c = b + new Vector2(sgn * 0.075f * f.Size.X * curl, -0.52f * len);  // bows out, away from the middle ...
            Vector2 t = b + new Vector2(-sgn * 0.19f * f.Size.X * curl, -0.93f * len);  // ... and the tip comes back in over the circle

            // The horn sways a hair about its root.
            float sway = 0.014f * MathF.Sin(f.Age * 0.55f + side * 2.1f + _seed * 0.01f);
            float cs = MathF.Cos(sway), sn = MathF.Sin(sway);
            Vector2 Rot(Vector2 p) { Vector2 d = p - b; return b + new Vector2(d.X * cs - d.Y * sn, d.X * sn + d.Y * cs); }
            c = Rot(c); t = Rot(t);

            StrandPath path = side == 0 ? _hornL : _hornR;
            for (int i = 0; i < HornPoints; i++)
            {
                float u = i / (HornPoints - 1f), v = 1f - u;
                path.Points[i] = v * v * b + 2f * v * u * c + u * u * t;
            }
            path.Count = HornPoints;
            path.BuildArc();

            scene.AddStroke(new StrokePrimitive
            {
                Path = path, Role = PrimitiveRole.Horn, Reveal = grow, WidthHint = _hornBase * f.Size.Y,
                Brightness = f.Alpha, Seed = _seed + 61 + side * 17, Depth = 0f, Agitation = DrawHelpers.Saturate(fire * 0.7f), Closed = false, ColorOverride = f.Color,
            });
        }
    }

    // hellfire, candles, smoke, cinders, embers

    /// <summary>How much fire there is: none until the circle ignites, a roar as it takes, then a steady burn that gutters out as the curse lifts.</summary>
    private float FireLevel()
    {
        ref readonly CastFrame f = ref _f;
        float ti = f.Age - IgniteAt;
        if (ti <= 0f) return 0f;
        float ramp = DrawHelpers.Smooth(ti / 0.7f);
        float roar = 0.55f * MathF.Exp(-ti * 2.2f) * DrawHelpers.Smooth(ti / 0.15f);
        // A toll draws the fire in as it charges and bursts it out as it lands.
        float toll = -0.28f * _toll.Charge + 0.62f * _toll.Power * _toll.Hit + 0.30f * _toll.Echo;
        return (ramp + roar + toll) * (1f - DrawHelpers.Smooth(f.Leave * 1.6f));
    }

    /// <summary>Where the inner circle is right now: the plane radius, height and turning angle every piece standing on it shares.</summary>
    private void CircleFrame(out float planeR, out float h, out float ang)
    {
        float px = _f.Px;
        float shortSide = px * 1080f;
        h = CircleHeight * px;
        planeR = SigilGeometry.PlaneRadius(CircleRadius * shortSide, h, CameraDistance * px);
        ang = _circleAng + CircleSpin * _f.Age;
    }

    private void EmitFlames(EffectScene scene, float fire, bool back)
    {
        ref readonly CastFrame f = ref _f;
        ref readonly LayerCamera cam = ref _cam;
        if (fire <= 0.01f) return;
        CircleFrame(out float planeR, out float h, out float ang);
        float bright = MathF.Min(1f, f.Alpha * fire * 1.1f);

        // Back tongues are tall, dim and soft; body and front are the fire proper.
        for (int layer = back ? 0 : 1; layer < (back ? 1 : 3); layer++)
        {
            FlameLayer style = layer == 0 ? FlameBack : layer == 1 ? FlameBody : FlameFront;
            for (int site = 0; site < FireSites; site++)
            {
                int ss = _seed + site * 977 + layer * 131;
                float a0 = ang + MathF.Tau * (site + DrawHelpers.HashRange(ss + 1, -0.30f, 0.30f)) / FireSites;
                Vector2 at = cam.Project(planeR * MathF.Cos(a0), planeR * MathF.Sin(a0), h, out float scale);

                // Tongues from the top of the screen leave it at once; those from the bottom climb into view, so the bottom burns taller.
                float low = DrawHelpers.Saturate((at.Y - _f.Center.Y) / (0.5f * f.Short) * 0.5f + 0.5f);
                float weight = 0.55f + 0.65f * low;
                float site01 = 0.78f + 0.44f * DrawHelpers.Hash01(ss + 2);

                Flames.Tongues(scene, in style, at, ss, f.Time, _f.Px * scale * weight * site01 * MathF.Min(fire, 1.25f),
                               new Vector2(9f * _f.Px, 0f), 0f, 0.85f, bright, f.Color);
            }
        }
    }

    private void EmitCandles(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        ref readonly LayerCamera cam = ref _cam;
        CircleFrame(out float planeR, out float h, out float ang);
        float px = _f.Px;
        float pen = Prog(f.Age, StarStart, StarSecs);
        float starAng = ang + MathF.PI * 0.5f;

        for (int j = 0; j < _starN; j++)
        {
            // The pen reaches corner j when the star is j/n drawn: that is when its candle stands up.
            int m = (j * _starK) % _starN;
            float appear = DrawHelpers.Saturate((pen * _starN - j) / 0.6f);
            if (appear <= 0.001f) continue;

            float a = starAng + MathF.Tau * m / _starN;
            Vector2 at = cam.Project(planeR * MathF.Cos(a), planeR * MathF.Sin(a), h, out float scale);
            int seed = _seed + m * 4513 + 29;

            float lit = DrawHelpers.Saturate((f.Age - (IgniteAt + 0.10f + LightGap * j)) / 1.0f);
            float out01 = DrawHelpers.Smooth(f.Leave * 2.2f);  // blown out as the curse lifts
            // The draught: a faint breath; drawn in toward the middle as a toll charges, then blown outward by it.
            float outward = at.X >= _f.Center.X ? 1f : -1f;
            float draught = (0.7f * MathF.Sin(f.Age * 0.8f + seed * 0.1f) - 7f * outward * _toll.Charge + 26f * outward * (_toll.Hit + 0.5f * _toll.Echo)) * px;
            float gutter = DrawHelpers.Saturate(0.30f * _toll.Charge + 0.62f * _toll.Hit + 0.30f * _toll.Echo);

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, AgeRatio = lit, Size = CandleSize * px * scale, Brightness = f.Alpha * appear * (1f - 0.5f * out01),
                Seed = seed, Role = PrimitiveRole.Candle, ColorOverride = f.Color,
                Rotation = 0.045f * MathF.Sin(f.Age * 0.9f + seed * 0.37f), Sway = draught,
                Variant = (int)(255f * (1f - out01) * (1f - gutter)) | 0x100,
            });
        }
    }

    private void EmitCinders(EffectScene scene, float fire)
    {
        ref readonly CastFrame f = ref _f;
        if (_tollFired != _tollIndex && _toll.Hit > 0.5f)
        {
            _tollFired = _tollIndex;
            _drift.BurstCinders((int)(26 * _toll.Power), f.Time);
        }
        _drift.EmitCinders(scene, f.Time, f.Dt, f.Alpha, fire, f.Color);
    }

    /// <summary>Embers drifting up the whole depth of the view: far ones small and bright, near ones large soft discs (out of focus)</summary>
    private void EmitMotes(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        float fire = DrawHelpers.Smooth((f.Age - IgniteAt + 0.4f) / 1.2f);
        if (fire <= 0.01f) return;
        float px = _f.Px;

        for (int i = 0; i < MoteCount; i++)
        {
            ref readonly var m = ref _motes[i];
            float z = m.Z;  // 0 = far, 1 = at the lens
            float rise = (m.Phase * (f.Size.Y + 160f * px) + f.Time * m.Speed * (0.5f + 1.0f * z) * px) % (f.Size.Y + 160f * px);
            float y = f.Size.Y + 80f * px - rise;
            float x = f.Size.X * m.X + MathF.Sin(f.Time * 0.7f + m.Phase * 31f) * m.Sway * px * (0.6f + z);
            float fade = DrawHelpers.Saturate(MathF.Min(rise / (f.Size.Y * 0.12f), (f.Size.Y + 160f * px - rise) / (f.Size.Y * 0.3f)));

            scene.AddParticle(new ParticlePrimitive
            {
                Position = new Vector2(x, y), AgeRatio = 0.5f, Size = m.Size * px * (0.7f + 2.6f * z * z),
                Brightness = f.Alpha * fire * fade * (0.85f - 0.40f * z), Seed = m.Seed, Role = PrimitiveRole.Mote, ColorOverride = f.Color,
            });
        }
    }
}

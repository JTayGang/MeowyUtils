using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Doom: a summoning. A blood-painted ritual circle is drawn over the screen by a hot pen and then ignited: a star turning inside it, a band of infernal script, candles at its corners, hellfire running round the ring and smoke and embers rising off it. Every few seconds the circle tolls and the whole thing surges. The outer blood ring is the hero: made of chains or rope it keeps its glow; a particle in a "made of" phrase replaces the hellfire.</summary>
public sealed class DoomEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
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
    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke),
        new("Particle", PrimitiveRole.Ember),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke,  "Blood ring",      "stroke.blood"),
        new("Stroke",   PrimitiveRole.Halo,        "Ring glow",       "stroke.hellglow"),
        new("Stroke",   PrimitiveRole.Edge,        "Star and circle", "stroke.magma"),
        new("Stroke",   PrimitiveRole.Inscription, "Outer rings",     "stroke.brand"),
        new("Stroke",   PrimitiveRole.Horn,        "Horns",           "stroke.horn"),
        new("Particle", PrimitiveRole.Ember,       "Hellfire",        "particle.hellfire"),
        new("Particle", PrimitiveRole.Cinder,      "Cinders",         "particle.cinder"),
        new("Particle", PrimitiveRole.Smoke,       "Smoke",           "particle.brimstone"),
        new("Particle", PrimitiveRole.Rune,        "Runes",           "particle.hellrune"),
        new("Particle", PrimitiveRole.Mote,        "Floating embers", "particle.hellmote"),
        new("Particle", PrimitiveRole.Goop,        "Blood",           "particle.blood"),
        new("Particle", PrimitiveRole.Candle,      "Candles",         "particle.candle"),
        new("Region",   PrimitiveRole.MainStroke,  "Firelight",       "region.hellglow", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke,  "Hell wash",       "region.flat-fill", "FlatFill"),
    };

    // ---- the camera ----
    private const float CameraDistance = 1500f;     // px at 1080p; smaller = stronger perspective
    private const float BaseTilt = 0.17f;           // radians the stack leans from the line of sight
    private const float TiltSway = 0.030f;
    private const float PrecessionRate = 0.17f;     // rad/s the lean swings round
    private const float LurchTilt = 0.22f;          // extra lean as the cast lands, settling over LurchSeconds
    private const float LurchSeconds = 1.0f;

    // ---- layout: what the viewer sees ----
    // Radii are fractions of the short side as they land on screen; heights are px toward the viewer (negative = behind the screen plane).
    private const float FarHeight = -360f, BloodHeight = -150f, BandHeight = -55f, CircleHeight = 40f;
    private const float FarRadius = 0.84f, BloodRadius = 0.605f, BandOuter = 0.545f, BandInner = 0.480f, RuneRadius = 0.5125f, CircleRadius = 0.435f;

    // Spin, rad/s, per layer (signs alternate so neighbours turn against each other).
    private const float FarSpin = 0.035f, BandSpin = 0.045f, CircleSpin = -0.040f;

    // ---- the hero ring ----
    private const int   BloodHalfLinks = 90;        // a chain gets 2x this many links however big the ring is (see the width below)
    private const float HaloWidthOverRing = 2.6f;

    // ---- line weights, px at 1080p (the glowing width; a physical material reads them as diameters) ----
    private const float FarWidth = 9f, BandWidth = 8f, CircleWidth = 12f, StarWidth = 11f, FireGlowWidth = 66f;

    // ---- the cast: when each piece starts being drawn, and how long the pen takes ----
    private const float BloodStart = 0.06f,  BloodSecs = 1.35f;
    private const float FarStart = 0.12f,    FarSecs = 1.15f;
    private const float BandStart = 0.35f,   BandSecs = 1.00f;
    private const float CircleStart = 0.60f, CircleSecs = 1.00f;
    private const float StarStart = 0.85f,   StarSecs = 1.15f;
    private const float IgniteAt = StarStart + StarSecs;       // the star closes: the circle takes light

    // ---- runes ----
    private const float RuneSize = 25f;             // half-height in px at 1080p
    private const int   MaxRunes = 40;

    // ---- candles ----
    private const float CandleSize = 13f;          // the pillar's half-width in px at 1080p
    private const float LightGap = 0.14f;           // seconds between one candle taking and the next

    // ---- hellfire: tongues round the circle ----
    private const int   FireSites = 26;
    private const float VariantBody = 0, VariantBack = 1, VariantFront = 2;

    // ---- horns: rising from the bottom corners ----
    private const float HornStart = IgniteAt + 0.35f, HornSecs = 1.6f;
    private const int   HornPoints = 22;

    // ---- the toll: every few seconds the circle charges and then tolls ----
    private const float ChargeSecs = 0.9f;          // the build-up before it lands
    private const float EchoDelay = 0.42f;          // seconds after the toll that a weaker echo can follow
    private const float TollSpan = 3.2f;            // a toll has finished with by then; the next is rolled

    // ---- floating embers ----
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

    private readonly CastTracker _cast = new();
    private readonly CastEnvelope _env = new();

    // Cinders and smoke leave the ring and are carried on their own; callbacks are built once, so nothing allocates per frame.
    private readonly ParticleEmitter _cinders = new(maxParticles: 40, seedSalt: 0xD0041);
    private readonly ParticleEmitter _smoke = new(maxParticles: 24, seedSalt: 0xD0051);
    private readonly Func<int, Vector2> _ringPos, _cinderVel, _smokeVel;

    private struct Mote { public float X, Phase, Speed, Sway, Size, Z; public int Seed; }
    private readonly Mote[] _motes = new Mote[MoteCount];

    // The toll in progress (see UpdateToll): when it lands, how hard, which corner of the star it shakes from, and whether an echo follows.
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

    // ---- per-cast layout ----
    private int _seed;
    private int _starN, _starK, _runeCount;
    private float _azimuth, _azimuthDir;
    private float _farAng, _bloodSeam, _bandAng, _circleAng;
    private float _px = 1f;
    private Vector2 _center;

    public DoomEffect()
    {
        _sparks.Gravity = new Vector2(0f, 900f);
        _sparks.Drag = 0.9f;
        _cinders.Gravity = new Vector2(0f, -26f);
        _cinders.Drag = 0.30f;
        _cinders.Wander = 240f;
        _cinders.WanderHz = 0.85f;
        _smoke.Drag = 0.06f;
        _smoke.Wander = 26f;
        _smoke.WanderHz = 0.35f;

        // Cinders and smoke leave the circle itself, wherever it is on screen now.
        _ringPos = seed =>
        {
            int i = (int)((uint)seed % (uint)Math.Max(1, _circle.Count - 1));
            return _circle.Points[i] + new Vector2(DrawHelpers.HashRange(seed + 7, -14f, 14f), DrawHelpers.HashRange(seed + 8, -10f, 6f)) * _px;
        };
        _cinderVel = seed => new Vector2(DrawHelpers.HashRange(seed + 10, -55f, 55f), -DrawHelpers.HashRange(seed + 11, 90f, 300f)) * _px;
        _smokeVel = seed => new Vector2(DrawHelpers.HashRange(seed + 10, -14f, 14f), -DrawHelpers.HashRange(seed + 11, 30f, 70f)) * _px;

        _sparkPos = _ => _sparkAt;
        _sparkVel = seed =>
        {
            float a = DrawHelpers.Hash01(seed + 5) * MathF.Tau;
            float sp = DrawHelpers.HashRange(seed + 6, 70f, 340f) * _px;
            return new Vector2(MathF.Cos(a) * sp, MathF.Sin(a) * sp - 120f * _px);
        };
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        if (_cast.Begin(time))
            BeginCast(unchecked((int)(_cast.Start * 1000f)), dt);

        float age = time - _cast.Start;
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        _px = shortSide / 1080f;
        _center = screenSize * 0.5f;

        _env.Update(alpha, age);
        alpha = _env.Alpha;
        float gate = _env.Gate;
        float leave = _env.Leave;

        _toll = UpdateToll(age);
        float tilt = BaseTilt + TiltSway * MathF.Sin(age * 0.23f + 1.3f) + LurchTilt * MathF.Exp(-age / LurchSeconds) + 0.10f * leave
                   + 0.11f * _toll.Power * _toll.Hit + 0.04f * _toll.Echo;                       // the toll thumps the whole stack
        float azimuth = _azimuth + _azimuthDir * PrecessionRate * age;
        var cam = new LayerCamera(_center, CameraDistance * _px, tilt, azimuth);

        float breath = 0.92f + 0.08f * MathF.Sin(age * 1.9f + 0.7f);

        Span<ShockFront> fronts = stackalloc ShockFront[5];
        int frontCount = BuildFronts(fronts, age, shortSide);

        EmitAtmosphere(scene, screenSize, shortSide, alpha, gate, age, breath, colorOverride);
        EmitCircle(scene, in cam, shortSide, alpha, age, breath, leave, fronts[..frontCount], colorOverride);
        EmitHorns(scene, screenSize, alpha, age, leave, FireLevel(age, leave), colorOverride);

        // Particles draw over every stroke, in the order they are submitted: far to near.
        float fire = FireLevel(age, leave);
        EmitRunes(scene, in cam, alpha, age, breath, leave, colorOverride);
        EmitSmoke(scene, time, dt, shortSide, alpha, fire, colorOverride);
        EmitFlames(scene, in cam, shortSide, alpha, time, age, fire, back: true, colorOverride);
        EmitCandles(scene, in cam, alpha, age, breath, leave, colorOverride);
        EmitFlames(scene, in cam, shortSide, alpha, time, age, fire, back: false, colorOverride);
        EmitSparks(scene, time, dt, alpha, age, colorOverride);
        EmitCinders(scene, time, dt, alpha, fire, colorOverride);
        EmitMotes(scene, screenSize, shortSide, alpha, time, age, colorOverride);
    }

    // ---- the cast ----

    private void BeginCast(int seed, float dt)
    {
        _seed = seed;
        _env.Begin(dt);

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
        _cinders.Clear();
        _smoke.Clear();

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

    /// <summary>The shocks running through the stack: the circle igniting (outward); a toll charging (drawn in toward the middle), landing (outward) and echoing (outward, weaker).</summary>
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

    // ---- the toll ----

    /// <summary>Where the circle is in its cycle: charging (0 to 1 over the second before it lands), just struck (decaying), or at rest. When one is over the next is rolled, so every toll differs in when, how hard and whether it echoes.</summary>
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

    // ---- atmosphere ----

    private void EmitAtmosphere(EffectScene scene, Vector2 size, float shortSide, float alpha, float gate, float age, float breath, Vector4? color)
    {
        float castIn = DrawHelpers.Smooth(age / 0.45f);
        float lit = DrawHelpers.Smooth((age - IgniteAt + 0.2f) / 1.0f);

        // The vignette closes in as a toll charges and pumps as it lands.
        scene.RequestVignette(HellColor.Vignette, 0.20f + 0.05f * _toll.Charge, MathF.Min(1f, alpha * 0.80f * castIn * (breath + 0.30f * _toll.Charge + 0.30f * _toll.Hit)), priority: 22, color);

        // The world going dark and red under the curse: a flat wash keeps the glow readable on a bright scene.
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = Wash, Alpha = 0.22f * alpha * castIn, ColorOverride = color });

        // The flash the cast lands with (the cast itself, so it uses the gate rather than the fade).
        float flash = age < 0.5f ? MathF.Exp(-age / 0.10f) : 0f;
        if (flash > 0.01f)
            scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = HellColor.Flash, Alpha = 0.46f * flash * gate, ColorOverride = color });

        // The toll lights the whole screen for an instant.
        float tollFlash = 0.18f * _toll.Power * _toll.Hit + 0.07f * _toll.Echo;
        if (tollFlash > 0.01f)
            scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = HellColor.Flash, Alpha = tollFlash * alpha, ColorOverride = color });

        // Firelight thrown up from below once the circle has caught.
        if (lit > 0.01f)
        {
            float reach = shortSide * 0.36f;
            scene.AddRegion(new RegionPrimitive { Min = new Vector2(0f, size.Y - reach), Max = size, Alpha = MathF.Min(1f, 0.55f * alpha * lit * (breath + 0.45f * _toll.Hit)), Bottom = true, ColorOverride = color });
        }
    }

    // ---- the circle ----

    private void EmitCircle(EffectScene scene, in LayerCamera cam, float shortSide, float alpha, float age, float breath, float leave,
                            ReadOnlySpan<ShockFront> fronts, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;
        Array.Clear(_penOn);

        // Far: one thin ring, nearly off the screen, that only shows at the sides.
        {
            float h = FarHeight * px, scale = cam.ScaleAt(h);
            float rs = FarRadius * shortSide;
            float reveal = Prog(age, FarStart, FarSecs);
            float excite = ShockFront.Excite(fronts, rs);
            SigilGeometry.Ring(_far, RingSegments, in cam, SigilGeometry.PlaneRadius(rs, h, dist), h, _farAng + FarSpin * age, RingWobble.None);
            AddBrand(scene, _far, FarWidth * px * scale, alpha * breath * 0.55f * (1f + 0.5f * excite), 0.65f, excite, reveal, 0f, 1, color);
        }

        // Blood: the hero ring, under a halo of its own so any material keeps the hell-light.
        {
            float h = BloodHeight * px;
            float rs = BloodRadius * shortSide;
            float reveal = Prog(age, BloodStart, BloodSecs);
            float excite = ShockFront.Excite(fronts, rs);
            float bright = alpha * breath;

            var wobble = new RingWobble(0.008f + 0.014f * excite, 3, _seed * 0.0007f - 0.30f * age, 0.005f, 5, _seed * 0.0011f + 0.42f * age);
            // The ring is round, so turning it shows only in its wobble, which already moves; its seam (a hairline in a glow, which cannot be seamless) is pinned to the top or bottom, where this ring is off screen.
            SigilGeometry.Ring(_blood, BloodSegments, in cam, SigilGeometry.PlaneRadius(rs, h, dist), h, _bloodSeam, in wobble);

            // The link count is pinned (the width follows the ring's length), so a chain never re-spaces its links as the ring zooms or tilts.
            float width = _blood.Length / (1.2f * BloodHalfLinks);
            float agit = DrawHelpers.Saturate(excite * 0.8f);

            scene.AddStroke(new StrokePrimitive
            {
                Path = _blood, Role = PrimitiveRole.Halo, Reveal = reveal, WidthHint = width * HaloWidthOverRing,
                Brightness = bright * 0.95f, Seed = _seed + 21, Phase = 2f, Depth = 0.30f, Agitation = agit, Closed = true, ColorOverride = color,
            });
            scene.AddStroke(new StrokePrimitive
            {
                Path = _blood, Role = PrimitiveRole.MainStroke, Reveal = reveal, WidthHint = width,
                Brightness = bright, Seed = _seed + 22, Phase = 0.7f, Depth = 0.06f, Agitation = agit,
                TipFlare = reveal < 0.999f ? 1f : 0f, Closed = true, ColorOverride = color,
            });
            TrackPen(0, _blood, reveal);
        }

        // Band: two thin rings with the runes between them (EmitRunes).
        {
            float h = BandHeight * px, scale = cam.ScaleAt(h);
            float eo = BandOuter * shortSide, ei = BandInner * shortSide;
            float ro = Prog(age, BandStart, BandSecs), ri = Prog(age, BandStart + 0.10f, BandSecs);
            float xo = ShockFront.Excite(fronts, eo), xi = ShockFront.Excite(fronts, ei);
            float bright = alpha * breath;
            SigilGeometry.Ring(_bandOuter, RingSegments, in cam, SigilGeometry.PlaneRadius(eo, h, dist), h, _bandAng + BandSpin * age, RingWobble.None);
            SigilGeometry.Ring(_bandInner, RingSegments, in cam, SigilGeometry.PlaneRadius(ei, h, dist), h, _bandAng + BandSpin * age, RingWobble.None);
            AddBrand(scene, _bandOuter, BandWidth * px * scale, bright * 0.80f * (1f + 0.5f * xo), 0.10f, xo, ro, 0f, 3, color);
            AddBrand(scene, _bandInner, BandWidth * px * scale, bright * 0.80f * (1f + 0.5f * xi), 0.10f, xi, ri, 0f, 4, color);
            TrackPen(1, _bandOuter, ro);
            TrackPen(2, _bandInner, ri);
        }

        // Circle and star: the inner ring, and the star drawn in one stroke inside it.
        {
            float h = CircleHeight * px, scale = cam.ScaleAt(h);
            float rs = CircleRadius * shortSide;
            float planeR = SigilGeometry.PlaneRadius(rs, h, dist);
            float rc = Prog(age, CircleStart, CircleSecs), rst = Prog(age, StarStart, StarSecs);
            float excite = ShockFront.Excite(fronts, rs * 0.8f);
            float bright = alpha * breath;
            float ang = _circleAng + CircleSpin * age;

            SigilGeometry.Ring(_circle, RingSegments, in cam, planeR, h, ang, RingWobble.None);
            SigilGeometry.Star(_star, in cam, _starN, _starK, planeR, h, ang + MathF.PI * 0.5f);

            // Under the ring of fire, a glow the flames stand in.
            float fire = FireLevel(age, leave);
            if (fire > 0.01f)
                scene.AddStroke(new StrokePrimitive
                {
                    Path = _circle, Role = PrimitiveRole.Halo, Reveal = 1f, WidthHint = FireGlowWidth * px * scale,
                    Brightness = MathF.Min(1f, alpha * fire) * 0.80f, Seed = _seed + 23, Phase = 3f, Depth = 0f, Agitation = DrawHelpers.Saturate(excite), Closed = true, ColorOverride = color,
                });

            AddMagma(scene, _circle, CircleWidth * px * scale, bright * 0.95f, excite, rc, 5, color);
            AddMagma(scene, _star, StarWidth * px * scale, bright, excite, rst, 6, color);
            TrackPen(3, _circle, rc);
            TrackPen(4, _star, rst);
        }
    }

    private void AddBrand(EffectScene scene, StrandPath path, float width, float brightness, float depth, float excite,
                          float reveal, float dash, int salt, Vector4? color)
    {
        if (brightness <= 0.003f || reveal <= 0.001f) return;
        scene.AddStroke(new StrokePrimitive
        {
            Path = path, Role = PrimitiveRole.Inscription, Reveal = reveal, WidthHint = width, Brightness = brightness,
            Seed = _seed + salt * 101, Phase = salt * 1.7f, Depth = depth, Agitation = DrawHelpers.Saturate(excite * 0.7f),
            TipFlare = reveal < 0.999f ? 1f : 0f, Closed = true, Dash = dash, ColorOverride = color,
        });
    }

    /// <summary>A bar of molten metal (the Edge role): the star and the circle, which carry the heat.</summary>
    private void AddMagma(EffectScene scene, StrandPath path, float width, float brightness, float excite, float reveal, int salt, Vector4? color)
    {
        if (brightness <= 0.003f || reveal <= 0.001f) return;
        scene.AddStroke(new StrokePrimitive
        {
            Path = path, Role = PrimitiveRole.Edge, Reveal = reveal, WidthHint = width, Brightness = brightness,
            Seed = _seed + salt * 101, Phase = salt * 1.7f, Depth = 0f, Agitation = DrawHelpers.Saturate(excite * 0.8f),
            TipFlare = reveal < 0.999f ? 1f : 0f, Closed = true, ColorOverride = color,
        });
    }

    /// <summary>Remembers where the pen of this stroke is while it is still being drawn.</summary>
    private void TrackPen(int i, StrandPath path, float reveal)
    {
        if (reveal <= 0.001f || reveal >= 0.999f) return;
        path.SampleAtArc(path.Length * reveal, out _pen[i], out _);
        _penOn[i] = true;
    }

    // ---- runes ----

    private void EmitRunes(EffectScene scene, in LayerCamera cam, float alpha, float age, float breath, float leave, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;
        float h = BandHeight * px;
        float shortSide = px * 1080f;
        float planeR = SigilGeometry.PlaneRadius(RuneRadius * shortSide, h, dist);
        float ang = _bandAng + BandSpin * age;
        float reveal = Prog(age, BandStart + 0.05f, BandSecs);
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
                Brightness = MathF.Min(1f, alpha * breath * 0.92f * lit * boost), Seed = seed, Role = PrimitiveRole.Rune, ColorOverride = color,
                Variant = 1, Rotation = MathF.Atan2(outward.X, -outward.Y),
            });
        }
    }

    // ---- sparks off the pen ----

    private void EmitSparks(EffectScene scene, float time, float dt, float alpha, float age, Vector4? color)
    {
        _sparks.Step(time, dt);

        if (age < IgniteAt + 0.05f && time >= _sparkNext)
        {
            _sparkNext = time + 0.045f;
            for (int i = 0; i < Pens; i++)
            {
                if (!_penOn[i]) continue;
                _sparkAt = _pen[i];
                _sparks.Burst(1, time + i * 0.0137f, _sparkPos, _sparkVel, 0.5f, 1.1f, 1.8f * _px, 3.4f * _px);
            }
        }
        _sparks.Emit(scene, time, PrimitiveRole.Cinder, brightnessMul: alpha, colorOverride: color);
    }

    // ---- horns ----

    /// <summary>Two horns rising out of the bottom corners and curling in over the circle, as if what is being summoned were climbing up into view. They are lit by the fire at the foot of the screen.</summary>
    private void EmitHorns(EffectScene scene, Vector2 size, float alpha, float age, float leave, float fire, Vector4? color)
    {
        for (int side = 0; side < 2; side++)
        {
            float sgn = side == 0 ? -1f : 1f;                                       // -1: left horn (curls right), +1: right horn (curls left)
            float grow = Prog(age, HornStart + 0.12f * side, HornSecs) * (1f - DrawHelpers.Smooth(leave * 1.4f));
            if (grow <= 0.001f) continue;

            float len = (_hornLen + _hornSkew * sgn) * size.Y;
            float curl = _hornCurl + 0.12f * sgn * _hornSkew;
            Vector2 b = new(size.X * (side == 0 ? 0.050f : 0.950f), size.Y * 1.05f);
            Vector2 c = b + new Vector2(sgn * 0.075f * size.X * curl, -0.52f * len);       // bows out, away from the middle ...
            Vector2 t = b + new Vector2(-sgn * 0.19f * size.X * curl, -0.93f * len);       // ... and the tip comes back in over the circle

            // The horn sways a hair about its root.
            float sway = 0.014f * MathF.Sin(age * 0.55f + side * 2.1f + _seed * 0.01f);
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
                Path = path, Role = PrimitiveRole.Horn, Reveal = grow, WidthHint = _hornBase * size.Y,
                Brightness = alpha, Seed = _seed + 61 + side * 17, Depth = 0f, Agitation = DrawHelpers.Saturate(fire * 0.7f), Closed = false, ColorOverride = color,
            });
        }
    }

    // ---- hellfire, candles, smoke, cinders, embers ----

    /// <summary>How much fire there is: none until the circle ignites, a roar as it takes, then a steady burn that gutters out as the curse lifts.</summary>
    private float FireLevel(float age, float leave)
    {
        float ti = age - IgniteAt;
        if (ti <= 0f) return 0f;
        float ramp = DrawHelpers.Smooth(ti / 0.7f);
        float roar = 0.55f * MathF.Exp(-ti * 2.2f) * DrawHelpers.Smooth(ti / 0.15f);
        // A toll draws the fire in as it charges and bursts it out as it lands.
        float toll = -0.28f * _toll.Charge + 0.62f * _toll.Power * _toll.Hit + 0.30f * _toll.Echo;
        return (ramp + roar + toll) * (1f - DrawHelpers.Smooth(leave * 1.6f));
    }

    /// <summary>Where the inner circle is right now: the plane radius, height and turning angle every piece standing on it shares.</summary>
    private void CircleFrame(float age, out float planeR, out float h, out float ang, out float shortSide)
    {
        float px = _px;
        shortSide = px * 1080f;
        h = CircleHeight * px;
        planeR = SigilGeometry.PlaneRadius(CircleRadius * shortSide, h, CameraDistance * px);
        ang = _circleAng + CircleSpin * age;
    }

    private void EmitFlames(EffectScene scene, in LayerCamera cam, float shortSide, float alpha, float time, float age, float fire, bool back, Vector4? color)
    {
        if (fire <= 0.01f) return;
        CircleFrame(age, out float planeR, out float h, out float ang, out _);
        float px = _px;

        // Back tongues are tall, dim and soft; body and front are the fire proper (front: short and white-hot).
        for (int layer = back ? 0 : 1; layer < (back ? 1 : 3); layer++)
        {
            int variant = layer switch { 0 => (int)VariantBack, 1 => (int)VariantBody, _ => (int)VariantFront };
            float period = layer switch { 0 => 1.65f, 1 => 1.10f, _ => 0.82f };
            int perSite = layer == 0 ? 1 : 2;
            float sizeLo = layer switch { 0 => 23f, 1 => 17f, _ => 11f };
            float sizeHi = layer switch { 0 => 33f, 1 => 26f, _ => 17f };

            for (int site = 0; site < FireSites; site++)
            {
                int ss = _seed + site * 977 + layer * 131;
                float a0 = ang + MathF.Tau * (site + DrawHelpers.HashRange(ss + 1, -0.30f, 0.30f)) / FireSites;
                Vector2 at = cam.Project(planeR * MathF.Cos(a0), planeR * MathF.Sin(a0), h, out float scale);

                // Tongues that rise from the top of the screen leave it at once; those from the bottom climb into view. So the bottom burns taller.
                float low = DrawHelpers.Saturate((at.Y - _center.Y) / (0.5f * shortSide) * 0.5f + 0.5f);
                float weight = 0.55f + 0.65f * low;
                float site01 = 0.78f + 0.44f * DrawHelpers.Hash01(ss + 2);          // some stretches burn hotter than others

                for (int t = 0; t < perSite; t++)
                {
                    float cyc = time / (period * DrawHelpers.HashRange(ss + 3, 0.85f, 1.15f)) + DrawHelpers.Hash01(ss + 4 + t * 7) + t / (float)perSite;
                    float life = cyc - MathF.Floor(cyc);
                    int id = (int)MathF.Floor(cyc);
                    int seed = ss * 31 + id * 7 + t * 13;

                    float size = DrawHelpers.HashRange(seed + 1, sizeLo, sizeHi) * px * scale * weight * site01 * MathF.Min(fire, 1.25f);
                    scene.AddParticle(new ParticlePrimitive
                    {
                        Position = at + new Vector2(DrawHelpers.HashRange(seed + 2, -9f, 9f) * px, -size * 0.85f),   // a flame's skirt dissolves below its base, so it is set on the circle
                        Velocity = new Vector2(DrawHelpers.HashRange(seed + 3, -12f, 12f), -30f),
                        AgeRatio = life, Size = size, Brightness = ParticleEmitter.FadeFor(life) * MathF.Min(1f, alpha * fire * 1.1f),
                        Seed = seed, Role = PrimitiveRole.Ember, Variant = variant, ColorOverride = color,
                    });
                }
            }
        }
    }

    private void EmitCandles(EffectScene scene, in LayerCamera cam, float alpha, float age, float breath, float leave, Vector4? color)
    {
        CircleFrame(age, out float planeR, out float h, out float ang, out _);
        float px = _px;
        float pen = Prog(age, StarStart, StarSecs);
        float starAng = ang + MathF.PI * 0.5f;

        for (int j = 0; j < _starN; j++)
        {
            // The pen reaches corner j when the star is j/n drawn: that is when its candle stands up. It takes light after the circle ignites, in the order of the pen.
            int m = (j * _starK) % _starN;
            float appear = DrawHelpers.Saturate((pen * _starN - j) / 0.6f);
            if (appear <= 0.001f) continue;

            float a = starAng + MathF.Tau * m / _starN;
            Vector2 at = cam.Project(planeR * MathF.Cos(a), planeR * MathF.Sin(a), h, out float scale);
            int seed = _seed + m * 4513 + 29;

            float lit = DrawHelpers.Saturate((age - (IgniteAt + 0.10f + LightGap * j)) / 1.0f);
            float out01 = DrawHelpers.Smooth(leave * 2.2f);                                  // blown out as the curse lifts
            // The draught: a faint breath; drawn in toward the middle as a toll charges, then blown outward by it.
            float outward = at.X >= _center.X ? 1f : -1f;
            float draught = (0.7f * MathF.Sin(age * 0.8f + seed * 0.1f) - 7f * outward * _toll.Charge + 26f * outward * (_toll.Hit + 0.5f * _toll.Echo)) * px;
            float gutter = DrawHelpers.Saturate(0.30f * _toll.Charge + 0.62f * _toll.Hit + 0.30f * _toll.Echo);

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, AgeRatio = lit, Size = CandleSize * px * scale, Brightness = alpha * appear * (1f - 0.5f * out01),
                Seed = seed, Role = PrimitiveRole.Candle, ColorOverride = color,
                Rotation = 0.045f * MathF.Sin(age * 0.9f + seed * 0.37f), Sway = draught,
                Variant = (int)(255f * (1f - out01) * (1f - gutter)) | 0x100,
            });
        }
    }

    private void EmitCinders(EffectScene scene, float time, float dt, float alpha, float fire, Vector4? color)
    {
        _cinders.Update(time, dt, 0.06f / MathF.Max(0.15f, fire), 0.16f / MathF.Max(0.15f, fire), _ringPos, _cinderVel, 1.3f, 2.8f, 1.7f * _px, 3.6f * _px);
        if (_tollFired != _tollIndex && _toll.Hit > 0.5f)
        {
            _tollFired = _tollIndex;
            _cinders.Burst((int)(26 * _toll.Power), time, _ringPos, _cinderVel, 1.1f, 2.6f, 1.9f * _px, 4.2f * _px);
        }
        if (fire <= 0.01f) { _cinders.Clear(); return; }
        _cinders.Emit(scene, time, PrimitiveRole.Cinder, brightnessMul: MathF.Min(1f, alpha * fire), colorOverride: color);
    }

    private void EmitSmoke(EffectScene scene, float time, float dt, float shortSide, float alpha, float fire, Vector4? color)
    {
        _smoke.Update(time, dt, 0.18f / MathF.Max(0.2f, fire), 0.38f / MathF.Max(0.2f, fire), _ringPos, _smokeVel, 3.0f, 5.0f, 0.055f * shortSide, 0.105f * shortSide);
        if (fire <= 0.01f) { _smoke.Clear(); return; }
        _smoke.Emit(scene, time, PrimitiveRole.Smoke, brightnessMul: MathF.Min(1f, alpha * fire), colorOverride: color, swayPerParticle: 6f * _px);
    }

    /// <summary>Embers drifting up the whole depth of the view: far ones small and bright, near ones large soft discs (out of focus), each rising at its own speed.</summary>
    private void EmitMotes(EffectScene scene, Vector2 size, float shortSide, float alpha, float time, float age, Vector4? color)
    {
        float fire = DrawHelpers.Smooth((age - IgniteAt + 0.4f) / 1.2f);
        if (fire <= 0.01f) return;
        float px = _px;

        for (int i = 0; i < MoteCount; i++)
        {
            ref readonly var m = ref _motes[i];
            float z = m.Z;                                                    // 0 = far, 1 = at the lens
            float rise = (m.Phase * (size.Y + 160f * px) + time * m.Speed * (0.5f + 1.0f * z) * px) % (size.Y + 160f * px);
            float y = size.Y + 80f * px - rise;
            float x = size.X * m.X + MathF.Sin(time * 0.7f + m.Phase * 31f) * m.Sway * px * (0.6f + z);
            float fade = DrawHelpers.Saturate(MathF.Min(rise / (size.Y * 0.12f), (size.Y + 160f * px - rise) / (size.Y * 0.3f)));

            scene.AddParticle(new ParticlePrimitive
            {
                Position = new Vector2(x, y), AgeRatio = 0.5f, Size = m.Size * px * (0.7f + 2.6f * z * z),
                Brightness = alpha * fire * fade * (0.85f - 0.40f * z), Seed = m.Seed, Role = PrimitiveRole.Mote, ColorOverride = color,
            });
        }
    }
}

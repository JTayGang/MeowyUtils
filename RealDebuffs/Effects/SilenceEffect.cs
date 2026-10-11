using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Silence: a curse seal stamped over the screen. Five layers of rings, runes and two turning triangles hang at different depths and turn against each other as the whole stack slowly precesses; a shock swallows the sound, the layers slam down from the lens, and a seal burns on with light running round it and the odd hush rippling out. The outer binding ring is the hero: made of chains, rope or tentacles it keeps the glow.</summary>
public sealed class SilenceEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Silence;
    public string DisplayName => "Silence";
    public string Description => "A curse seal slams down around the screen, then burns on: rings turning at different depths, drifting runes, violet light.";
    public int DrawOrder => 6;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Silence"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "silence", "silenced", "mute", "muted", "gagged", "voiceless"
    };

    // The binding ring is what a "made of X" phrase replaces; a particle in that phrase also replaces the runes.
    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke),
        new("Particle", PrimitiveRole.Rune),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke,  "Binding ring",  "stroke.sigil"),
        new("Stroke",   PrimitiveRole.Halo,        "Ring glow",     "stroke.glow"),
        new("Stroke",   PrimitiveRole.Inscription, "Inner sigil",   "stroke.sigil"),
        new("Particle", PrimitiveRole.Rune,        "Runes",         "particle.rune"),
        new("Particle", PrimitiveRole.Mote,        "Motes",         "particle.mote"),
        new("Particle", PrimitiveRole.Miasma,      "Miasma",        "particle.miasma"),
        new("Region",   PrimitiveRole.MainStroke,  "Curse veil",    "region.edge-glow", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke,  "Seal flash",    "region.flat-fill", "FlatFill"),
    };

    // ---- the camera ----
    private const float CameraDistance = 1500f;     // px at 1080p; smaller = stronger perspective
    private const float BaseTilt = 0.15f;           // radians the stack leans from the line of sight
    private const float TiltSway = 0.035f;
    private const float PrecessionRate = 0.21f;     // rad/s the lean swings round (a lap every ~30 s)
    private const float LurchTilt = 0.20f;          // extra lean as the seal lands, settling over LurchSeconds
    private const float LurchSeconds = 0.9f;

    // ---- layout: what the viewer sees ----
    // Radii are fractions of the short side as they land on screen; heights are px toward the viewer (negative = behind the screen plane).
    private const float FarHeight = -330f,  HeroHeight = -150f, SealHeight = 0f, NodeHeight = 240f;
    private const float TriHeightA = 95f,   TriHeightB = 180f;
    private const float FarOuter = 0.800f,  FarInner = 0.735f,  FarRunes = 0.7675f;
    private const float HeroRadius = 0.625f;
    private const float SealOuter = 0.515f, SealInner = 0.485f;
    private const float TriRadius = 0.455f;
    private const float NodeRing = 0.385f,  NodeRadius = 0.017f;
    private const int   NodeCount = 12, FarRuneCount = 44;

    // Spin, rad/s, per layer (signs alternate so neighbours turn against each other).
    private const float FarSpin = 0.040f, HeroSpin = -0.030f, SealSpin = 0.055f, TriSpinA = -0.085f, TriSpinB = 0.070f, NodeSpin = 0.110f;

    // ---- the hero ring ----
    private const int   HeroHalfLinks = 90;         // a chain gets 2x this many links however big the ring is (see HeroWidth)
    private const float HeroWobble = 0.010f;        // radius wobble at rest, as a fraction
    private const float HaloWidthOverRing = 2.4f;

    // ---- line weights, px at 1080p (the glowing width; a physical material reads them as diameters) ----
    private const float FarWidth = 10f, SealWidth = 12f, SealDashWidth = 9f, TriWidth = 11f, NodeWidth = 8f, NodeCircleWidth = 7f, ShockWidth = 15f;
    private const float SealDash = 30f;

    // ---- the cast ----
    private const float ArriveLift = 520f;          // px toward the lens a layer starts from
    private const float ArriveSeconds = 0.62f;
    private const float LockSeconds = 0.70f;        // when the seal lands: the outward shock and the sparks
    private static readonly float[] ArriveAt = { 0.08f, 0.20f, 0.32f, 0.44f, 0.56f };   // far, hero, seal, triangles, nodes
    private const float SpinIn = 1.5f;              // extra turns, in radians, a layer sheds as it lands
    private const float LeaveLift = 300f;           // px a layer drifts toward the lens as the curse lifts

    // ---- the steady state ----
    private const float FirstHush = 2.8f;
    private const float HushSecondsLong = 2.4f;
    private const float HushGain = 0.55f;

    // ---- runes and motes ----
    private const float RuneSize = 27f;             // half-height in px at 1080p
    private const int   MaxFloating = 14;
    private const int   MoteCount = 64;
    private const float MoteNear = 700f, MoteFar = -450f;   // heights the motes fill

    private static readonly uint Flash = CurseColor.Flash;
    private static readonly uint Hush = DrawHelpers.Pack(0.060f, 0.012f, 0.100f);
    private static readonly uint Veil = DrawHelpers.Pack(0.58f, 0.13f, 0.82f);

    // ---- state ----
    private enum Layer { Far, Hero, Seal, Triangles, Nodes }

    private const int RingSegments = 128, HeroSegments = 144, NodeSegments = 20, ShockSegments = 96;

    private readonly StrandPath _farOuter = new(RingSegments + 1), _farInner = new(RingSegments + 1);
    private readonly StrandPath _hero = new(HeroSegments + 1);
    private readonly StrandPath _sealOuter = new(RingSegments + 1), _sealInner = new(RingSegments + 1);
    private readonly StrandPath _triA = new(4), _triB = new(4);
    private readonly StrandPath _nodeRing = new(RingSegments + 1);
    private readonly StrandPath[] _nodes = new StrandPath[NodeCount];
    private readonly StrandPath _shock = new(ShockSegments + 1);

    private struct Floating
    {
        public float Angle, Radius, Height, Lift, Spin, Born, Life, Size, Tilt;
        public int Seed;
    }

    private struct Mote
    {
        public float Fx, Fy, Z, Speed, Sway, Phase, Size;
        public int Seed;
    }

    private readonly Floating[] _floating = new Floating[MaxFloating];
    private int _floatingCount;
    private readonly Mote[] _motes = new Mote[MoteCount];

    private readonly CastTracker _cast = new();
    private int _seed;
    private float _azimuth, _azimuthDir, _hushEvery, _nextRune, _lastAlpha, _peakAlpha, _rampLead;
    private bool _leaving, _stamped;
    private int _runeSpawns;

    // Miasma wisps are the one emitter-driven particle; callbacks are built once, so nothing allocates per frame.
    private readonly ParticleEmitter _miasma = new(maxParticles: 12, seedSalt: 0x51A3C3);
    private readonly Func<int, Vector2> _miasmaPos, _miasmaVel;
    private float _px = 1f;
    private Vector2 _center;

    private struct Ripple
    {
        public float Front, Width, Gain;
    }

    public SilenceEffect()
    {
        for (int i = 0; i < NodeCount; i++) _nodes[i] = new StrandPath(NodeSegments + 1);

        // Wisps leave the binding ring and drift off it, inward or out.
        _miasmaPos = seed =>
        {
            int i = (int)((uint)seed % (uint)Math.Max(1, _hero.Count - 1));
            return _hero.Points[i] + new Vector2(DrawHelpers.HashRange(seed + 7, -30f, 30f), DrawHelpers.HashRange(seed + 8, -30f, 30f)) * _px;
        };
        _miasmaVel = seed =>
        {
            int i = (int)((uint)seed % (uint)Math.Max(1, _hero.Count - 1));
            Vector2 from = _hero.Points[i] - _center;
            Vector2 dir = from.LengthSquared() > 1f ? Vector2.Normalize(from) : new Vector2(0f, -1f);
            float side = DrawHelpers.Hash01(seed + 3) < 0.5f ? -1f : 1f;
            return (dir * side * DrawHelpers.HashRange(seed + 4, 6f, 20f) + new Vector2(0f, DrawHelpers.HashRange(seed + 5, -14f, -3f))) * _px;
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

        // Alpha only falls while the status is lifting; the curse then loosens its hold instead of simply dimming.
        if (alpha > _lastAlpha + 1e-4f) _leaving = false;
        else if (alpha < _lastAlpha - 1e-4f) _leaving = true;
        _lastAlpha = alpha;
        if (!_leaving) _peakAlpha = MathF.Max(_peakAlpha, alpha);
        float leave = _leaving && _peakAlpha > 0.01f ? DrawHelpers.Saturate(1f - alpha / _peakAlpha) : 0f;

        // The manager fades alpha in from nothing over FadeInSeconds, which would hide most of the cast. The fade is linear, so alpha * FadeInSeconds / rampAge
        // is the full strength: ride the ramp up to three times faster (punch), and let the cast's own flash ignore it (gate). The fade-out is left alone.
        float gate = alpha;
        if (!_leaving && age + _rampLead < EffectManager.FadeInSeconds)
        {
            float toFull = EffectManager.FadeInSeconds / MathF.Max(age + _rampLead, 0.001f);
            gate = MathF.Min(alpha * toFull, 1f);
            alpha *= MathF.Min(3f, toFull);
        }

        float tilt = BaseTilt + TiltSway * MathF.Sin(age * 0.23f + 1.3f)
                   + LurchTilt * MathF.Exp(-age / LurchSeconds) + 0.10f * leave;
        float azimuth = _azimuth + _azimuthDir * PrecessionRate * age;
        var cam = new LayerCamera(_center, CameraDistance * _px, tilt, azimuth);

        float breath = 0.90f + 0.10f * MathF.Sin(age * 1.9f + 0.7f);

        Span<Ripple> ripples = stackalloc Ripple[3];
        int rippleCount = BuildRipples(ripples, age, shortSide);

        EmitAtmosphere(scene, screenSize, shortSide, alpha, gate, age, breath, colorOverride);
        EmitRings(scene, in cam, shortSide, alpha, age, breath, leave, ripples[..rippleCount], colorOverride);
        EmitFarRunes(scene, in cam, shortSide, alpha, age, breath, leave, ripples[..rippleCount], colorOverride);
        EmitFloatingRunes(scene, in cam, alpha, age, colorOverride);
        EmitMiasma(scene, time, dt, alpha, age, colorOverride);
        EmitMotes(scene, in cam, screenSize, alpha, age, leave, colorOverride);
    }

    // ---- the cast ----

    private void BeginCast(int seed, float dt)
    {
        _seed = seed;
        _rampLead = dt;
        _azimuth = DrawHelpers.Hash01(seed + 1) * MathF.Tau;
        _azimuthDir = DrawHelpers.Hash01(seed + 2) < 0.5f ? -1f : 1f;
        _hushEvery = DrawHelpers.HashRange(seed + 3, 9f, 12f);
        _nextRune = 1.1f;
        _runeSpawns = 0;
        _floatingCount = 0;
        _stamped = false;
        _leaving = false;
        _peakAlpha = 0f;
        _lastAlpha = 0f;
        _miasma.Clear();

        // Motes fill the view frustum at each depth (Fx, Fy are -1..1 across the screen there), and drift up at a world speed, so near ones cross the screen faster.
        for (int i = 0; i < MoteCount; i++)
        {
            int s = seed + i * 6151 + 11;
            float z = MoteFar + (MoteNear - MoteFar) * MathF.Pow(DrawHelpers.Hash01(s + 1), 1.5f);
            _motes[i] = new Mote
            {
                Fx = DrawHelpers.HashRange(s + 2, -1.12f, 1.12f),
                Fy = DrawHelpers.HashRange(s + 3, -1.12f, 1.12f),
                Z = z,
                Speed = DrawHelpers.HashRange(s + 4, 10f, 34f),
                Sway = DrawHelpers.HashRange(s + 5, 12f, 46f),
                Phase = DrawHelpers.Hash01(s + 6) * MathF.Tau,
                Size = DrawHelpers.HashRange(s + 7, 1.8f, 4.4f),
                Seed = s,
            };
        }
    }

    /// <summary>The shocks running through the stack, strongest first-ish: sound being swallowed (inward), the seal locking (outward), and the occasional hush once it has settled.</summary>
    private int BuildRipples(Span<Ripple> into, float age, float shortSide)
    {
        int n = 0;

        // 1. The sound is drawn in: a ring collapsing from beyond the screen to the middle.
        if (age < 0.60f)
        {
            float p = age / 0.60f;
            into[n++] = new Ripple
            {
                Front = shortSide * (1.15f * (1f - p * p) + 0.04f),
                Width = shortSide * 0.07f,
                Gain = 0.9f * MathF.Sin(MathF.PI * MathF.Min(1f, p * 1.15f)),
            };
        }

        // 2. The seal locks and throws a shock back out through every layer.
        float tl = age - LockSeconds;
        if (tl > 0f && tl < 1.1f)
        {
            into[n++] = new Ripple
            {
                Front = shortSide * (0.05f + 1.45f * tl),
                Width = shortSide * 0.06f,
                Gain = 1.2f * MathF.Pow(1f - tl / 1.1f, 1.4f),
            };
        }

        // 3. Settled: now and then the curse reasserts itself, softly.
        float th = age - FirstHush;
        if (th > 0f && n < into.Length)
        {
            float tp = th % _hushEvery;
            if (tp < HushSecondsLong)
            {
                float u = tp / HushSecondsLong;
                into[n++] = new Ripple
                {
                    Front = shortSide * (0.20f + 0.90f * DrawHelpers.EaseOutCubic(u)),
                    Width = shortSide * 0.07f,
                    Gain = HushGain * (1f - DrawHelpers.Smooth(u)),
                };
            }
        }
        return n;
    }

    private static float Excite(ReadOnlySpan<Ripple> ripples, float screenRadius)
    {
        float e = 0f;
        foreach (var r in ripples)
        {
            float d = (screenRadius - r.Front) / r.Width;
            e += r.Gain * MathF.Exp(-d * d);
        }
        return MathF.Min(e, 1.8f);
    }

    /// <summary>How far a layer has landed (0..1), how far it still has to fall toward its place, and how solid it is.</summary>
    private static void Arrive(float age, Layer layer, float leave, out float lift, out float appear, out float landed)
    {
        landed = DrawHelpers.Saturate((age - ArriveAt[(int)layer]) / ArriveSeconds);
        lift = (1f - EaseOutBack(landed)) * ArriveLift + leave * LeaveLift;
        appear = DrawHelpers.Smooth(landed / 0.40f) * (1f - DrawHelpers.Smooth(leave));
    }

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.35f;
        float u = t - 1f;
        return 1f + (c1 + 1f) * u * u * u + c1 * u * u;
    }

    // ---- atmosphere ----

    private static void EmitAtmosphere(EffectScene scene, Vector2 size, float shortSide, float alpha, float castGate, float age,
                                       float breath, Vector4? colorOverride)
    {
        float castIn = DrawHelpers.Smooth(age / 0.45f);

        scene.RequestVignette(CurseColor.Vignette, 0.17f, alpha * 0.62f * castIn * breath, priority: 15, colorOverride);

        // Light from the seal spills onto the screen's edges.
        float depth = shortSide * 0.13f;
        float a = 0.20f * alpha * castIn * breath;
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new Vector2(size.X, depth), Tint = Veil, Alpha = a, Top = true, ColorOverride = colorOverride });
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(0f, size.Y - depth), Max = size, Tint = Veil, Alpha = a, Bottom = true, ColorOverride = colorOverride });
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new Vector2(depth, size.Y), Tint = Veil, Alpha = a, Left = true, ColorOverride = colorOverride });
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(size.X - depth, 0f), Max = size, Tint = Veil, Alpha = a, Right = true, ColorOverride = colorOverride });

        // The flash the curse lands with, then the world going quiet for a beat.
        // (These two are the cast itself, so they use castGate rather than alpha: the manager's fade-in would swallow the flash.)
        float flash = age < 0.45f ? MathF.Exp(-age / 0.10f) : 0f;
        if (flash > 0.01f)
            scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = Flash, Alpha = 0.42f * flash * castGate, ColorOverride = colorOverride });

        float hush = DrawHelpers.Smooth(age / 0.25f) * (1f - DrawHelpers.Smooth((age - 0.35f) / 1.3f));
        if (hush > 0.01f)
            scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = Hush, Alpha = 0.34f * hush * castGate, ColorOverride = colorOverride });
    }

    // ---- the rings ----

    private void EmitRings(EffectScene scene, in LayerCamera cam, float shortSide, float alpha, float age, float breath, float leave,
                           ReadOnlySpan<Ripple> ripples, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;

        // Far band: two thin rings with the runes between them (EmitFarRunes).
        {
            Arrive(age, Layer.Far, leave, out float lift, out float appear, out float landed);
            float h = FarHeight * px + lift * px;
            float ang = FarSpin * age + SpinIn * (1f - landed) * (1f - landed);
            float scale = cam.ScaleAt(h);
            float eo = FarOuter * shortSide, ei = FarInner * shortSide;
            float bright = alpha * appear * breath;

            SigilGeometry.Ring(_farOuter, RingSegments, in cam, SigilGeometry.PlaneRadius(eo, h, dist), h, ang, RingWobble.None);
            SigilGeometry.Ring(_farInner, RingSegments, in cam, SigilGeometry.PlaneRadius(ei, h, dist), h, ang, RingWobble.None);
            AddInscription(scene, _farOuter, FarWidth * px * scale, bright * 0.62f * (1f + 0.5f * Excite(ripples, eo)), 0.65f, Excite(ripples, eo), landed, 0f, 1, color);
            AddInscription(scene, _farInner, FarWidth * 0.7f * px * scale, bright * 0.50f * (1f + 0.5f * Excite(ripples, ei)), 0.65f, Excite(ripples, ei), landed, 0f, 2, color);
        }

        // Hero: the binding ring, under a halo of its own so any material keeps the curse-light.
        {
            Arrive(age, Layer.Hero, leave, out float lift, out float appear, out float landed);
            float h = HeroHeight * px + lift * px;
            float ang = HeroSpin * age - SpinIn * (1f - landed) * (1f - landed);
            float eff = HeroRadius * shortSide;
            float excite = Excite(ripples, eff);
            float bright = alpha * appear * breath * (0.82f + 0.30f * excite);

            var wobble = new RingWobble(HeroWobble + 0.016f * excite, 3, _seed * 0.0007f - 0.35f * age,
                                        HeroWobble * 0.6f, 5, _seed * 0.0011f + 0.52f * age);
            SigilGeometry.Ring(_hero, HeroSegments, in cam, SigilGeometry.PlaneRadius(eff, h, dist), h, ang, in wobble);

            // Link count is pinned: the width follows the ring's length, so a chain never re-spaces its links as the ring zooms or tilts.
            float heroWidth = _hero.Length / (1.2f * HeroHalfLinks);
            float agit = DrawHelpers.Saturate(excite * 0.8f);

            scene.AddStroke(new StrokePrimitive
            {
                Path = _hero, Role = PrimitiveRole.Halo, Reveal = DrawHelpers.Saturate(landed * 1.5f), WidthHint = heroWidth * HaloWidthOverRing,
                Brightness = bright * 1.05f, Seed = _seed + 21, Phase = 2f, Depth = 0.30f, Agitation = agit,
                Closed = true, ColorOverride = color,
            });
            scene.AddStroke(new StrokePrimitive
            {
                Path = _hero, Role = PrimitiveRole.MainStroke, Reveal = DrawHelpers.Saturate(landed * 1.5f), WidthHint = heroWidth,
                Brightness = bright, Seed = _seed + 22, Phase = 0.7f, Depth = 0.30f, Agitation = agit,
                TipFlare = landed < 0.66f ? 1f : 0f, Closed = true, ColorOverride = color,
            });

            // The seal lands: light thrown off the ring all the way round (whatever the ring is made of decides what).
            if (!_stamped && age >= LockSeconds)
            {
                _stamped = true;
                for (int k = 0; k < 9; k++)
                {
                    float a = ang + MathF.Tau * (k + DrawHelpers.Hash01(_seed + k * 3) * 0.6f) / 9f;
                    Vector2 at = SigilGeometry.PointOn(in cam, SigilGeometry.PlaneRadius(eff, h, dist), h, a, out _, out Vector2 outward);
                    if (at.X < 0f || at.Y < 0f || at.X > scene.ScreenSize.X || at.Y > scene.ScreenSize.Y) continue;

                    scene.AddImpact(new ImpactPrimitive
                    {
                        StrokeRole = PrimitiveRole.MainStroke, Position = at, Direction = outward, Strength = 0.6f,
                        Brightness = alpha, Seed = unchecked(_seed + 7919 * k), ColorOverride = color,
                    });
                }
            }
        }

        // Seal: a solid ring and a dashed one turning against it.
        {
            Arrive(age, Layer.Seal, leave, out float lift, out float appear, out float landed);
            float h = SealHeight * px + lift * px;
            float scale = cam.ScaleAt(h);
            float eo = SealOuter * shortSide, ei = SealInner * shortSide;
            float bright = alpha * appear * breath;

            SigilGeometry.Ring(_sealOuter, RingSegments, in cam, SigilGeometry.PlaneRadius(eo, h, dist), h, SealSpin * age + SpinIn * (1f - landed) * (1f - landed), RingWobble.None);
            SigilGeometry.Ring(_sealInner, RingSegments, in cam, SigilGeometry.PlaneRadius(ei, h, dist), h, -SealSpin * 1.6f * age - SpinIn * (1f - landed) * (1f - landed), RingWobble.None);
            AddInscription(scene, _sealOuter, SealWidth * px * scale, bright * 0.80f * (1f + 0.5f * Excite(ripples, eo)), 0.12f, Excite(ripples, eo), landed, 0f, 3, color);
            AddInscription(scene, _sealInner, SealDashWidth * px * scale, bright * 0.88f * (1f + 0.5f * Excite(ripples, ei)), 0.12f, Excite(ripples, ei), landed, SealDash * scale, 4, color);
        }

        // Triangles: two, at different depths, turning against each other. Now and then they line up as a hexagram.
        {
            Arrive(age, Layer.Triangles, leave, out float lift, out float appear, out float landed);
            float er = TriRadius * shortSide;
            float excite = Excite(ripples, er * 0.8f);
            float bright = alpha * appear * breath * 0.80f * (1f + 0.5f * excite);
            float turnIn = SpinIn * (1f - landed) * (1f - landed);

            float hA = TriHeightA * px + lift * px, hB = TriHeightB * px + lift * px;
            SigilGeometry.Polygon(_triA, in cam, 3, SigilGeometry.PlaneRadius(er, hA, dist), hA, TriSpinA * age - turnIn + _seed * 0.0003f);
            SigilGeometry.Polygon(_triB, in cam, 3, SigilGeometry.PlaneRadius(er, hB, dist), hB, TriSpinB * age + turnIn + MathF.PI / 3f + _seed * 0.0003f);
            AddInscription(scene, _triA, TriWidth * px * cam.ScaleAt(hA), bright, 0.0f, excite, landed, 0f, 5, color);
            AddInscription(scene, _triB, TriWidth * px * cam.ScaleAt(hB), bright * 0.92f, 0.0f, excite, landed, 0f, 7, color);
        }

        // Nodes: a thin ring nearest the lens with a bead at every twelfth.
        {
            Arrive(age, Layer.Nodes, leave, out float lift, out float appear, out float landed);
            float h = NodeHeight * px + lift * px;
            float scale = cam.ScaleAt(h);
            float en = NodeRing * shortSide;
            float excite = Excite(ripples, en);
            float bright = alpha * appear * breath * 0.78f * (1f + 0.5f * excite);
            float planeR = SigilGeometry.PlaneRadius(en, h, dist);
            float ang = NodeSpin * age + SpinIn * (1f - landed) * (1f - landed);

            SigilGeometry.Ring(_nodeRing, RingSegments, in cam, planeR, h, ang, RingWobble.None);
            AddInscription(scene, _nodeRing, NodeWidth * px * scale, bright, 0.0f, excite, landed, 0f, 6, color);

            float beadR = SigilGeometry.PlaneRadius(NodeRadius * shortSide, h, dist);
            for (int i = 0; i < NodeCount; i++)
            {
                float a = ang + MathF.Tau * i / NodeCount;
                SigilGeometry.Ring(_nodes[i], NodeSegments, in cam, beadR, h, 0f, RingWobble.None,
                                   planeR * MathF.Cos(a), planeR * MathF.Sin(a));
                AddInscription(scene, _nodes[i], NodeCircleWidth * px * scale, bright * 0.95f, 0.0f, excite, 1f, 0f, 10 + i, color);
            }
        }

        // The shock itself, drawn as a line of light riding the strongest ripple.
        int strongest = -1; float best = 0.05f;
        for (int i = 0; i < ripples.Length; i++)
            if (ripples[i].Gain > best) { best = ripples[i].Gain; strongest = i; }
        if (strongest >= 0)
        {
            var r = ripples[strongest];
            float planeR = MathF.Max(8f * px, r.Front);
            SigilGeometry.Ring(_shock, ShockSegments, in cam, planeR, 0f, 0f, RingWobble.None);

            // Dark at the middle of the screen, where the player is, and brighter as it reaches the rings (a bright ring dead centre reads as a crosshair).
            float reachIn = DrawHelpers.Smooth((r.Front - 0.08f * shortSide) / (0.22f * shortSide));
            AddInscription(scene, _shock, ShockWidth * px, alpha * MathF.Min(1.4f, r.Gain * 1.2f) * reachIn, 0.0f, r.Gain, 1f, 0f, 30, color);
        }
    }

    private void AddInscription(EffectScene scene, StrandPath path, float width, float brightness, float depth, float excite,
                                float landed, float dash, int salt, Vector4? color)
    {
        if (brightness <= 0.003f) return;

        float reveal = DrawHelpers.Saturate(landed * 1.35f);
        scene.AddStroke(new StrokePrimitive
        {
            Path = path, Role = PrimitiveRole.Inscription, Reveal = reveal, WidthHint = width, Brightness = brightness,
            Seed = _seed + salt * 101, Phase = salt * 1.7f, Depth = depth, Agitation = DrawHelpers.Saturate(excite * 0.7f),
            TipFlare = reveal < 0.999f ? 1f : 0f, Closed = true, Dash = dash,
            ColorOverride = color,
        });
    }

    // ---- runes ----

    private void EmitFarRunes(EffectScene scene, in LayerCamera cam, float shortSide, float alpha, float age, float breath, float leave,
                              ReadOnlySpan<Ripple> ripples, Vector4? color)
    {
        Arrive(age, Layer.Far, leave, out float lift, out float appear, out float landed);
        float px = _px, dist = CameraDistance * px;
        float h = FarHeight * px + lift * px;
        float ang = FarSpin * age + SpinIn * (1f - landed) * (1f - landed);
        float eff = FarRunes * shortSide;
        float planeR = SigilGeometry.PlaneRadius(eff, h, dist);
        float excite = Excite(ripples, eff);

        // A beam of light sweeps the band, so the runes read in turn.
        float scan = age * 0.62f + _seed * 0.001f;

        for (int i = 0; i < FarRuneCount; i++)
        {
            float u = i / (float)FarRuneCount;
            float a = ang + MathF.Tau * u;

            // They light in order round the ring as the seal lands.
            float ignite = DrawHelpers.Saturate((age - 0.70f - 0.9f * u) / 0.22f);
            float flare = ignite > 0f && ignite < 1f ? 1f + 1.4f * MathF.Sin(MathF.PI * ignite) : 1f;

            float d = MathF.Abs(MathF.IEEERemainder(a - scan, MathF.Tau)) / 0.30f;
            float sweep = MathF.Exp(-d * d);

            float k = alpha * appear * ignite * (0.70f + 0.95f * sweep + 0.5f * excite) * flare * (0.80f + 0.20f * breath);
            if (k <= 0.01f) continue;

            Vector2 at = SigilGeometry.PointOn(in cam, planeR, h, a, out float scale, out Vector2 outward);
            if (at.X < -60f || at.Y < -60f || at.X > scene.ScreenSize.X + 60f || at.Y > scene.ScreenSize.Y + 60f) continue;

            // Every 11th rune is one of the sealed-mouth marks.
            int seed = _seed + i * 977;
            int variant = 1;
            if (i % 11 == 0) variant = ((RuneGlyphs.FirstSilence + (i / 11) % 4 + 1) << 1) | 1;

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = RuneSize * px * scale, AgeRatio = 0.5f, Brightness = k * 0.70f,
                Seed = seed, Role = PrimitiveRole.Rune, Variant = variant,
                Rotation = MathF.Atan2(outward.X, -outward.Y), ColorOverride = color,
            });
        }
    }

    private void EmitFloatingRunes(EffectScene scene, in LayerCamera cam, float alpha, float age, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;

        // Runes peel off the seal and drift toward the lens, turning slowly and growing as they come.
        if (age >= _nextRune && _floatingCount < MaxFloating && !_leaving)
        {
            int s = _seed + _runeSpawns * 4099 + 5;
            _floating[_floatingCount++] = new Floating
            {
                Angle = DrawHelpers.Hash01(s + 1) * MathF.Tau,
                Radius = DrawHelpers.HashRange(s + 2, 0.40f, 0.70f),
                Height = DrawHelpers.HashRange(s + 3, -120f, 60f),
                Lift = DrawHelpers.HashRange(s + 4, 55f, 120f),
                Spin = DrawHelpers.HashRange(s + 5, -0.10f, 0.10f),
                Born = age,
                Life = DrawHelpers.HashRange(s + 6, 3.6f, 6.0f),
                Size = DrawHelpers.HashRange(s + 7, 0.8f, 1.4f),
                Tilt = DrawHelpers.HashRange(s + 8, -0.35f, 0.35f),
                Seed = s,
            };
            _runeSpawns++;
            _nextRune = age + DrawHelpers.HashRange(s + 9, 0.45f, 1.05f);
        }

        float shortSide = MathF.Min(_center.X, _center.Y) * 2f;
        int w = 0;
        for (int i = 0; i < _floatingCount; i++)
        {
            Floating f = _floating[i];
            float t = age - f.Born;
            float u = t / f.Life;
            if (u >= 1f) continue;
            _floating[w++] = f;

            float h = (f.Height + f.Lift * t) * px;
            float planeR = SigilGeometry.PlaneRadius(f.Radius * shortSide, 0f, dist) * (1f + 0.06f * u);
            float a = f.Angle + f.Spin * t;
            Vector2 at = SigilGeometry.PointOn(in cam, planeR, h, a, out float scale, out _);

            // In and out of the dark: they ignite, glow, and burn away as they near the lens.
            float fade = DrawHelpers.Smooth(u / 0.18f) * (1f - DrawHelpers.Smooth((u - 0.65f) / 0.35f));
            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = RuneSize * 1.15f * f.Size * px * scale, AgeRatio = 0.5f,
                Brightness = alpha * fade * 0.85f, Seed = f.Seed, Role = PrimitiveRole.Rune, Variant = 0,
                Rotation = f.Tilt + 0.20f * MathF.Sin(t * 0.9f + f.Seed), ColorOverride = color,
            });
        }
        _floatingCount = w;
    }

    // ---- miasma and motes ----

    private void EmitMiasma(EffectScene scene, float time, float dt, float alpha, float age, Vector4? color)
    {
        if (age > 0.9f && _hero.Count > 4 && !_leaving)
        {
            _miasma.Update(time, dt, spawnIntervalMin: 0.35f, spawnIntervalMax: 0.80f,
                           spawnPos: _miasmaPos, spawnVelocity: _miasmaVel,
                           lifespanMin: 4.0f, lifespanMax: 6.5f, sizeMin: 70f * _px, sizeMax: 150f * _px);
        }
        _miasma.Emit(scene, time, PrimitiveRole.Miasma, brightnessMul: alpha * DrawHelpers.Saturate((age - 0.8f) * 0.6f), color);
    }

    private void EmitMotes(EffectScene scene, in LayerCamera cam, Vector2 size, float alpha, float age, float leave, Vector4? color)
    {
        float px = _px, dist = CameraDistance * px;
        float ramp = DrawHelpers.Smooth((age - 0.35f) / 1.4f) * (1f - DrawHelpers.Smooth(leave));

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

            float u = fx * size.X * 0.5f / s, v = fy * halfH / s;
            Vector2 at = cam.Project(u, v, z, out float scale);
            if (at.X < -80f || at.Y < -80f || at.X > size.X + 80f || at.Y > size.Y + 80f) continue;

            float blur = DrawHelpers.Smooth(150f, MoteNear, m.Z);
            float far = DrawHelpers.Smooth(MoteFar, 0f, m.Z);
            float bright = alpha * ramp * (0.35f + 0.65f * far) * (1f - 0.45f * blur);

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = m.Size * px * scale * (1f + 3.4f * blur), AgeRatio = 0.5f,
                Brightness = bright, Seed = m.Seed, Role = PrimitiveRole.Mote, ColorOverride = color,
            });
        }
    }
}

using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Silence: a curse seal of rings, runes and triangles slams down and burns on; blocked chat makes it reject.</summary>
public sealed class SilenceEffect : SceneEffect, IHasSwappableSlots, IReactsToBlockedChat
{
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke,  "Binding ring",  "stroke.sigil", Hero: true),
        new("Stroke",   PrimitiveRole.Halo,        "Ring glow",     "stroke.glow"),
        new("Stroke",   PrimitiveRole.Inscription, "Inner sigil",   "stroke.sigil"),
        new("Particle", PrimitiveRole.Rune,        "Runes",         "particle.rune", Hero: true),
        new("Particle", PrimitiveRole.Mote,        "Motes",         "particle.mote"),
        new("Particle", PrimitiveRole.Miasma,      "Miasma",        "particle.miasma"),
        new("Region",   PrimitiveRole.MainStroke,  "Curse veil",    "region.edge-glow", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke,  "Seal flash",    "region.flat-fill", "FlatFill"),
    };

    // camera
    private const float CameraDistance = 1500f;
    private readonly SigilView _view = new(new SigilSway(BaseTilt: 0.15f, TiltSway: 0.035f, Precession: 0.21f, LurchTilt: 0.20f, LurchSeconds: 0.9f, BreathBase: 0.90f, BreathAmp: 0.10f), CameraDistance);

    // layout: radii are short-side fractions, heights px toward the viewer
    private const float FarHeight = -330f,  HeroHeight = -150f, SealHeight = 0f, NodeHeight = 240f;
    private const float TriHeightA = 95f,   TriHeightB = 180f;
    private const float FarOuter = 0.800f,  FarInner = 0.735f,  FarRunes = 0.7675f;
    private const float HeroRadius = 0.625f;
    private const float SealOuter = 0.515f, SealInner = 0.485f;
    private const float TriRadius = 0.455f;
    private const float NodeRing = 0.385f,  NodeRadius = 0.017f;
    private const int   NodeCount = 12, FarRuneCount = 44;

    // spin rad/s per layer
    private const float FarSpin = 0.040f, HeroSpin = -0.030f, SealSpin = 0.055f, TriSpinA = -0.085f, TriSpinB = 0.070f, NodeSpin = 0.110f;

    private const float HeroWobble = 0.010f;

    // line widths, px at 1080p
    private const float FarWidth = 10f, SealWidth = 12f, SealDashWidth = 9f, TriWidth = 11f, NodeWidth = 8f, NodeCircleWidth = 7f, ShockWidth = 15f;
    private const float SealDash = 30f;

    // cast
    private const float ArriveLift = 520f;
    private const float ArriveSeconds = 0.62f;
    private const float LockSeconds = 0.70f;
    private static readonly float[] ArriveAt = { 0.08f, 0.20f, 0.32f, 0.44f, 0.56f };  // far, hero, seal, triangles, nodes
    private const float SpinIn = 1.5f;
    private const float LeaveLift = 300f;

    // hush
    private const float FirstHush = 2.8f;
    private const float HushSecondsLong = 2.4f;
    private const float HushGain = 0.55f;

    // denied message
    private const float DenySeconds = 1.0f;
    private const float DenyShake = 14f, DenyShakeRate = 42f, DenyShakeDecay = 0.13f, DenyTilt = 0.08f;
    private const float DenyFlareDecay = 0.30f;
    private const float DenyGain = 0.9f;

    private const float RuneSize = 27f;
    private const int   MaxFloating = 14;

    private static readonly uint Flash = CurseColor.Flash;
    private static readonly uint Hush = DrawHelpers.Pack(0.060f, 0.012f, 0.100f);
    private static readonly uint Veil = DrawHelpers.Pack(0.58f, 0.13f, 0.82f);

    private const float NeverDenied = -1000f;

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

    private readonly Floating[] _floating = new Floating[MaxFloating];
    private int _floatingCount;
    private readonly MoteField _motes = new(64, 700f, -450f, (10f, 34f), (12f, 46f), (1.8f, 4.4f));
    private readonly ShockFront[] _fronts = new ShockFront[4];
    private int _frontCount;

    private readonly CastTracker _cast = new(envelope: true);
    private CastFrame _f;
    private int _seed, _runeSpawns;
    private float _hushEvery, _nextRune;
    private float _deniedAt = NeverDenied;
    private bool _stamped;

    private readonly ParticleEmitter _miasma = new(maxParticles: 12, seedSalt: 0x51A3C3);
    private readonly Func<int, Vector2> _miasmaPos, _miasmaVel;

    public SilenceEffect() : base(DebuffKind.Silence, "Silence",
        "A curse seal slams down around the screen, then burns on: rings turning at different depths, drifting runes, violet light.",
        6, "Silence",
        keywords: ["silence", "silenced", "mute", "muted", "gagged", "voiceless"])
    {
        for (int i = 0; i < NodeCount; i++) _nodes[i] = new StrandPath(NodeSegments + 1);

        // Wisps leave the binding ring and drift off it, inward or out.
        _miasmaPos = seed =>
        {
            int i = (int)((uint)seed % (uint)Math.Max(1, _hero.Count - 1));
            return _hero.Points[i] + new Vector2(DrawHelpers.HashRange(seed + 7, -30f, 30f), DrawHelpers.HashRange(seed + 8, -30f, 30f)) * _f.Px;
        };
        _miasmaVel = seed =>
        {
            int i = (int)((uint)seed % (uint)Math.Max(1, _hero.Count - 1));
            Vector2 from = _hero.Points[i] - _f.Center;
            Vector2 dir = from.LengthSquared() > 1f ? Vector2.Normalize(from) : new Vector2(0f, -1f);
            float side = DrawHelpers.Sign(seed + 3);
            return (dir * side * DrawHelpers.HashRange(seed + 4, 6f, 20f) + new Vector2(0f, DrawHelpers.HashRange(seed + 5, -14f, -3f))) * _f.Px;
        };
    }

    /// <summary>The chat blocker swallowed a message: the seal rejects it.</summary>
    public void OnChatBlocked(float time) => _deniedAt = time;

    public override void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        var f = _f = _cast.Frame(screenSize, alpha, time, dt, colorOverride);
        if (f.Fresh) BeginCast(f.Seed);

        // A swallowed message: damped-sine shake, decaying flare. Not while the curse is lifting.
        float sinceDenied = f.Time - _deniedAt;
        bool denied = !f.Leaving && sinceDenied >= 0f && sinceDenied < DenySeconds;
        float denyFlare = denied ? MathF.Exp(-sinceDenied / DenyFlareDecay) : 0f;
        float shake = denied ? MathF.Sin(sinceDenied * DenyShakeRate) * MathF.Exp(-sinceDenied / DenyShakeDecay) * DenyShake * f.Px : 0f;

        _view.Update(in f, extraTilt: DenyTilt * denyFlare, shift: new Vector2(shake, 0f));
        _frontCount = BuildFronts(_fronts, f.Age, denied ? sinceDenied : -1f, f.Short);

        EmitAtmosphere(scene, f.Gate, denyFlare);
        EmitRings(scene);
        EmitFarRunes(scene);
        EmitFloatingRunes(scene);
        EmitMiasma(scene, f.Time, f.Dt);
        _motes.Emit(scene, in _view.Cam, CameraDistance * f.Px, f.Size, f.Px, f.Age,
                    f.Alpha * DrawHelpers.Smooth((f.Age - 0.35f) / 1.4f) * (1f - DrawHelpers.Smooth(f.Leave)),
                    blurDim: 0.45f, twinkle: 0f, f.Color);
    }

    private void BeginCast(int seed)
    {
        _seed = seed;
        _deniedAt = NeverDenied;
        _view.Begin(seed);
        _hushEvery = DrawHelpers.HashRange(seed + 3, 9f, 12f);
        _nextRune = 1.1f;
        _runeSpawns = _floatingCount = 0;
        _stamped = false;
        _miasma.Clear();
        _motes.Seed(seed);
    }

    // The shocks: sound drawn in, the seal locking, a bounced message, and the occasional settled hush.
    private int BuildFronts(Span<ShockFront> into, float age, float sinceDenied, float shortSide)
    {
        int n = 0;

        if (age < 0.60f)
        {
            float p = age / 0.60f;
            into[n++] = new ShockFront(shortSide * (1.15f * (1f - p * p) + 0.04f), shortSide * 0.07f, 0.9f * MathF.Sin(MathF.PI * MathF.Min(1f, p * 1.15f)));
        }

        float tl = age - LockSeconds;
        if (tl > 0f && tl < 1.1f)
            into[n++] = new ShockFront(shortSide * (0.05f + 1.45f * tl), shortSide * 0.06f, 1.2f * MathF.Pow(1f - tl / 1.1f, 1.4f));

        if (sinceDenied >= 0f && n < into.Length)
        {
            float u = sinceDenied / DenySeconds;
            into[n++] = new ShockFront(shortSide * (0.16f + 0.95f * DrawHelpers.EaseOutCubic(u)), shortSide * 0.055f, DenyGain * (1f - DrawHelpers.Smooth(u)));
        }

        float th = age - FirstHush;
        if (th > 0f && n < into.Length)
        {
            float tp = th % _hushEvery;
            if (tp < HushSecondsLong)
            {
                float u = tp / HushSecondsLong;
                into[n++] = new ShockFront(shortSide * (0.20f + 0.90f * DrawHelpers.EaseOutCubic(u)), shortSide * 0.07f, HushGain * (1f - DrawHelpers.Smooth(u)));
            }
        }
        return n;
    }

    private float Excite(float screenRadius) => ShockFront.Excite(_fronts.AsSpan(0, _frontCount), screenRadius);

    private static float TurnIn(float landed) => SpinIn * (1f - landed) * (1f - landed);

    // How far a layer has landed (0..1), how far it still has to fall, and how solid it is.
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

    private void EmitAtmosphere(EffectScene scene, float castGate, float denyFlare)
    {
        ref readonly CastFrame f = ref _f;
        float castIn = DrawHelpers.Smooth(f.Age / 0.45f);

        scene.RequestVignette(CurseColor.Vignette, 0.17f + 0.04f * denyFlare, f.Alpha * 0.62f * castIn * _view.Breath, priority: 15, f.Color);
        scene.EdgeSpill(f.Size, f.Short * 0.13f, Veil, 0.20f * f.Alpha * castIn * _view.Breath * (1f + 1.2f * denyFlare), f.Color);

        // The cast's own flash and hush use the gate, so the manager's fade-in can't swallow them.
        float flash = f.Age < 0.45f ? MathF.Exp(-f.Age / 0.10f) : 0f;
        scene.Wash(f.Size, Flash, 0.42f * flash * castGate, f.Color);
        float hush = DrawHelpers.Smooth(f.Age / 0.25f) * (1f - DrawHelpers.Smooth((f.Age - 0.35f) / 1.3f));
        scene.Wash(f.Size, Hush, 0.34f * hush * castGate, f.Color);
    }

    private void Inscribe(EffectScene scene, StrandPath path, int salt, float width, float bright, float depth, float excite, float landed, float dash)
        => scene.Ring(path, PrimitiveRole.Inscription, _seed, salt, width, bright, depth, excite, DrawHelpers.Saturate(landed * 1.35f), dash, _f.Color);

    private void EmitRings(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        ref readonly LayerCamera cam = ref _view.Cam;
        float px = _f.Px, dist = CameraDistance * px, shortSide = f.Short, age = f.Age, alpha = f.Alpha, breath = _view.Breath, leave = f.Leave;

        // Far band: two thin rings; the runes sit between them (EmitFarRunes).
        {
            Arrive(age, Layer.Far, leave, out float lift, out float appear, out float landed);
            float h = FarHeight * px + lift * px;
            float ang = FarSpin * age + TurnIn(landed);
            float scale = cam.ScaleAt(h);
            float eo = FarOuter * shortSide, ei = FarInner * shortSide;
            float bright = alpha * appear * breath;

            SigilGeometry.Ring(_farOuter, RingSegments, in cam, SigilGeometry.PlaneRadius(eo, h, dist), h, ang, RingWobble.None);
            SigilGeometry.Ring(_farInner, RingSegments, in cam, SigilGeometry.PlaneRadius(ei, h, dist), h, ang, RingWobble.None);
            Inscribe(scene, _farOuter, 1, FarWidth * px * scale, bright * 0.62f * (1f + 0.5f * Excite(eo)), 0.65f, Excite(eo), landed, 0f);
            Inscribe(scene, _farInner, 2, FarWidth * 0.7f * px * scale, bright * 0.50f * (1f + 0.5f * Excite(ei)), 0.65f, Excite(ei), landed, 0f);
        }

        // Hero: the binding ring.
        {
            Arrive(age, Layer.Hero, leave, out float lift, out float appear, out float landed);
            float h = HeroHeight * px + lift * px;
            float ang = HeroSpin * age - TurnIn(landed);
            float eff = HeroRadius * shortSide;
            float excite = Excite(eff);
            float bright = alpha * appear * breath * (0.82f + 0.30f * excite);

            var wobble = new RingWobble(HeroWobble + 0.016f * excite, 3, _seed * 0.0007f - 0.35f * age,
                                        HeroWobble * 0.6f, 5, _seed * 0.0011f + 0.52f * age);
            SigilGeometry.Ring(_hero, HeroSegments, in cam, SigilGeometry.PlaneRadius(eff, h, dist), h, ang, in wobble);
            scene.HeroRing(_hero, _seed, DrawHelpers.Saturate(landed * 1.5f), bright, 1.05f, 0.30f, 0.30f,
                           DrawHelpers.Saturate(excite * 0.8f), landed < 0.66f ? 1f : 0f, f.Color);

            // The seal lands: light thrown off the ring all the way round.
            if (!_stamped && age >= LockSeconds)
            {
                _stamped = true;
                for (int k = 0; k < 9; k++)
                {
                    float a = ang + MathF.Tau * (k + DrawHelpers.Hash01(_seed + k * 3) * 0.6f) / 9f;
                    Vector2 at = SigilGeometry.PointOn(in cam, SigilGeometry.PlaneRadius(eff, h, dist), h, a, out _, out Vector2 outward);
                    if (at.X < 0f || at.Y < 0f || at.X > f.Size.X || at.Y > f.Size.Y) continue;

                    scene.AddImpact(new ImpactPrimitive
                    {
                        StrokeRole = PrimitiveRole.MainStroke, Position = at, Direction = outward, Strength = 0.6f,
                        Brightness = alpha, Seed = unchecked(_seed + 7919 * k), ColorOverride = f.Color,
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

            SigilGeometry.Ring(_sealOuter, RingSegments, in cam, SigilGeometry.PlaneRadius(eo, h, dist), h, SealSpin * age + TurnIn(landed), RingWobble.None);
            SigilGeometry.Ring(_sealInner, RingSegments, in cam, SigilGeometry.PlaneRadius(ei, h, dist), h, -SealSpin * 1.6f * age - TurnIn(landed), RingWobble.None);
            Inscribe(scene, _sealOuter, 3, SealWidth * px * scale, bright * 0.80f * (1f + 0.5f * Excite(eo)), 0.12f, Excite(eo), landed, 0f);
            Inscribe(scene, _sealInner, 4, SealDashWidth * px * scale, bright * 0.88f * (1f + 0.5f * Excite(ei)), 0.12f, Excite(ei), landed, SealDash * scale);
        }

        // Triangles: two at different depths turning against each other.
        {
            Arrive(age, Layer.Triangles, leave, out float lift, out float appear, out float landed);
            float er = TriRadius * shortSide;
            float excite = Excite(er * 0.8f);
            float bright = alpha * appear * breath * 0.80f * (1f + 0.5f * excite);
            float turnIn = TurnIn(landed);

            float hA = TriHeightA * px + lift * px, hB = TriHeightB * px + lift * px;
            SigilGeometry.Polygon(_triA, in cam, 3, SigilGeometry.PlaneRadius(er, hA, dist), hA, TriSpinA * age - turnIn + _seed * 0.0003f);
            SigilGeometry.Polygon(_triB, in cam, 3, SigilGeometry.PlaneRadius(er, hB, dist), hB, TriSpinB * age + turnIn + MathF.PI / 3f + _seed * 0.0003f);
            Inscribe(scene, _triA, 5, TriWidth * px * cam.ScaleAt(hA), bright, 0f, excite, landed, 0f);
            Inscribe(scene, _triB, 7, TriWidth * px * cam.ScaleAt(hB), bright * 0.92f, 0f, excite, landed, 0f);
        }

        // Nodes: a thin ring nearest the lens with a bead at every twelfth.
        {
            Arrive(age, Layer.Nodes, leave, out float lift, out float appear, out float landed);
            float h = NodeHeight * px + lift * px;
            float scale = cam.ScaleAt(h);
            float en = NodeRing * shortSide;
            float excite = Excite(en);
            float bright = alpha * appear * breath * 0.78f * (1f + 0.5f * excite);
            float planeR = SigilGeometry.PlaneRadius(en, h, dist);
            float ang = NodeSpin * age + TurnIn(landed);

            SigilGeometry.Ring(_nodeRing, RingSegments, in cam, planeR, h, ang, RingWobble.None);
            Inscribe(scene, _nodeRing, 6, NodeWidth * px * scale, bright, 0f, excite, landed, 0f);

            float beadR = SigilGeometry.PlaneRadius(NodeRadius * shortSide, h, dist);
            for (int i = 0; i < NodeCount; i++)
            {
                float a = ang + MathF.Tau * i / NodeCount;
                SigilGeometry.Ring(_nodes[i], NodeSegments, in cam, beadR, h, 0f, RingWobble.None, planeR * MathF.Cos(a), planeR * MathF.Sin(a));
                Inscribe(scene, _nodes[i], 10 + i, NodeCircleWidth * px * scale, bright * 0.95f, 0f, excite, 1f, 0f);
            }
        }

        // The shock itself, drawn as a line of light riding the strongest front.
        int strongest = -1; float best = 0.05f;
        for (int i = 0; i < _frontCount; i++)
            if (_fronts[i].Gain > best) { best = _fronts[i].Gain; strongest = i; }
        if (strongest >= 0)
        {
            var r = _fronts[strongest];
            SigilGeometry.Ring(_shock, ShockSegments, in cam, MathF.Max(8f * px, r.Front), 0f, 0f, RingWobble.None);

            // Dark at screen centre (a bright ring there reads as a crosshair), brighter as it reaches the rings.
            float reachIn = DrawHelpers.Smooth((r.Front - 0.08f * shortSide) / (0.22f * shortSide));
            Inscribe(scene, _shock, 30, ShockWidth * px, alpha * MathF.Min(1.4f, r.Gain * 1.2f) * reachIn, 0f, r.Gain, 1f, 0f);
        }
    }

    private void EmitFarRunes(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        Arrive(f.Age, Layer.Far, f.Leave, out float lift, out float appear, out float landed);
        float px = _f.Px, dist = CameraDistance * px;
        float h = FarHeight * px + lift * px;
        float ang = FarSpin * f.Age + TurnIn(landed);
        float eff = FarRunes * f.Short;
        float planeR = SigilGeometry.PlaneRadius(eff, h, dist);
        float excite = Excite(eff);
        float scan = f.Age * 0.62f + _seed * 0.001f;  // a beam sweeps the band so the runes read in turn

        for (int i = 0; i < FarRuneCount; i++)
        {
            float u = i / (float)FarRuneCount;
            float a = ang + MathF.Tau * u;

            // They light in order round the ring as the seal lands.
            float ignite = DrawHelpers.Saturate((f.Age - 0.70f - 0.9f * u) / 0.22f);
            float flare = ignite > 0f && ignite < 1f ? 1f + 1.4f * MathF.Sin(MathF.PI * ignite) : 1f;

            float d = MathF.Abs(MathF.IEEERemainder(a - scan, MathF.Tau)) / 0.30f;
            float sweep = MathF.Exp(-d * d);

            float k = f.Alpha * appear * ignite * (0.70f + 0.95f * sweep + 0.5f * excite) * flare * (0.80f + 0.20f * _view.Breath);
            if (k <= 0.01f) continue;

            Vector2 at = SigilGeometry.PointOn(in _view.Cam, planeR, h, a, out float scale, out Vector2 outward);
            if (at.X < -60f || at.Y < -60f || at.X > f.Size.X + 60f || at.Y > f.Size.Y + 60f) continue;

            // Every 11th rune is a sealed-mouth mark.
            int variant = 1;
            if (i % 11 == 0) variant = ((RuneGlyphs.FirstSilence + (i / 11) % 4 + 1) << 1) | 1;

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = RuneSize * px * scale, AgeRatio = 0.5f, Brightness = k * 0.70f,
                Seed = _seed + i * 977, Role = PrimitiveRole.Rune, Variant = variant,
                Rotation = MathF.Atan2(outward.X, -outward.Y), ColorOverride = f.Color,
            });
        }
    }

    // Runes peel off the seal and drift toward the lens, turning and growing as they come.
    private void EmitFloatingRunes(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        float px = _f.Px, dist = CameraDistance * px, age = f.Age;

        if (age >= _nextRune && _floatingCount < MaxFloating && !f.Leaving)
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

        int w = 0;
        for (int i = 0; i < _floatingCount; i++)
        {
            Floating fl = _floating[i];
            float t = age - fl.Born;
            float u = t / fl.Life;
            if (u >= 1f) continue;
            _floating[w++] = fl;

            float h = (fl.Height + fl.Lift * t) * px;
            float planeR = SigilGeometry.PlaneRadius(fl.Radius * f.Short, 0f, dist) * (1f + 0.06f * u);
            Vector2 at = SigilGeometry.PointOn(in _view.Cam, planeR, h, fl.Angle + fl.Spin * t, out float scale, out _);

            float fade = DrawHelpers.Smooth(u / 0.18f) * (1f - DrawHelpers.Smooth((u - 0.65f) / 0.35f));
            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = RuneSize * 1.15f * fl.Size * px * scale, AgeRatio = 0.5f,
                Brightness = f.Alpha * fade * 0.85f, Seed = fl.Seed, Role = PrimitiveRole.Rune, Variant = 0,
                Rotation = fl.Tilt + 0.20f * MathF.Sin(t * 0.9f + fl.Seed), ColorOverride = f.Color,
            });
        }
        _floatingCount = w;
    }

    private void EmitMiasma(EffectScene scene, float time, float dt)
    {
        ref readonly CastFrame f = ref _f;
        if (f.Age > 0.9f && _hero.Count > 4 && !f.Leaving)
        {
            _miasma.Update(time, dt, spawnIntervalMin: 0.35f, spawnIntervalMax: 0.80f,
                           spawnPos: _miasmaPos, spawnVelocity: _miasmaVel,
                           lifespanMin: 4.0f, lifespanMax: 6.5f, sizeMin: 70f * _f.Px, sizeMax: 150f * _f.Px);
        }
        _miasma.Emit(scene, time, PrimitiveRole.Miasma, brightnessMul: f.Alpha * DrawHelpers.Saturate((f.Age - 0.8f) * 0.6f), f.Color);
    }
}

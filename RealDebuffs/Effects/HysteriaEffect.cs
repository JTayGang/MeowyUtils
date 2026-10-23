using System.Numerics;
using RealDebuffs.Effects.Framework;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects;

/// <summary>
/// Hysteria: a fall down a haunted tunnel. Pale streamers spiral past, broken rings with script on them rush at you, and eyes of every
/// kind drift out of the dark, turn to watch you go by and are gone. Now and then the fall stops and the dark looks back: a crowd of
/// eyes opens, one enormous eye, a gate that slams through you, a surge that tears the tunnel past.
/// </summary>
public sealed class HysteriaEffect : ISceneEffect, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Hysteria;
    public string DisplayName => "Hysteria";
    public string Description => "The lights go out and you fall. Pale streamers spiral past, eyes drift out of the dark and turn to watch you go by, and now and then the fall stops and the dark looks back.";
    public int DrawOrder => 9;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Hysteria"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "hysteria", "hysterical", "haunted", "possessed", "insane", "madness", "hallucination", "hallucinations", "delirium", "paranoia", "terror"
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke,  "Streamers",     "stroke.wisp", Hero: true),
        new("Stroke",   PrimitiveRole.Trail,       "Faint trails",  "stroke.wisp"),
        new("Stroke",   PrimitiveRole.Inscription, "Rings",         "stroke.haunt"),
        new("Stroke",   PrimitiveRole.Halo,        "Ghosting",      "stroke.hauntglow"),
        new("Particle", PrimitiveRole.Eye,         "Watchers",      "particle.eye", Hero: true),
        new("Particle", PrimitiveRole.Rune,        "Script",        "particle.omen"),
        new("Particle", PrimitiveRole.Mote,        "Specks",        "particle.spirit"),
        new("Particle", PrimitiveRole.Miasma,      "Gloom",         "particle.gloom"),
        new("Region",   PrimitiveRole.MainStroke,  "Cold veil",     "region.edge-glow", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke,  "Blackout",      "region.flat-fill", "FlatFill"),
    };

    // The tunnel. Heights are px toward the viewer at 1080p (LayerCamera's, so the perspective is its own); things come in at the far end,
    // swell and sweep outward as they near, and leave past the lens. Radii are the lane a thing flies in, measured at the focal plane.
    private const float CameraDistance = 1500f;
    private const float FarHeight = -3600f, NearHeight = 1050f, CruiseSpeed = 650f;
    private static readonly SigilSway Sway = new(BaseTilt: 0.08f, TiltSway: 0.05f, Precession: 0.10f, LurchTilt: 0.20f, LurchSeconds: 0.7f, BreathBase: 0.92f, BreathAmp: 0.08f);

    // What flies down it
    private const int HeroStreamers = 10, FineStreamers = 22, StreamerCount = HeroStreamers + FineStreamers, StreamerPts = 16;
    private const int RingCount = 4, RingSegments = 96, ScriptRunes = 22, GateRings = 3, GateSegments = 128;
    private const int EyeLanes = 22, OrbLanes = 6, ClusterLanes = 3, ClusterEyes = 5, SpeckCount = 64, GloomCount = 7;

    // The cast: a flash, the dark, the tunnel rushing at you at 18 fps like a bad signal and slowing to a fall, the eyes waking, one blink together
    private const float StopMotionUntil = 1.6f, StopMotionRate = 18f;
    private const float BlinkAt = 1.50f, IntroEyes = 1.9f, ShockSeconds = 0.9f;

    // Hallucinations
    private const float FirstVisionMin = 3.6f, FirstVisionMax = 5.0f, VisionGapMin = 3.2f, VisionGapMax = 7.0f;
    private const float StareClear = 0.34f;     // short-side fraction round the middle of the screen that a crowd of eyes keeps out of
    private const int MaxStare = 12;

    private static readonly uint Flash = HauntColor.Flash;
    private static readonly uint Ink = HauntColor.Vignette;
    private static readonly uint Drain = DrawHelpers.Pack(0.25f, 0.33f, 0.35f);
    private static readonly uint Veil = DrawHelpers.Pack(0.42f, 0.60f, 0.58f);

    // The two halves of the double vision.
    private static readonly Vector4 RedSplit = new(1.00f, 0.16f, 0.20f, 1f);
    private static readonly Vector4 CyanSplit = new(0.12f, 0.86f, 1.00f, 1f);

    private enum VisionKind { None, Stare, GreatEye, Gate, Surge }

    /// <summary>The apparition in progress: what it is, when it began, how long it lasts, and where its one big thing is (screen fractions).</summary>
    private struct Vision
    {
        public VisionKind Kind;
        public float Start, Life, Size, Roll;
        public int Seed;
        public Vector2 At;
        public EyeStyle Style;
    }

    /// <summary>One eye of a crowd, placed in screen fractions so a resize doesn't move it.</summary>
    private struct Sprite
    {
        public Vector2 At;
        public float Size, Roll, Delay;
        public int Seed;
        public EyeStyle Style;
    }

    /// <summary>Something drawn this frame at some height down the tunnel: collected, sorted far to near, then drawn.</summary>
    private struct Thing
    {
        public float Height;
        public ParticlePrimitive Particle;
    }

    private static readonly Comparison<Thing> FarToNear = (a, b) => a.Height.CompareTo(b.Height);

    private readonly TunnelFlight _tunnel = new(FarHeight, NearHeight);
    private readonly StrandPath[] _streamer = new StrandPath[StreamerCount];
    private readonly StrandPath[] _ring = new StrandPath[RingCount], _ringA = new StrandPath[RingCount], _ringB = new StrandPath[RingCount];
    private readonly StrandPath[] _gate = new StrandPath[GateRings];
    private readonly StrandPath _gateA = new(GateSegments + 1), _gateB = new(GateSegments + 1);
    private readonly StrandPath _shock = new(ShockSegments + 1);
    private const int ShockSegments = 96;

    private readonly float[] _streamerAt = new float[StreamerCount], _ringAt = new float[RingCount];
    private readonly float[] _eyeAt = new float[EyeLanes], _orbAt = new float[OrbLanes], _clusterAt = new float[ClusterLanes];
    private readonly float[] _specAt = new float[SpeckCount], _gloomAt = new float[GloomCount];

    private readonly Sprite[] _stare = new Sprite[MaxStare];
    private int _stareCount;
    private readonly List<Thing> _things = new(256);

    private Vision _v;
    private VisionKind _lastKind;
    private FlashSchedule _flashes;
    private readonly Heartbeat _heart = new();

    private readonly CastTracker _cast = new(envelope: true);
    private CastFrame _f;
    private LayerCamera _cam;
    private float _breath, _roll, _tqPrev, _appear;

    // per cast
    private int _seed;
    private float _hand, _swirl, _baseAngle, _spin, _cruise, _azimuth, _azimuthDir;

    // per frame, from the apparition
    private float _level, _env, _kick, _ghost, _stress, _staring, _vEnded, _resumeAt, _kickTilt, _jump;

    public HysteriaEffect()
    {
        for (int i = 0; i < StreamerCount; i++) _streamer[i] = new StrandPath(StreamerPts);
        for (int i = 0; i < RingCount; i++)
        {
            _ring[i] = new StrandPath(RingSegments + 1);
            _ringA[i] = new StrandPath(RingSegments + 1);
            _ringB[i] = new StrandPath(RingSegments + 1);
        }
        for (int i = 0; i < GateRings; i++)
        {
            _gate[i] = new StrandPath(GateSegments + 1);
        }
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        var f = _f = _cast.Frame(screenSize, alpha, time, dt, colorOverride);
        if (f.Fresh) BeginCast(f.Seed);

        UpdateVision(in f);
        float beat = _heart.Level(f.Age, _stress);

        // The intro plays at 18 frames a second, like a bad signal, then lets go.
        float tq = f.Age < StopMotionUntil ? MathF.Floor(f.Age * StopMotionRate) / StopMotionRate : f.Age;
        float step = f.Age < StopMotionUntil ? tq - _tqPrev : f.Dt;
        _tqPrev = tq;

        // After the flash and the dark, the tunnel materializes out of nothing.
        _appear = DrawHelpers.Smooth((tq - 0.15f) / 0.45f);
        float speed = SpeedNow(in f);
        _tunnel.Advance(MathF.Max(0f, step), CruiseSpeed * _cruise * speed);
        _roll = _baseAngle + _spin * f.Age + _jump * _kick;
        float lenMul = Math.Clamp(0.55f + 0.45f * MathF.Pow(MathF.Max(speed, 0.01f), 0.6f), 0.5f, 3f);

        // Whatever is shown jolts the whole view: a lurch of tilt, a shake, and a tremor that never quite stops.
        float tremor = (0.3f + _stress) * f.Px;
        Vector2 shake = new Vector2(Flicker.Tic(time, _seed + 31, 33f), Flicker.Tic(time, _seed + 32, 33f)) * 12f * f.Px * _kick
                      + new Vector2(Flicker.Tic(time, _seed + 33), Flicker.Tic(time, _seed + 34)) * 1.4f * tremor;
        float tilt = Sway.Tilt(f.Age, f.Leave) + _kickTilt * _kick + 0.008f * beat + 0.004f * Flicker.Tic(time, _seed + 35);
        _cam = new LayerCamera(f.Center + shake, CameraDistance * f.Px, tilt, Sway.Azimuth(_azimuth, _azimuthDir, f.Age));
        _breath = Sway.Breath(f.Age);

        EmitAtmosphere(scene, beat);
        EmitStreamers(scene, tq, beat, lenMul);
        EmitRings(scene, tq);
        EmitShock(scene);
        EmitGate(scene);

        _things.Clear();
        CollectGloom();
        CollectSpecks();
        CollectEyes(tq);
        _things.Sort(FarToNear);
        foreach (var t in _things) scene.AddParticle(t.Particle);

        EmitVisionEyes(scene);
    }

    private void BeginCast(int seed)
    {
        _seed = seed;
        _azimuth = DrawHelpers.Hash01(seed + 1) * MathF.Tau;
        _azimuthDir = DrawHelpers.Hash01(seed + 2) < 0.5f ? -1f : 1f;
        _hand = DrawHelpers.Hash01(seed + 5) < 0.5f ? -1f : 1f;
        _swirl = _hand * DrawHelpers.HashRange(seed + 7, 0.00030f, 0.00060f);
        _baseAngle = DrawHelpers.Hash01(seed + 8) * MathF.Tau;
        _spin = DrawHelpers.HashRange(seed + 9, 0.03f, 0.08f) * (DrawHelpers.Hash01(seed + 10) < 0.5f ? -1f : 1f);
        _cruise = DrawHelpers.HashRange(seed + 11, 0.85f, 1.15f);

        // Where each thing starts along the tunnel, evenly spread so it is never empty and never bunched; laps then re-roll their lanes.
        Spread(_streamerAt, seed + 100); Spread(_ringAt, seed + 200); Spread(_eyeAt, seed + 300);
        Spread(_orbAt, seed + 400); Spread(_clusterAt, seed + 500); Spread(_specAt, seed + 600); Spread(_gloomAt, seed + 700);
        _tunnel.Travel = DrawHelpers.Hash01(seed + 12) * _tunnel.Length;

        _flashes.Begin(seed, DrawHelpers.HashRange(seed + 6, FirstVisionMin, FirstVisionMax));
        _heart.Begin(seed, 0.32f);
        _v = default;
        _lastKind = VisionKind.None;
        _vEnded = _resumeAt = -100f;
        _tqPrev = 0f;
        _stress = 0.12f;
    }

    private void Spread(float[] offsets, int seed)
    {
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = _tunnel.Length * (i + DrawHelpers.Hash01(seed + i * 17)) / offsets.Length;
    }

    /// <summary>The fall, in multiples of cruising speed: the cast rushes in and slows; an apparition can stop it dead or tear it forward.</summary>
    private float SpeedNow(in CastFrame f)
    {
        float intro = 1f + 9f * MathF.Exp(-f.Age / 0.55f);
        float v = _v.Kind switch
        {
            VisionKind.Stare or VisionKind.GreatEye => 1f - 0.97f * _env,
            VisionKind.Gate => 1f - 0.80f * _env,
            VisionKind.Surge => 1f + 8f * _env,
            _ => 1f,
        };
        return intro * (v + 3f * MathF.Exp(-(f.Age - _resumeAt) / 0.45f));
    }

    // The apparitions

    private void UpdateVision(in CastFrame f)
    {
        if (_v.Kind != VisionKind.None && f.Age >= _v.Start + _v.Life)
        {
            _flashes.Finish(f.Age, VisionGapMin, VisionGapMax);
            _lastKind = _v.Kind;
            _vEnded = f.Age;
            if (_v.Kind is VisionKind.Stare or VisionKind.GreatEye) _resumeAt = f.Age;   // the fall lets go with a lurch
            _v.Kind = VisionKind.None;
        }
        if (_v.Kind == VisionKind.None && !f.Leaving && _flashes.TryStart(f.Age, out int seed))
            BeginVision(in f, seed);

        bool on = _v.Kind != VisionKind.None;
        float t = f.Age - _v.Start;
        _level = on ? Flicker.Level(t, _v.Life, _v.Seed) : 0f;
        _env = on ? DrawHelpers.Smooth(t / 0.07f) * (1f - DrawHelpers.Smooth((t - (_v.Life - 0.18f)) / 0.18f)) : 0f;
        _kick = on ? MathF.Exp(-t / 0.20f) : 0f;

        // The gate hits you as it passes through, late in its life.
        if (_v.Kind == VisionKind.Gate && t > 0.78f * _v.Life) _kick = MathF.Max(_kick, MathF.Exp(-(t - 0.78f * _v.Life) / 0.18f));

        // Every eye in the tunnel opens and fixes on you for anything that has eyes in it.
        _staring = _v.Kind is VisionKind.Stare or VisionKind.GreatEye or VisionKind.Surge ? _env : 0f;
        _ghost = _v.Kind switch
        {
            VisionKind.Surge => _env,
            VisionKind.Gate => 0.6f * _level,
            VisionKind.None => 0f,
            _ => 0.22f * _level,
        };

        // Fright lingers: it is full while something is shown and drains over a few seconds, quickening the pulse and the tremor.
        float target = on ? 1f : MathF.Max(0.12f, MathF.Exp(-(f.Age - _vEnded) / 3.5f));
        _stress += (target - _stress) * (1f - MathF.Exp(-f.Dt * 3f));
    }

    private void BeginVision(in CastFrame f, int seed)
    {
        // Never the same kind twice running.
        float roll = DrawHelpers.Hash01(seed + 1);
        VisionKind kind = roll < 0.30f ? VisionKind.Stare : roll < 0.48f ? VisionKind.GreatEye : roll < 0.74f ? VisionKind.Gate : VisionKind.Surge;
        if (kind == _lastKind) kind = (VisionKind)((int)kind % 4 + 1);

        _v = new Vision { Kind = kind, Start = f.Age, Seed = seed };
        float sign = DrawHelpers.Hash01(seed + 3) < 0.5f ? -1f : 1f;
        _kickTilt = sign * DrawHelpers.HashRange(seed + 4, 0.07f, 0.14f);
        _jump = -sign * DrawHelpers.HashRange(seed + 5, 0.15f, 0.35f);

        switch (kind)
        {
            case VisionKind.Stare:
                _v.Life = DrawHelpers.HashRange(seed + 6, 1.1f, 1.7f);
                PlaceStare(in f, seed);
                break;
            case VisionKind.GreatEye:
                _v.Life = DrawHelpers.HashRange(seed + 6, 0.80f, 1.20f);
                _v.At = new Vector2(DrawHelpers.HashRange(seed + 7, 0.36f, 0.64f), DrawHelpers.HashRange(seed + 8, 0.16f, 0.34f));
                _v.Size = DrawHelpers.HashRange(seed + 9, 190f, 250f);
                _v.Roll = DrawHelpers.HashRange(seed + 10, -0.12f, 0.12f);
                _v.Style = PickBigStyle(seed + 11);
                break;
            case VisionKind.Gate:
                _v.Life = DrawHelpers.HashRange(seed + 6, 0.70f, 0.95f);
                break;
            default:
                _v.Life = DrawHelpers.HashRange(seed + 6, 0.50f, 0.85f);
                break;
        }
    }

    /// <summary>Eyes of every kind in the dark round the edges, in pairs and alone, turned to face the middle, none near it and none on top of another.</summary>
    private void PlaceStare(in CastFrame f, int seed)
    {
        int pairs = 3 + (int)(DrawHelpers.Hash01(seed + 20) * 4f);
        _stareCount = 0;
        Span<Vector2> sites = stackalloc Vector2[6];
        int n = 0;

        for (int attempt = 0; attempt < 90 && n < pairs; attempt++)
        {
            var c = new Vector2(DrawHelpers.HashRange(seed + 100 + attempt * 3, 0.06f, 0.94f), DrawHelpers.HashRange(seed + 101 + attempt * 3, 0.08f, 0.92f));
            Vector2 off = (c - new Vector2(0.5f)) * f.Size;
            float d = off.Length() / f.Short;
            bool crowded = false;
            for (int i = 0; i < n; i++) if (Vector2.Distance(sites[i] * f.Size, c * f.Size) < 0.18f * f.Short) crowded = true;
            if (d < StareClear || crowded) continue;
            sites[n] = c;

            // A pair sits side by side across the line to the middle: the axis is square to it. Farther out, a little bigger.
            float axis = MathF.Atan2(off.Y, off.X) + MathF.PI * 0.5f;
            float roll = MathF.Atan2(MathF.Sin(axis), MathF.Cos(axis));
            if (roll > MathF.PI * 0.5f) roll -= MathF.PI; else if (roll < -MathF.PI * 0.5f) roll += MathF.PI;
            roll = Math.Clamp(roll, -0.8f, 0.8f);

            float size = DrawHelpers.HashRange(seed + 200 + n, 22f, 48f) * (0.8f + 0.5f * MathF.Min(d, 1f));
            float delay = DrawHelpers.HashRange(seed + 300 + n, 0.02f, 0.55f);
            bool single = DrawHelpers.Hash01(seed + 400 + n) < 0.12f;
            EyeStyle style = PickStyle(seed + 450 + n);
            Vector2 half = new Vector2(MathF.Cos(axis), MathF.Sin(axis)) * size * 1.3f * f.Px / f.Size;

            _stare[_stareCount++] = new Sprite { At = c - (single ? Vector2.Zero : half), Size = size, Roll = roll, Delay = delay, Seed = seed + 500 + n * 2, Style = style };
            if (!single) _stare[_stareCount++] = new Sprite { At = c + half, Size = size, Roll = roll, Delay = delay + 0.03f, Seed = seed + 501 + n * 2, Style = style };
            n++;
        }
    }

    // What sort of eye

    private static EyeStyle PickStyle(int seed)
    {
        float r = DrawHelpers.Hash01(seed);
        return r < 0.24f ? EyeStyle.Watcher : r < 0.36f ? EyeStyle.Bloodshot : r < 0.45f ? EyeStyle.Void : r < 0.53f ? EyeStyle.Goat
             : r < 0.59f ? EyeStyle.Spiral : r < 0.66f ? EyeStyle.Triple : r < 0.74f ? EyeStyle.Weeping : r < 0.84f ? EyeStyle.Hollow : EyeStyle.Feral;
    }

    /// <summary>For the one enormous eye: the kinds that have something to see up close.</summary>
    private static EyeStyle PickBigStyle(int seed)
    {
        EyeStyle[] styles = { EyeStyle.Watcher, EyeStyle.Bloodshot, EyeStyle.Void, EyeStyle.Goat, EyeStyle.Spiral, EyeStyle.Weeping, EyeStyle.Hollow, EyeStyle.Triple };
        return styles[(int)(DrawHelpers.Hash01(seed) * styles.Length) % styles.Length];
    }

    // The tunnel

    /// <summary>Where a thing in a lane appears at height h (px, scaled), and how much it is magnified. The whole tunnel swirls as it recedes.</summary>
    private Vector2 Place(float radial, float theta, float h, out float scale, float tangential = 0f)
    {
        float a = theta + _swirl * h / _f.Px + _roll;
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return _cam.Project(radial * c - tangential * s, radial * s + tangential * c, h, out scale);
    }

    /// <summary>Fades a thing in out of the far dark and out again before it passes the lens.</summary>
    private static float DepthFade(float near, float inEnd = 0.18f, float outStart = 0.84f)
        => DrawHelpers.Smooth(near / inEnd) * (1f - DrawHelpers.Smooth((near - outStart) / (1f - outStart)));

    /// <summary>Keeps the middle of the screen, where you are playing, clear.</summary>
    private float CenterFade(Vector2 at, float inner = 0.20f, float span = 0.16f)
        => DrawHelpers.Smooth((Vector2.Distance(at, _f.Center) - inner * _f.Short) / (span * _f.Short));

    /// <summary>Distance is dimmer: the far end of the tunnel is nearly dark.</summary>
    private static float Fog(float near) => 0.35f + 0.65f * DrawHelpers.Smooth(near / 0.55f);

    /// <summary>
    /// Long soft streamers spiralling past: the ten brightest are the hero stroke, the rest a fainter lot. Each flies in a lane of its
    /// own, trailing back toward the far end of the tunnel; the cast stretches them, a stop shortens them.
    /// </summary>
    private void EmitStreamers(EffectScene scene, float tq, float beat, float lenMul)
    {
        ref readonly CastFrame f = ref _f;
        float px = f.Px;
        float agitation = DrawHelpers.Saturate(0.9f * _kick + 0.35f * _env + 0.25f * beat);

        for (int i = 0; i < StreamerCount; i++)
        {
            bool hero = i < HeroStreamers;
            float hu = _tunnel.HeightOf(_streamerAt[i], out int lap);
            float near = _tunnel.Near(hu);
            int s = _seed + 1000 + i * 7919 + lap * 104729;

            float fade = DepthFade(near, 0.14f, 0.80f);
            float appear = DrawHelpers.Smooth((tq - 0.04f - 0.30f * DrawHelpers.Hash01(_seed + i * 13)) / 0.30f);
            float gain = DrawHelpers.HashRange(s + 6, 0.45f, 0.85f) * (hero ? 1f : 0.55f);
            float k = f.Alpha * fade * appear * gain * _breath * (1f + 0.25f * beat) * Flicker.Gutter(f.Time, s, 0.01f + 0.05f * _stress);
            if (k < 0.01f) continue;

            float radial = DrawHelpers.HashRange(s + 1, 240f, 780f) * px;
            float theta = DrawHelpers.Hash01(s + 2) * MathF.Tau;
            float curl = _hand * DrawHelpers.HashRange(s + 3, 0.0003f, 0.0008f);
            float length = DrawHelpers.HashRange(s + 4, hero ? 1100f : 600f, hero ? 2100f : 1200f) * lenMul;
            float tailU = MathF.Max(FarHeight, hu - length);

            var path = _streamer[i];
            Vector2 head = default;
            float headScale = 1f;
            for (int p = 0; p < StreamerPts; p++)
            {
                float hk = DrawHelpers.Lerp(tailU, hu, p / (StreamerPts - 1f)) * px;
                path.Points[p] = Place(radial, theta + curl * hk / px, hk, out float sc);
                if (p == StreamerPts - 1) { head = path.Points[p]; headScale = sc; }
            }
            path.Count = StreamerPts;
            path.BuildArc();

            k *= CenterFade(head, 0.14f, 0.16f);
            if (k < 0.01f) continue;

            scene.AddStroke(new StrokePrimitive
            {
                Path = path, Role = hero ? PrimitiveRole.MainStroke : PrimitiveRole.Trail, Reveal = 1f,
                WidthHint = DrawHelpers.HashRange(s + 5, hero ? 12f : 6f, hero ? 26f : 13f) * px * headScale,
                Brightness = k, Seed = s, Phase = i * 1.3f, Depth = (1f - near) * 0.6f, Agitation = agitation, ColorOverride = f.Color,
            });
        }
    }

    /// <summary>
    /// Broken rings rushing at you, which is what gives the tunnel its walls. Every other lap, the nearest carries a band of script.
    /// </summary>
    private void EmitRings(EffectScene scene, float tq)
    {
        ref readonly CastFrame f = ref _f;
        float px = f.Px;

        for (int r = 0; r < RingCount; r++)
        {
            float hu = _tunnel.HeightOf(_ringAt[r], out int lap);
            float near = _tunnel.Near(hu);
            int s = _seed + 2000 + r * 7919 + lap * 104729;
            float appear = DrawHelpers.Smooth((tq - 0.25f - 0.12f * r) / 0.45f);
            float fade = DepthFade(near, 0.18f, 0.80f) * appear;
            if (fade < 0.02f) continue;

            float h = hu * px;
            float scale = _cam.ScaleAt(h);
            float radius = DrawHelpers.HashRange(s + 1, 0.60f, 0.82f) * f.Short;
            var wobble = new RingWobble(DrawHelpers.HashRange(s + 2, 0.008f, 0.022f), 2 + (s & 1), DrawHelpers.Hash01(s + 3) * MathF.Tau,
                                        DrawHelpers.HashRange(s + 4, 0.002f, 0.005f), 5 + (s & 3), DrawHelpers.Hash01(s + 5) * MathF.Tau);
            float angle = DrawHelpers.Hash01(s + 6) * MathF.Tau + DrawHelpers.HashRange(s + 7, -0.5f, 0.5f) * f.Age + _roll;
            SigilGeometry.Ring(_ring[r], RingSegments, in _cam, radius, h, angle, in wobble);

            bool scripted = r == 0 && (lap & 1) == 0;
            float dash = !scripted && DrawHelpers.Hash01(s + 8) < 0.5f ? DrawHelpers.HashRange(s + 9, 50f, 140f) : 0f;
            float k = f.Alpha * fade * 0.55f * _breath * Flicker.Gutter(f.Time, s, 0.008f + 0.04f * _stress);
            scene.Ring(_ring[r], PrimitiveRole.Inscription, _seed, 40 + r, Math.Clamp(9f * px * scale, 6f * px, 30f * px), k, (1f - near) * 0.6f, 0.2f * _stress, 1f, dash, f.Color);

            if (_ghost > 0.02f) EmitGhostRing(scene, r, Math.Clamp(9f * px * scale, 6f * px, 30f * px), k, 90 + r * 2);
            if (scripted) EmitScript(scene, radius, h, angle, 17f * px * scale, f.Alpha * fade * 0.75f * _breath, ScriptRunes, s);
        }
    }

    /// <summary>Double vision: two copies of a ring slide apart, one red and one cyan, as the two eyes stop agreeing.</summary>
    private void EmitGhostRing(EffectScene scene, int ring, float width, float brightness, int salt)
    {
        ref readonly CastFrame f = ref _f;
        float apart = (6f + 30f * _ghost) * f.Px;
        Offset(_ringA[ring], _ring[ring], new Vector2(-apart, 0.22f * apart));
        Offset(_ringB[ring], _ring[ring], new Vector2(apart, -0.22f * apart));
        for (int side = 0; side < 2; side++)
            scene.Ring(side == 0 ? _ringA[ring] : _ringB[ring], PrimitiveRole.Halo, _seed, salt + side, width * 1.6f, brightness * 2.2f * _ghost, 0f, 0f, 1f, 0f,
                       f.Color ?? (side == 0 ? RedSplit : CyanSplit));
    }

    /// <summary>A band of runes round a ring, upright to the outside, turning with it: glyphs that foreshorten and swell with the fall.</summary>
    private void EmitScript(EffectScene scene, float radius, float h, float angle, float size, float brightness, int count, int seed)
    {
        ref readonly CastFrame f = ref _f;
        Vector2 centre = _cam.Project(0f, 0f, h, out _);
        float band = radius * 0.93f;
        for (int g = 0; g < count; g++)
        {
            float a = angle + MathF.Tau * g / count;
            Vector2 at = _cam.Project(band * MathF.Cos(a), band * MathF.Sin(a), h, out _);
            Vector2 outward = at - centre;
            if (outward.LengthSquared() < 1f) continue;
            outward = Vector2.Normalize(outward);

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = size, AgeRatio = 0.5f, Brightness = brightness, Seed = seed + g * 31, Role = PrimitiveRole.Rune,
                Rotation = MathF.Atan2(outward.X, -outward.Y), ColorOverride = f.Color,
            });
        }
    }

    /// <summary>The cast lands as a ring of light racing out from the middle: the first ring of the tunnel.</summary>
    private void EmitShock(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        if (f.Age >= ShockSeconds) return;

        float u = f.Age / ShockSeconds;
        float gain = MathF.Pow(1f - u, 1.4f);
        float r = f.Short * (0.04f + 1.25f * DrawHelpers.EaseOutCubic(u));
        SigilGeometry.Ring(_shock, ShockSegments, in _cam, MathF.Max(8f * f.Px, r), 0f, 0f, RingWobble.None);

        // Dark at the middle of the screen (a bright ring there reads as a crosshair), brighter as it reaches the edges.
        float reachIn = DrawHelpers.Smooth((r - 0.08f * f.Short) / (0.22f * f.Short));
        scene.Ring(_shock, PrimitiveRole.Inscription, _seed, 30, 14f * f.Px, f.Alpha * MathF.Min(1.4f, gain * 1.2f) * reachIn, 0f, gain, 1f, 0f, f.Color);
    }

    // Things that drift down the tunnel

    /// <summary>Banks of ink-dark smoke along the walls, swelling as they pass.</summary>
    private void CollectGloom()
    {
        ref readonly CastFrame f = ref _f;
        for (int i = 0; i < GloomCount; i++)
        {
            float hu = _tunnel.HeightOf(_gloomAt[i], out int lap);
            float near = _tunnel.Near(hu);
            int s = _seed + 7000 + i * 7919 + lap * 104729;
            Vector2 at = Place(DrawHelpers.HashRange(s + 1, 520f, 900f) * f.Px, DrawHelpers.Hash01(s + 2) * MathF.Tau, hu * f.Px, out float scale);

            float k = f.Alpha * _appear * DepthFade(near, 0.25f, 0.85f) * CenterFade(at, 0.25f, 0.20f) * 0.55f;
            if (k < 0.02f) continue;

            AddThing(hu, new ParticlePrimitive
            {
                Position = at, Size = DrawHelpers.HashRange(s + 3, 150f, 280f) * f.Px * scale * 0.6f, AgeRatio = 0.45f, Brightness = k,
                Seed = s, Role = PrimitiveRole.Miasma, Rotation = DrawHelpers.Hash01(s + 4) * MathF.Tau, ColorOverride = f.Color,
            });
        }
    }

    /// <summary>Specks of pale light streaming out of the dark: nothing says speed like dust going by.</summary>
    private void CollectSpecks()
    {
        ref readonly CastFrame f = ref _f;
        for (int i = 0; i < SpeckCount; i++)
        {
            float hu = _tunnel.HeightOf(_specAt[i], out int lap);
            float near = _tunnel.Near(hu);
            int s = _seed + 8000 + i * 7919 + lap * 104729;
            Vector2 at = Place(DrawHelpers.HashRange(s + 1, 120f, 900f) * f.Px, DrawHelpers.Hash01(s + 2) * MathF.Tau, hu * f.Px, out float scale);

            float k = f.Alpha * _appear * DepthFade(near, 0.10f, 0.90f) * CenterFade(at, 0.16f, 0.14f) * DrawHelpers.HashRange(s + 3, 0.30f, 0.80f) * Fog(near);
            if (k < 0.02f) continue;

            AddThing(hu, new ParticlePrimitive
            {
                Position = at, Size = DrawHelpers.HashRange(s + 5, 1.8f, 4.2f) * f.Px * scale, AgeRatio = 0.5f, Brightness = k,
                Seed = s, Role = PrimitiveRole.Mote, ColorOverride = f.Color,
            });
        }
    }

    /// <summary>
    /// The eyes. Every lane is a different eye on each lap: lidded eyes of every kind, floating eyeballs trailing their nerves, and
    /// clusters of small ones. Far ones are shut and dim; they open as they near, turn to watch you, and are gone past the lens.
    /// </summary>
    private void CollectEyes(float tq)
    {
        for (int i = 0; i < EyeLanes; i++) LaneEye(i, false, tq);
        for (int i = 0; i < OrbLanes; i++) LaneEye(i, true, tq);
        for (int i = 0; i < ClusterLanes; i++) LaneCluster(i, tq);
    }

    private void LaneEye(int lane, bool orb, float tq)
    {
        ref readonly CastFrame f = ref _f;
        float hu = _tunnel.HeightOf(orb ? _orbAt[lane] : _eyeAt[lane], out int lap);
        float near = _tunnel.Near(hu);
        if (near < 0.04f || near > 0.98f) return;

        int s = _seed + (orb ? 9000 : 4000) + lane * 7919 + lap * 104729;
        float theta = DrawHelpers.Hash01(s + 2) * MathF.Tau;
        Vector2 at = Place(DrawHelpers.HashRange(s + 1, 170f, 600f) * f.Px, theta, hu * f.Px, out float scale);

        float k = f.Alpha * DepthFade(near) * CenterFade(at) * Fog(near) * IntroIn(tq, lane * 53 + (orb ? 7 : 0));
        if (orb) k *= 1f - 0.7f * SyncBlink(f.Age);
        float size = DrawHelpers.HashRange(s + 3, orb ? 32f : 46f, orb ? 58f : 98f) * f.Px * scale;
        if (k < 0.02f || size < 3f) return;

        // A fifth of the lidded ones are stitched shut and sleep through it all.
        bool sleeper = !orb && DrawHelpers.Hash01(s + 9) < 0.20f;
        EyeStyle style = orb ? EyeStyle.Orb : sleeper ? EyeStyle.Sewn : PickStyle(s + 11);
        float open = sleeper ? 0f : orb ? 1f : EyeOpen(lane, s, near, f.Age, tq);

        // Turned along the way round the tunnel, never upside down, rocking a little.
        float along = theta + _swirl * hu + _roll + MathF.PI * 0.5f;
        float roll = MathF.Atan2(MathF.Sin(along), MathF.Cos(along));
        if (roll > MathF.PI * 0.5f) roll -= MathF.PI; else if (roll < -MathF.PI * 0.5f) roll += MathF.PI;
        roll = orb ? 0f : roll * 0.40f + 0.2f * MathF.Sin(f.Age * 0.6f + lane);

        AddThing(hu, new ParticlePrimitive
        {
            Position = at, Size = size, AgeRatio = 0.5f, Brightness = k, Seed = s, Role = PrimitiveRole.Eye,
            Variant = ParticleEye.Held(open, style), Rotation = roll, ColorOverride = f.Color,
        });
    }

    /// <summary>A clump of small eyes sharing a stretch of tunnel, opening one after another.</summary>
    private void LaneCluster(int lane, float tq)
    {
        ref readonly CastFrame f = ref _f;
        float hu0 = _tunnel.HeightOf(_clusterAt[lane], out int lap);
        if (_tunnel.Near(hu0) < 0.04f) return;

        int s = _seed + 11000 + lane * 7919 + lap * 104729;
        float radial = DrawHelpers.HashRange(s + 1, 300f, 560f) * f.Px, theta = DrawHelpers.Hash01(s + 2) * MathF.Tau;
        int members = ClusterEyes - 1 + (int)(DrawHelpers.Hash01(s + 3) * 3f);

        for (int m = 0; m < members; m++)
        {
            int ms = s + 100 + m * 53;
            float hu = hu0 + DrawHelpers.HashRange(ms + 1, -90f, 90f);
            float near = _tunnel.Near(hu);
            if (near < 0.04f || near > 0.98f) continue;

            float a = DrawHelpers.Hash01(ms + 2) * MathF.Tau, d = DrawHelpers.HashRange(ms + 3, 0f, 95f) * f.Px;
            Vector2 at = Place(radial + d * MathF.Cos(a), theta, hu * f.Px, out float scale, d * MathF.Sin(a));

            float k = f.Alpha * DepthFade(near) * CenterFade(at) * Fog(near) * IntroIn(tq, lane * 97 + m);
            float size = DrawHelpers.HashRange(ms + 4, 14f, 30f) * f.Px * scale;
            if (k < 0.02f || size < 3f) continue;

            // They wake one after another as the clump comes on.
            float open = DrawHelpers.Smooth((near - 0.30f - 0.05f * m) / 0.08f) * (1f - SyncBlink(f.Age));
            open = MathF.Max(open * (1f - Blink(f.Age, ms)), _staring * 0.95f);
            AddThing(hu, new ParticlePrimitive
            {
                Position = at, Size = size, AgeRatio = 0.5f, Brightness = k, Seed = ms, Role = PrimitiveRole.Eye,
                Variant = ParticleEye.Held(open, DrawHelpers.Hash01(ms + 5) < 0.3f ? EyeStyle.Bloodshot : EyeStyle.Watcher),
                Rotation = DrawHelpers.HashRange(ms + 6, -0.5f, 0.5f), ColorOverride = f.Color,
            });
        }
    }

    /// <summary>
    /// How far a lane's eye is open: in the cast they wake in a scatter and all blink once together; after, each opens as it nears,
    /// blinks now and then, and is shut again as it goes. An apparition with eyes in it opens every one.
    /// </summary>
    private float EyeOpen(int lane, int seed, float near, float age, float tq)
    {
        float awakeFrom = DrawHelpers.HashRange(seed + 12, 0.22f, 0.50f);
        float natural = DrawHelpers.Smooth((near - awakeFrom) / 0.08f) * (1f - DrawHelpers.Smooth((near - 0.93f) / 0.05f)) * (1f - Blink(age, seed));
        float intro = DrawHelpers.Smooth((tq - (0.5f + 0.9f * DrawHelpers.Hash01(_seed + lane * 53))) / 0.12f);

        float open = DrawHelpers.Lerp(intro, natural, DrawHelpers.Smooth((age - IntroEyes) / 0.4f)) * (1f - SyncBlink(age));
        return MathF.Max(open, 0.95f * _staring);
    }

    /// <summary>In the cast each eye comes out of the dark in its own time, a scatter between 0.15 s and 0.9 s.</summary>
    private float IntroIn(float tq, int salt)
        => DrawHelpers.Smooth((tq - 0.15f - 0.75f * DrawHelpers.Hash01(_seed + salt)) / 0.25f);

    /// <summary>A quick blink every few seconds, each eye on its own beat.</summary>
    private static float Blink(float age, int seed)
    {
        float phase = (age * 0.31f + DrawHelpers.Hash01(seed + 10) * 7f) % 1f;
        return phase < 0.045f ? MathF.Sin(MathF.PI * phase / 0.045f) : 0f;
    }

    /// <summary>The blink every eye in the tunnel makes together, once, as the cast settles.</summary>
    private static float SyncBlink(float age)
        => DrawHelpers.Smooth((age - BlinkAt) / 0.05f) * (1f - DrawHelpers.Smooth((age - BlinkAt - 0.30f) / 0.12f));

    private void AddThing(float height, in ParticlePrimitive p) => _things.Add(new Thing { Height = height, Particle = p });

    // Atmosphere

    private void EmitAtmosphere(EffectScene scene, float beat)
    {
        ref readonly CastFrame f = ref _f;
        float slam = MathF.Exp(-f.Age / 0.45f);
        float eventDark = _v.Kind switch { VisionKind.Stare => 0.36f, VisionKind.GreatEye => 0.30f, VisionKind.Surge => 0.18f, VisionKind.Gate => 0.16f, _ => 0f };

        // The vignette slams shut on the cast, then closes a little with each beat and hard when something is shown.
        scene.RequestVignette(HauntColor.Vignette, 0.19f + 0.09f * slam + 0.07f * _env + 0.03f * beat,
                              f.Alpha * (0.64f + 0.30f * slam + 0.22f * _env + 0.12f * beat), priority: 17, f.Color);
        scene.EdgeSpill(f.Size, f.Short * 0.12f, Veil, 0.16f * f.Alpha * _breath * (1f + 0.5f * beat), f.Color);
        scene.Wash(f.Size, Drain, 0.06f * f.Alpha, f.Color);

        // The cast: a cold flash, then the lights go out for a moment and the tunnel is seen by its own light.
        float flash = f.Age < 0.40f ? MathF.Exp(-f.Age / 0.08f) : 0f;
        scene.Wash(f.Size, Flash, 0.50f * flash * f.Gate, f.Color);
        float dark = DrawHelpers.Smooth(f.Age / 0.12f) * (1f - DrawHelpers.Smooth((f.Age - 0.40f) / 1.0f));
        scene.Wash(f.Size, Ink, 0.36f * dark * f.Gate, f.Color);

        // An apparition: the world dims so it can be seen, and it lands with a cold flash.
        float t = f.Age - _v.Start;
        scene.Wash(f.Size, Ink, eventDark * _env * f.Alpha, f.Color);
        if (_v.Kind != VisionKind.None)
            scene.Wash(f.Size, Flash, (_v.Kind == VisionKind.Gate ? 0.22f : 0.12f) * MathF.Exp(-t / 0.05f) * f.Alpha, f.Color);
    }

    // Apparitions

    /// <summary>A crowd of eyes in the dark at the edges, or one enormous eye: drawn in front of everything, flickering on and off.</summary>
    private void EmitVisionEyes(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        float t = f.Age - _v.Start;

        if (_v.Kind == VisionKind.Stare)
        {
            for (int k = 0; k < _stareCount; k++)
            {
                ref readonly Sprite e = ref _stare[k];
                float te = t - e.Delay;
                if (te < 0f) continue;

                // Each opens on its own stutter, then they all slam shut together as it ends.
                float lit = Flicker.Level(te, _v.Life - e.Delay, e.Seed);
                float open = DrawHelpers.Smooth(te / 0.10f) * DrawHelpers.Smooth((_v.Life - t) / 0.07f);
                AddVisionEye(scene, e.At * f.Size, e.Size * f.Px, e.Roll, open, lit, e.Style, e.Seed);
            }
        }
        else if (_v.Kind == VisionKind.GreatEye)
        {
            // One enormous eye that looks at you, blinks once, and is gone.
            float blink = MathF.Exp(-MathF.Pow((t - 0.55f * _v.Life) / 0.045f, 2f));
            float open = DrawHelpers.Smooth(t / 0.08f) * (1f - blink) * DrawHelpers.Smooth((_v.Life - t) / 0.06f);
            AddVisionEye(scene, _v.At * f.Size, _v.Size * f.Px, _v.Roll, open, _level, _v.Style, _v.Seed);
        }
    }

    private void AddVisionEye(EffectScene scene, Vector2 at, float size, float roll, float open, float lit, EyeStyle style, int seed)
    {
        ref readonly CastFrame f = ref _f;
        if (open <= 0.01f || lit <= 0.01f) return;
        scene.AddParticle(new ParticlePrimitive
        {
            Position = at, Size = size, AgeRatio = 0.5f, Brightness = f.Alpha * lit, Seed = seed, Role = PrimitiveRole.Eye,
            Variant = ParticleEye.Held(open, style), Rotation = roll, ColorOverride = f.Color,
        });
    }

    /// <summary>
    /// A gate: three rings, one after another, rushing out of the far end of the tunnel and straight through you, ghosted red and cyan,
    /// the nearest wearing a band of script. It hits as it passes.
    /// </summary>
    private void EmitGate(EffectScene scene)
    {
        if (_v.Kind != VisionKind.Gate) return;
        ref readonly CastFrame f = ref _f;
        float px = f.Px, t = f.Age - _v.Start;

        for (int g = 0; g < GateRings; g++)
        {
            float tg = t - g * 0.09f;
            if (tg < 0f) continue;
            float lit = Flicker.Level(tg, _v.Life, _v.Seed + g) * (g == 0 ? 1f : 0.7f);
            if (lit <= 0.01f) continue;

            // Slow to leave the dark, then faster and faster, until it is past you.
            float u = DrawHelpers.Saturate(tg / (_v.Life * 0.85f));
            float h = DrawHelpers.Lerp(FarHeight * 0.75f, NearHeight * 0.9f, u * u * (0.4f + 0.6f * u)) * px;
            float scale = _cam.ScaleAt(h);
            float radius = f.Short * (0.74f - 0.07f * g);
            float angle = _v.Seed * 0.001f + g * 0.9f + t * 1.2f;
            SigilGeometry.Ring(_gate[g], GateSegments, in _cam, radius, h, angle, RingWobble.None);

            float width = Math.Clamp(14f * px * scale, 8f * px, 44f * px), k = f.Alpha * lit;
            scene.Ring(_gate[g], PrimitiveRole.Inscription, _v.Seed, 60 + g, width, k, 0f, _env, 1f, g == 1 ? 90f : 0f, f.Color);

            if (g > 0) continue;

            // The leading ring is ghosted: the same ring twice more, a glow of red and of cyan, sliding apart; and it wears a band of script.
            float apart = (10f + 26f * _env) * px;
            Offset(_gateA, _gate[g], new Vector2(-apart, 0.22f * apart));
            Offset(_gateB, _gate[g], new Vector2(apart, -0.22f * apart));
            for (int side = 0; side < 2; side++)
                scene.Ring(side == 0 ? _gateA : _gateB, PrimitiveRole.Halo, _v.Seed, 70 + side, width * 1.5f, k * 1.8f, 0f, 0f, 1f, 0f,
                           f.Color ?? (side == 0 ? RedSplit : CyanSplit));
            EmitScript(scene, radius, h, angle, 22f * px * scale, k, ScriptRunes + 6, _v.Seed);
        }
    }

    private static void Offset(StrandPath to, StrandPath from, Vector2 by)
    {
        to.Count = from.Count;
        for (int k = 0; k < from.Count; k++) to.Points[k] = from.Points[k] + by;
        Array.Copy(from.Arc, to.Arc, from.Count);   // a shift changes no lengths
    }
}

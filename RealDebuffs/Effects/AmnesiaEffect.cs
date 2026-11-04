using System.Numerics;
using RealDebuffs.Effects.Framework;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects;

/// <summary>What the fog murmurs as it forgets. Edit freely: lowercase letters, spaces and ' , . ! ? - only (anything else is left blank).</summary>
public static class AmnesiaWhispers
{
    public static readonly string[] Phrases =
    {
        "what was i doing", "where was i going", "it was just here", "on the tip of my tongue", "who was i talking to", "why did i come here",
        "i knew this", "wait", "what was i saying", "something important", "i was just about to", "how long have i been here",
        "the name, the name", "it's slipping away", "never mind", "what day is it", "have we met", "which button was it",
        "what was the mechanic", "who has the tether", "what was my rotation", "i had a plan", "left or right", "was i afraid",
        "hold on", "it doesn't matter", "i remember...", "no, wait", "where am i", "who am i",
        "i can't think", "something about a door", "what was it", "i'm sure i knew", "just a second ago", "forgive me, i forgot",
    };
}

/// <summary>Amnesia: a fall down a pale spiral drain toward a light. Now and then the fall stalls, everything is forgotten, and the drain is wrung into a new spiral.</summary>
public sealed class AmnesiaEffect : SceneEffect, IHasSwappableSlots
{
    // The spiral is what a "made of X" phrase replaces; so is the fog.
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        Slot.Stroke(PrimitiveRole.MainStroke, "Spiral", "stroke.silk", hero: true),
        Slot.Particle(PrimitiveRole.Mist, "Fog", "particle.pearlfog", hero: true),
        Slot.Particle(PrimitiveRole.Whisper, "Murmurs", "particle.fugue"),
        Slot.Particle(PrimitiveRole.Mote, "Memories", "particle.lethe"),
        Slot.Region(RegionKind.FlatFill, "Tunnel", "region.vortex"),
    };

    private const float CameraDistance = 1500f;
    private readonly SigilView _view = new(new SigilSway(BaseTilt: 0.07f, TiltSway: 0.045f, Precession: 0.09f, LurchTilt: 0.18f, LurchSeconds: 0.9f, BreathBase: 0.94f, BreathAmp: 0.06f), CameraDistance);

    // Pacing: how fast it all feels.
    private const float CruiseSpeed = 640f;                      // px/s down the tunnel at 1080p; a lap takes about 11 s
    private const float IntroSecs = 2.4f, IntroSpin = 2.4f;     // the cast rushes in and slows; the view whips round this many radians
    private const float FirstWhisper = 1.4f;

    // A lapse: the fall stalls, the screen whitens, the spiral is swapped (SwapAt), and the fall lurches on.
    private const float SwapAt = 0.7f, LapseSecs = 2.5f, FirstLapseMin = 8f, FirstLapseMax = 11f, LapseMin = 9f, LapseMax = 15f;

    // The spiral: arms riding the wall as overlapping ribbons, each flying down its own helix.
    private const int MaxArms = 5, PiecesPerArm = 5, PiecePts = 20;
    private const float PieceLen = 2800f;

    // What drifts down the tunnel.
    private const int Billows = 14, Specks = 70;
    private const float FarTier = -3500f, MidTier = -1200f, NearTier = 200f;  // heights the whispers sit at among the things

    private static readonly uint Veil = DrawHelpers.Pack(0.80f, 0.82f, 0.94f);

    /// <summary>Something drawn this frame at some height down the tunnel: collected, sorted far to near, then drawn.</summary>
    private struct Thing { public float Height; public ParticlePrimitive Particle; }

    private static readonly Comparison<Thing> FarToNear = (a, b) => a.Height.CompareTo(b.Height);

    /// <summary>The spiral as it is drawn now; each lapse rolls a new one.</summary>
    private struct Spiral { public int Arms; public float Pitch, Width; }
    private Spiral _spiral;

    private readonly TunnelFlight _tunnel = new(VortexField.FarHeight, VortexField.NearHeight);

    /// <summary>The same flight, with room past the lens for a whole ribbon to leave before it comes round again.</summary>
    private readonly TunnelFlight _pieceFlight = new(VortexField.FarHeight, VortexField.NearHeight + PieceLen);

    private readonly CastTracker _cast = new(envelope: true);
    private CastFrame _f;
    private readonly VortexField _field = new();
    private readonly WhisperField _whispers = new(AmnesiaWhispers.Phrases);
    private FlashSchedule _lapses;
    private readonly StrandPath[] _piece = new StrandPath[MaxArms * PiecesPerArm];
    private readonly float[] _pieceAt = new float[MaxArms * PiecesPerArm], _fogAt = new float[Billows], _speckAt = new float[Specks];
    private readonly List<Thing> _things = new(128);

    // per cast
    private int _seed, _lapseIndex;
    private float _hand, _spinDir, _spinRate, _spinPhase, _baseAngle, _cruise, _pullEvery, _pullPhase, _swirl, _kickDone, _appear;

    // the lapse in progress (_lapseAt < 0: none)
    private float _lapseAt = -100f, _lapseKick;
    private bool _swapped;

    public AmnesiaEffect() : base(DebuffKind.Amnesia, "Amnesia",
        "You fall down a pale spiral drain toward a light: ribbons of satin wind down the walls through milky fog that murmurs what it forgets. Now and then the fall stalls, everything is lost, and the drain is wrung into a new spiral.",
        11, "Amnesia",
        keywords: ["amnesia", "amnesiac", "forgot", "forgotten", "forgetful", "oblivion", "memory loss", "dazed"])
    {
        for (int i = 0; i < _piece.Length; i++) _piece[i] = new StrandPath(PiecePts);
    }

    public override void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        var f = _f = _cast.Frame(screenSize, alpha, time, dt, colorOverride);
        if (f.Fresh) BeginCast(f.Seed);
        else if (f.Resized) _whispers.Clear();

        float t = StepLapse(in f);
        float bump = t >= 0f ? MathF.Sin(MathF.PI * t / LapseSecs) : 0f;
        float bleach = Bleach(t);
        float pull = 0.5f + 0.5f * MathF.Sin(MathF.Tau * f.Age / _pullEvery + _pullPhase);
        _appear = Prog(f.Age, 0.10f, 1.0f);

        _tunnel.Advance(f.Dt, CruiseSpeed * _cruise * SpeedNow(f.Age, t));
        _pieceFlight.Travel = _tunnel.Travel;
        _swirl += (_hand * _spiral.Pitch - _swirl) * DrawHelpers.Saturate(1f - MathF.Exp(-f.Dt * 3f));

        // The fall breathes in, and a lapse thumps the whole view.
        _view.Update(in f, extraTilt: 0.03f * pull + 0.10f * bump, distanceScale: 1f - 0.04f * pull - 0.06f * bump);

        EmitTunnel(scene, SpinAt(f.Age, t), bleach, pull, bump);
        EmitAtmosphere(scene, bleach, pull);
        EmitSpiral(scene, bleach);

        // Particles draw over every stroke, in the order they are submitted: the things far to near, with the whispers set among them.
        _things.Clear();
        CollectFog();
        CollectSpecks();
        _things.Sort(FarToNear);
        _whispers.Update(in f);
        int next = 0;
        Flush(scene, ref next, FarTier);  _whispers.Emit(scene, in f, 2);
        Flush(scene, ref next, MidTier);  _whispers.Emit(scene, in f, 1);
        Flush(scene, ref next, NearTier); _whispers.Emit(scene, in f, 0);
        Flush(scene, ref next, float.MaxValue);
    }

    private void Flush(EffectScene scene, ref int next, float below)
    {
        while (next < _things.Count && _things[next].Height < below) scene.AddParticle(_things[next++].Particle);
    }

    // the cast

    private void BeginCast(int seed)
    {
        _seed = seed;
        _view.Begin(seed);
        _field.Begin(seed);
        _whispers.Begin(seed, FirstWhisper);
        _lapses.Begin(seed, DrawHelpers.HashRange(seed + 6, FirstLapseMin, FirstLapseMax));

        _hand = DrawHelpers.Sign(seed + 1);
        _spinDir = DrawHelpers.Sign(seed + 2);
        _spinRate = DrawHelpers.HashRange(seed + 3, 0.10f, 0.20f);
        _spinPhase = DrawHelpers.Hash01(seed + 4) * MathF.Tau;
        _baseAngle = DrawHelpers.Hash01(seed + 5) * MathF.Tau;
        _cruise = DrawHelpers.HashRange(seed + 7, 0.85f, 1.15f);
        _pullEvery = DrawHelpers.HashRange(seed + 8, 5.5f, 8.0f);
        _pullPhase = DrawHelpers.Hash01(seed + 9) * MathF.Tau;

        _lapseIndex = 0; _lapseAt = -100f; _kickDone = 0f;
        _spiral = Roll(0);
        _swirl = _hand * _spiral.Pitch;

        // Where each thing starts along the tunnel; ribbons keep an even stagger so an arm never has a gap.
        _tunnel.Travel = DrawHelpers.Hash01(seed + 12) * _tunnel.Length;
        Spread(_fogAt, seed + 700, _tunnel.Length);
        Spread(_speckAt, seed + 800, _tunnel.Length);
        for (int i = 0; i < _pieceAt.Length; i++)
            _pieceAt[i] = _pieceFlight.Length * (i % PiecesPerArm + 0.37f * (i / PiecesPerArm) % 1f) / PiecesPerArm;
    }

    private static void Spread(float[] offsets, int seed, float length)
    {
        for (int i = 0; i < offsets.Length; i++) offsets[i] = length * (i + DrawHelpers.Hash01(seed + i * 17)) / offsets.Length;
    }

    /// <summary>The spiral for the nth drawing of it: arm count, how tightly the walls twist, and how broad the ribbons.</summary>
    private Spiral Roll(int index)
    {
        int s = _seed + index * 7919 + 100;
        int arms = 3 + (int)(DrawHelpers.Hash01(s + 1) * 3f);
        return new Spiral { Arms = arms, Pitch = DrawHelpers.HashRange(s + 2, 0.00065f, 0.00105f), Width = 130f + DrawHelpers.HashRange(s + 3, 70f, 90f) * 4f / arms };
    }

    /// <summary>Starts, steps and ends a lapse: the words are let go, the spiral is swapped while the screen is whitest, and the fall lurches on. Returns seconds into it, or -1.</summary>
    private float StepLapse(in CastFrame f)
    {
        if (_lapseAt >= 0f && f.Age - _lapseAt >= LapseSecs)
        {
            _kickDone += _lapseKick;
            _lapseAt = -100f;
            _lapses.Finish(f.Age, LapseMin, LapseMax);
        }
        if (_lapseAt < 0f && !f.Leaving && _lapses.TryStart(f.Age, out int seed))
        {
            _lapseAt = f.Age; _swapped = false;
            _lapseKick = DrawHelpers.HashRange(seed + 1, 1.2f, 2.4f);
            _whispers.LetGo(f.Age);
        }
        if (_lapseAt < 0f) return -1f;

        float t = f.Age - _lapseAt;
        if (!_swapped && t >= SwapAt) { _spiral = Roll(++_lapseIndex); _swapped = true; }
        return t;
    }

    /// <summary>The fall in multiples of cruising speed: the cast rushes in and slows; a lapse stalls it, then tears it forward.</summary>
    private static float SpeedNow(float age, float lapseT)
    {
        float intro = 1f + 11f * MathF.Exp(-age / 0.70f);
        if (lapseT < 0f) return intro;
        float v = lapseT < SwapAt
            ? 1f - 0.95f * DrawHelpers.Smooth(lapseT / SwapAt)
            : 0.05f + 0.95f * DrawHelpers.Smooth((lapseT - SwapAt) / 0.9f) + 1.6f * MathF.Sin(MathF.PI * DrawHelpers.Saturate((lapseT - SwapAt) / (LapseSecs - SwapAt)));
        return intro * v;
    }

    /// <summary>How far the view has rolled: a steady turn, a slow surge, the whip of the cast and the kick of each lapse.</summary>
    private float SpinAt(float age, float lapseT)
    {
        float kick = lapseT >= 0f ? _lapseKick * DrawHelpers.Smooth(lapseT / LapseSecs) : 0f;
        return _baseAngle + _spinDir * (_spinRate * age + 0.25f * MathF.Sin(0.29f * age + _spinPhase) + IntroSpin * (1f - MathF.Exp(-age / 1.2f)) + _kickDone + kick);
    }

    private static float Prog(float age, float start, float secs) => DrawHelpers.Smooth(DrawHelpers.Saturate((age - start) / secs));

    /// <summary>Fades a thing in out of the far light and out again once it has passed the lens.</summary>
    private static float DepthFade(float near, float inEnd, float outStart)
        => DrawHelpers.Smooth(near / inEnd) * (1f - DrawHelpers.Smooth((near - outStart) / (1f - outStart)));

    // the tunnel and its atmosphere

    /// <summary>How white the screen is in a lapse: rising to a peak as the spiral is let go, then clearing.</summary>
    private static float Bleach(float t)
        => t < 0f ? 0f : 0.95f * DrawHelpers.Smooth(t / SwapAt) * (1f - DrawHelpers.Smooth((t - SwapAt) / (LapseSecs - SwapAt)));

    private void EmitTunnel(EffectScene scene, float spin, float bleach, float pull, float bump)
    {
        ref readonly CastFrame f = ref _f;
        var v = _field;
        v.Flight = _tunnel; v.Cam = _view.Cam; v.Px = f.Px; v.Short = f.Short; v.Time = f.Time;
        v.Swirl = _swirl * (1f - 0.5f * DrawHelpers.Smooth(f.Leave)); v.Roll = spin;
        v.Breath = 1f - 0.02f * pull - 0.03f * bump; v.Alpha = f.Alpha * (1f - 0.35f * f.Leave);
        v.Arms = _spiral.Arms; v.Appear = _appear; v.Bleach = bleach; v.Eye = 0.85f + 0.25f * pull;

        // The cast lands as a ring of light racing down the tunnel from its far end.
        v.Front = VortexField.FarHeight + _tunnel.Length * DrawHelpers.EaseOutCubic(DrawHelpers.Saturate(f.Age / 1.1f));
        v.FrontGain = f.Age < 1.6f ? 0.9f * MathF.Exp(-f.Age / 0.8f) : 0f;
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = f.Size, Alpha = 1f, State = v, ColorOverride = f.Color });
    }

    private void EmitAtmosphere(EffectScene scene, float bleach, float pull)
    {
        ref readonly CastFrame f = ref _f;
        float intro = DrawHelpers.Smooth(f.Age / IntroSecs);

        // The edges bleach rather than darken, and the vignette breathes with the fall.
        scene.RequestVignette(MemoryColor.Vignette, 0.20f + 0.14f * intro, MathF.Min(1f, f.Alpha * 0.80f * intro * (0.90f + 0.15f * pull)), priority: 22, f.Color);
        scene.Wash(f.Size, Veil, 0.16f * f.Alpha * DrawHelpers.Smooth(f.Age), f.Color);

        // The cast flash uses f.Gate so the fade-in can't swallow it; a lapse is the same white, slower.
        float flash = f.Age < 1.4f ? MathF.Exp(-MathF.Pow(f.Age / 0.42f, 2f)) : 0f;
        scene.Wash(f.Size, MemoryColor.Flash, 0.92f * flash * f.Gate, f.Color);
        scene.Wash(f.Size, MemoryColor.Flash, 0.55f * bleach * f.Alpha, f.Color);
    }

    // the spiral

    /// <summary>Each arm is a run of overlapping ribbons riding one helix of the wall toward the lens, wider as they near.</summary>
    private void EmitSpiral(EffectScene scene, float bleach)
    {
        ref readonly CastFrame f = ref _f;
        float px = f.Px;
        float gain = f.Alpha * (1f - DrawHelpers.Saturate(bleach / 0.8f)) * (1f - f.Leave) * _view.Breath;
        if (gain <= 0.01f) return;

        for (int k = 0; k < _spiral.Arms; k++)
        {
            float theta = MathF.Tau * k / _spiral.Arms;
            for (int m = 0; m < PiecesPerArm; m++)
            {
                int i = k * PiecesPerArm + m;
                float hu = _pieceFlight.HeightOf(_pieceAt[i], out int lap);
                float near = _pieceFlight.Near(hu);
                int s = _seed + 1000 + i * 7919 + lap * 104729;

                float appear = DrawHelpers.Smooth((f.Age - 0.25f - 0.6f * DrawHelpers.Hash01(_seed + i * 13)) / 0.5f);
                float brightness = gain * appear * DepthFade(near, 0.10f, 0.97f);
                float head = MathF.Min(hu, VortexField.NearHeight), tail = MathF.Max(VortexField.FarHeight, hu - PieceLen);
                if (brightness < 0.01f || head - tail < 60f) continue;

                var path = _piece[i];
                for (int p = 0; p < PiecePts; p++)
                    path.Points[p] = _field.At(theta, DrawHelpers.Lerp(head, tail, p / (PiecePts - 1f)), 0.97f, out _);
                path.Count = PiecePts;
                path.BuildArc();

                scene.AddStroke(new StrokePrimitive
                {
                    Path = path, Role = PrimitiveRole.MainStroke, Reveal = 1f, WidthHint = _spiral.Width * px * _view.Cam.ScaleAt(head * px),
                    Brightness = brightness, Seed = s, Phase = i * 1.3f, Depth = (1f - near) * 0.6f, Twist = i * 1.3f + _hand * 0.9f * f.Age, ColorOverride = f.Color,
                });
            }
        }
    }

    // things that drift down the tunnel

    private void AddThing(float height, in ParticlePrimitive particle) => _things.Add(new Thing { Height = height, Particle = particle });

    /// <summary>Billows of milky fog along the walls, swelling as they pass the lens.</summary>
    private void CollectFog()
    {
        ref readonly CastFrame f = ref _f;
        for (int i = 0; i < Billows; i++)
        {
            float hu = _tunnel.HeightOf(_fogAt[i], out int lap);
            float near = _tunnel.Near(hu);
            int s = _seed + 7000 + i * 7919 + lap * 104729;
            Vector2 at = _field.At(DrawHelpers.Hash01(s + 2) * MathF.Tau, hu, DrawHelpers.HashRange(s + 1, 0.25f, 1.0f), out float scale);

            float k = f.Alpha * _appear * DepthFade(near, 0.20f, 0.85f) * 0.55f * (1f - 0.6f * f.Leave);
            if (k < 0.02f) continue;

            AddThing(hu, new ParticlePrimitive
            {
                Position = at, Size = DrawHelpers.HashRange(s + 3, 240f, 440f) * f.Px * scale * 0.75f, AgeRatio = 0.45f, Brightness = k,
                Seed = s, Role = PrimitiveRole.Mist, Rotation = DrawHelpers.Hash01(s + 4) * MathF.Tau, Variant = 60, ColorOverride = f.Color,
            });
        }
    }

    /// <summary>Specks of light streaming out of the far light: nothing says speed like dust going by.</summary>
    private void CollectSpecks()
    {
        ref readonly CastFrame f = ref _f;
        for (int i = 0; i < Specks; i++)
        {
            float hu = _tunnel.HeightOf(_speckAt[i], out int lap);
            float near = _tunnel.Near(hu);
            int s = _seed + 8000 + i * 7919 + lap * 104729;
            Vector2 at = _field.At(DrawHelpers.Hash01(s + 2) * MathF.Tau, hu, DrawHelpers.HashRange(s + 1, 0.10f, 1.0f), out float scale);

            float k = f.Alpha * _appear * DepthFade(near, 0.10f, 0.92f) * DrawHelpers.HashRange(s + 3, 0.35f, 0.90f) * (1f - f.Leave);
            if (k < 0.02f) continue;

            AddThing(hu, new ParticlePrimitive
            {
                Position = at, Size = DrawHelpers.HashRange(s + 5, 2.5f, 7f) * f.Px * scale, AgeRatio = 0.5f, Brightness = k,
                Seed = s, Role = PrimitiveRole.Mote, ColorOverride = f.Color,
            });
        }
    }
}

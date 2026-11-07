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

/// <summary>Amnesia: a pale spiral turning behind milky fog. Now and then the fall stalls, everything is forgotten, and the spiral unwinds and is wound anew.</summary>
public sealed class AmnesiaEffect : SceneEffect, IHasSwappableSlots
{
    // The fog is what a "made of X" phrase replaces.
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        Slot.Particle(PrimitiveRole.Mist, "Fog", "particle.pearlfog", hero: true),
        // The murmurs are the secondary layer targeted by "with X" and modifier-colour phrases.
        Slot.Particle(PrimitiveRole.Whisper, "Murmurs", "particle.fugue", modifier: true),
        Slot.Particle(PrimitiveRole.Mote, "Memories", "particle.lethe"),
        Slot.Region(RegionKind.FlatFill, "Spiral and fog", "region.vortex"),
    };

    private const float CameraDistance = 1500f;
    private readonly SigilView _view = new(new SigilSway(BaseTilt: 0.07f, TiltSway: 0.045f, Precession: 0.09f, LurchTilt: 0.18f, LurchSeconds: 0.9f, BreathBase: 0.94f, BreathAmp: 0.06f), CameraDistance);

    // Pacing: how fast it all feels.
    private const float CruiseSpeed = 640f;                      // px/s down the tunnel at 1080p; a lap takes about 11 s
    private const float IntroSecs = 2.4f, IntroSpin = 2.4f;     // the cast rushes in and slows; the view whips round this many radians
    private const float FirstWhisper = 1.4f;

    // A lapse: the fall stalls, the fog thins and the spiral unwinds into the centre, a new one is rolled (SwapAt), and it winds back out as the fall lurches on.
    private const float SwapAt = 0.7f, LapseSecs = 2.5f, FirstLapseMin = 8f, FirstLapseMax = 11f, LapseMin = 9f, LapseMax = 15f;

    // The spiral behind the fog: how fast it flows outward (fraction of the short side per second, matching the fall), and how fast the cast first sends it.
    private const float SpiralFlow = 0.065f, SpiralRush = 4f;

    // What drifts down the tunnel.
    private const int Billows = 14, Specks = 70;
    private const float FarTier = -3500f, MidTier = -1200f, NearTier = 200f;  // heights the whispers sit at among the things

    /// <summary>Something drawn this frame at some height down the tunnel: collected, sorted far to near, then drawn.</summary>
    private struct Thing { public float Height; public ParticlePrimitive Particle; }

    private static readonly Comparison<Thing> FarToNear = (a, b) => a.Height.CompareTo(b.Height);

    /// <summary>The spiral as it is drawn now; each lapse rolls a new one: the fog's arm count and twist, and the spiral's own arm count and band gap.</summary>
    private struct Spiral { public int Arms, BandArms; public float Pitch, Gap; }
    private Spiral _spiral;

    private readonly TunnelFlight _tunnel = new(VortexField.FarHeight, VortexField.NearHeight);

    private CastFrame _f;
    private readonly VortexField _field = new();
    private readonly WhisperField _whispers = new(AmnesiaWhispers.Phrases);
    private FlashSchedule _lapses;
    private readonly float[] _fogAt = new float[Billows], _speckAt = new float[Specks];
    private readonly List<Thing> _things = new(128);

    // per cast
    private int _seed, _lapseIndex;
    private float _hand, _spinDir, _spinRate, _spinPhase, _baseAngle, _cruise, _pullEvery, _pullPhase, _swirl, _turn, _kickDone, _appear;

    // the lapse in progress (_lapseAt < 0: none)
    private float _lapseAt = -100f, _lapseKick;
    private bool _swapped;

    public AmnesiaEffect() : base(DebuffKind.Amnesia, "Amnesia",
        "A pale spiral turns behind milky fog that murmurs what it forgets, and the fog thins toward the middle so you can still see. Now and then everything is lost: the spiral unwinds, and is wound anew.",
        11, "Amnesia",
        keywords: ["amnesia", "amnesiac", "forgot", "forgotten", "forgetful", "oblivion", "memory loss", "dazed"], envelope: true)
    {
    }

    protected override void Emit(EffectScene scene, in CastFrame f)
    {
        _f = f;
        if (f.Fresh) BeginCast(f.Seed);
        else if (f.Resized) _whispers.Clear();

        float t = StepLapse(in f);
        float bump = t >= 0f ? MathF.Sin(MathF.PI * t / LapseSecs) : 0f;
        float pull = 0.5f + 0.5f * MathF.Sin(MathF.Tau * f.Age / _pullEvery + _pullPhase);
        _appear = Prog(f.Age, 0.10f, 1.0f);
        LapseLook(t, out float reach, out float thin);

        float lapse = LapseSpeed(t);
        _tunnel.Advance(f.Dt, CruiseSpeed * _cruise * Intro(f.Age, 11f) * lapse);
        _swirl += (_hand * _spiral.Pitch - _swirl) * DrawHelpers.Saturate(1f - MathF.Exp(-f.Dt * 3f));

        // The spiral turns so its bands flow outward, the way the fog does; the fall's stalls and lurches carry over to it.
        float omega = SpiralFlow * MathF.Tau / (_spiral.BandArms * _spiral.Gap);
        _turn = (_turn - _hand * omega * Intro(f.Age, SpiralRush) * lapse * f.Dt) % MathF.Tau;

        // The fall breathes in, and a lapse thumps the whole view.
        _view.Update(in f, extraTilt: 0.03f * pull + 0.10f * bump, distanceScale: 1f - 0.04f * pull - 0.06f * bump);

        EmitTunnel(scene, SpinAt(f.Age, t), pull, bump, t, _appear * reach, thin);
        EmitAtmosphere(scene, pull, t);

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

        // Where each thing starts along the tunnel.
        _turn = DrawHelpers.Hash01(seed + 16) * MathF.Tau;
        _tunnel.Travel = DrawHelpers.Hash01(seed + 12) * _tunnel.Length;
        Spread(_fogAt, seed + 700, _tunnel.Length);
        Spread(_speckAt, seed + 800, _tunnel.Length);
    }

    private static void Spread(float[] offsets, int seed, float length)
    {
        for (int i = 0; i < offsets.Length; i++) offsets[i] = length * (i + DrawHelpers.Hash01(seed + i * 17)) / offsets.Length;
    }

    /// <summary>The spiral for the nth drawing of it: the fog's arm count and twist, and the spiral's own arm count (2 or 4) and gap between bands.</summary>
    private Spiral Roll(int index)
    {
        int s = _seed + index * 7919 + 100;
        return new Spiral
        {
            Arms = 3 + (int)(DrawHelpers.Hash01(s + 1) * 3f), Pitch = DrawHelpers.HashRange(s + 2, 0.00065f, 0.00105f),
            BandArms = DrawHelpers.Hash01(s + 3) < 0.65f ? 2 : 4, Gap = DrawHelpers.HashRange(s + 4, 0.075f, 0.105f),
        };
    }

    /// <summary>Starts, steps and ends a lapse: the words are let go, the spiral is swapped while it is unwound, and the fall lurches on. Returns seconds into it, or -1.</summary>
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

    /// <summary>The cast rushes in: 1 at rest, 1 + rush at the start, easing back.</summary>
    private static float Intro(float age, float rush) => 1f + rush * MathF.Exp(-age / 0.70f);

    /// <summary>A lapse as a multiple of the fall's speed: it stalls, then tears forward.</summary>
    private static float LapseSpeed(float lapseT)
    {
        if (lapseT < 0f) return 1f;
        return lapseT < SwapAt
            ? 1f - 0.95f * DrawHelpers.Smooth(lapseT / SwapAt)
            : 0.05f + 0.95f * DrawHelpers.Smooth((lapseT - SwapAt) / 0.9f) + 1.6f * MathF.Sin(MathF.PI * DrawHelpers.Saturate((lapseT - SwapAt) / (LapseSecs - SwapAt)));
    }

    /// <summary>What a lapse does to the picture: the spiral unwinds into the centre and the fog thins, then both come back from the rim and the middle.</summary>
    private static void LapseLook(float t, out float reach, out float thin)
    {
        reach = 1f; thin = 1f;
        if (t < 0f) return;
        if (t < SwapAt)
        {
            float d = DrawHelpers.Smooth(t / SwapAt);
            reach = 1f - d; thin = 1f - 0.8f * d;
            return;
        }
        float u = (t - SwapAt) / (LapseSecs - SwapAt);
        reach = DrawHelpers.Smooth(u / 0.8f); thin = 0.2f + 0.8f * DrawHelpers.Smooth(u);
    }

    /// <summary>How far the view has rolled: a steady turn, a slow surge, the whip of the cast and the kick of each lapse.</summary>
    private float SpinAt(float age, float lapseT)
    {
        float kick = lapseT >= 0f ? _lapseKick * DrawHelpers.Smooth(lapseT / LapseSecs) : 0f;
        return _baseAngle + _spinDir * (_spinRate * age + 0.25f * MathF.Sin(0.29f * age + _spinPhase) + IntroSpin * (1f - MathF.Exp(-age / 1.2f)) + _kickDone + kick);
    }

    private static float Prog(float age, float start, float secs) => DrawHelpers.Smooth(DrawHelpers.Saturate((age - start) / secs));

    /// <summary>Fades a thing in out of the far end and out again once it has passed the lens.</summary>
    private static float DepthFade(float near, float inEnd, float outStart)
        => DrawHelpers.Smooth(near / inEnd) * (1f - DrawHelpers.Smooth((near - outStart) / (1f - outStart)));

    // the tunnel and its atmosphere

    private void EmitTunnel(EffectScene scene, float spin, float pull, float bump, float lapseT, float reach, float thin)
    {
        ref readonly CastFrame f = ref _f;
        var v = _field;
        v.Flight = _tunnel; v.Cam = _view.Cam; v.Px = f.Px; v.Short = f.Short; v.Time = f.Time;
        v.Swirl = _swirl * (1f - 0.5f * DrawHelpers.Smooth(f.Leave)); v.Roll = spin;
        v.Breath = 1f - 0.02f * pull - 0.03f * bump; v.Alpha = f.Alpha * (1f - 0.35f * f.Leave);
        v.Arms = _spiral.Arms; v.Appear = _appear; v.Thin = thin;
        v.SpiralArms = _spiral.BandArms; v.SpiralPitch = _spiral.Gap; v.SpiralHand = _hand; v.SpiralTurn = _turn; v.SpiralReach = reach;

        // The cast lands, and a lapse comes back, as a ring of light racing down the tunnel from its far end.
        float ring = f.Age < 1.6f ? f.Age : lapseT >= SwapAt ? lapseT - SwapAt : -1f;
        v.Front = VortexField.FarHeight + _tunnel.Length * DrawHelpers.EaseOutCubic(DrawHelpers.Saturate(ring / 1.1f));
        v.FrontGain = ring >= 0f && ring < 1.6f ? 0.9f * MathF.Exp(-ring / 0.8f) : 0f;
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = f.Size, Alpha = 1f, State = v, ColorOverride = f.Color });
    }

    private void EmitAtmosphere(EffectScene scene, float pull, float lapseT)
    {
        ref readonly CastFrame f = ref _f;
        float intro = DrawHelpers.Smooth(f.Age / IntroSecs);

        // The edges bleach rather than darken, and the vignette breathes with the fall.
        scene.RequestVignette(MemoryColor.Vignette, 0.18f + 0.08f * intro, MathF.Min(1f, f.Alpha * 0.60f * intro * (0.90f + 0.15f * pull)), priority: 22, f.Color);

        // The flashes are at the rim only, so the middle of the screen is never whited out. The cast flash uses f.Gate so the fade-in can't swallow it; a lapse is a softer one as it comes back.
        float flash = f.Age < 1.4f ? MathF.Exp(-MathF.Pow(f.Age / 0.42f, 2f)) : 0f;
        scene.EdgeSpill(f.Size, f.Short * 0.22f, MemoryColor.Flash, 0.70f * flash * f.Gate, f.Color);
        float back = lapseT >= 0f ? MathF.Exp(-MathF.Pow((lapseT - SwapAt) / 0.35f, 2f)) : 0f;
        scene.EdgeSpill(f.Size, f.Short * 0.22f, MemoryColor.Flash, 0.40f * back * f.Alpha, f.Color);
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

            float k = f.Alpha * _appear * DepthFade(near, 0.20f, 0.85f) * 0.55f * (1f - 0.6f * f.Leave) * DrawHelpers.Smooth(0.22f, 0.57f, (at - f.Center).Length() / f.Short);
            if (k < 0.02f) continue;

            AddThing(hu, new ParticlePrimitive
            {
                Position = at, Size = DrawHelpers.HashRange(s + 3, 240f, 440f) * f.Px * scale * 0.75f, AgeRatio = 0.45f, Brightness = k,
                Seed = s, Role = PrimitiveRole.Mist, Rotation = DrawHelpers.Hash01(s + 4) * MathF.Tau, Variant = 60, ColorOverride = f.Color,
            });
        }
    }

    /// <summary>Specks of light streaming out of the far end: nothing says speed like dust going by.</summary>
    private void CollectSpecks()
    {
        ref readonly CastFrame f = ref _f;
        for (int i = 0; i < Specks; i++)
        {
            float hu = _tunnel.HeightOf(_speckAt[i], out int lap);
            float near = _tunnel.Near(hu);
            int s = _seed + 8000 + i * 7919 + lap * 104729;
            Vector2 at = _field.At(DrawHelpers.Hash01(s + 2) * MathF.Tau, hu, DrawHelpers.HashRange(s + 1, 0.10f, 1.0f), out float scale);

            float k = f.Alpha * _appear * DepthFade(near, 0.10f, 0.92f) * DrawHelpers.HashRange(s + 3, 0.35f, 0.90f) * (1f - f.Leave) * DrawHelpers.Smooth(0.22f, 0.52f, (at - f.Center).Length() / f.Short);
            if (k < 0.02f) continue;

            AddThing(hu, new ParticlePrimitive
            {
                Position = at, Size = DrawHelpers.HashRange(s + 5, 2.5f, 7f) * f.Px * scale, AgeRatio = 0.5f, Brightness = k,
                Seed = s, Role = PrimitiveRole.Mote, ColorOverride = f.Color,
            });
        }
    }
}

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

/// <summary>Amnesia: a pale spiral turning behind milky fog.</summary>
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
    private const float CruiseSpeed = 440f;                      // px/s down the tunnel at 1080p; a lap takes about 15 s
    private const float IntroSecs = 2.4f, IntroSpin = 2.4f;     // the cast rushes in and slows; the view whips round this many radians
    private const float FirstWhisper = 1.4f;

    // The spiral behind the fog: how fast it flows outward (fraction of the short side per second, matching the fall), and how fast the cast first sends it.
    private const float SpiralFlow = 0.045f, SpiralRush = 4f;

    // What drifts down the tunnel, and how strongly the words show through the fog over them.
    private const int Billows = 14, Specks = 70;
    private const float WhisperOpacity = 0.65f;

    /// <summary>Something drawn this frame at some height down the tunnel: collected, sorted far to near, then drawn.</summary>
    private struct Thing { public float Height; public ParticlePrimitive Particle; }

    private static readonly Comparison<Thing> FarToNear = (a, b) => a.Height.CompareTo(b.Height);

    /// <summary>The spiral for this cast: the fog's arm count and twist, and the spiral's own arm count and band gap.</summary>
    private struct Spiral { public int Arms, BandArms; public float Pitch, Gap; }
    private Spiral _spiral;

    private readonly TunnelFlight _tunnel = new(VortexField.FarHeight, VortexField.NearHeight);

    private CastFrame _f;
    private readonly VortexField _field = new();
    private readonly WhisperField _whispers = new(AmnesiaWhispers.Phrases, WhisperOpacity, belowVeil: true);
    private readonly float[] _fogAt = new float[Billows], _speckAt = new float[Specks];
    private readonly List<Thing> _things = new(128);

    // per cast
    private int _seed;
    private float _hand, _spinDir, _spinRate, _spinPhase, _baseAngle, _cruise, _pullEvery, _pullPhase, _turn, _appear;

    public AmnesiaEffect() : base(DebuffKind.Amnesia, "Amnesia",
        "A pale spiral turns behind milky fog that murmurs what it forgets, and the fog thins toward the middle so you can still see.",
        11, "Amnesia",
        keywords: ["amnesia", "amnesiac", "forgot", "forgotten", "forgetful", "oblivion", "memory loss", "dazed"], envelope: true)
    {
    }

    protected override void Emit(EffectScene scene, in CastFrame f)
    {
        _f = f;
        if (f.Fresh) BeginCast(f.Seed);
        else if (f.Resized) _whispers.Clear();

        float pull = 0.5f + 0.5f * MathF.Sin(MathF.Tau * f.Age / _pullEvery + _pullPhase);
        _appear = Prog(f.Age, 0.10f, 1.0f);

        _tunnel.Advance(f.Dt, CruiseSpeed * _cruise * Intro(f.Age, 11f));

        // The spiral turns so its bands flow outward, the way the fog does.
        float omega = SpiralFlow * MathF.Tau / (_spiral.BandArms * _spiral.Gap);
        _turn = (_turn - _hand * omega * Intro(f.Age, SpiralRush) * f.Dt) % MathF.Tau;

        // The fall breathes in.
        _view.Update(in f, extraTilt: 0.03f * pull, distanceScale: 1f - 0.04f * pull);

        EmitTunnel(scene, SpinAt(f.Age), pull);
        EmitAtmosphere(scene, pull);

        // Particles draw over every stroke, in the order they are submitted: the things far to near. The whispers hang under the fog.
        _things.Clear();
        CollectFog();
        CollectSpecks();
        _things.Sort(FarToNear);
        for (int i = 0; i < _things.Count; i++) scene.AddParticle(_things[i].Particle);
        _whispers.Update(in f);
        for (int depth = 2; depth >= 0; depth--) _whispers.Emit(scene, in f, depth);
    }

    // the cast

    private void BeginCast(int seed)
    {
        _seed = seed;
        _view.Begin(seed);
        _field.Begin(seed);
        _whispers.Begin(seed, FirstWhisper);

        _hand = DrawHelpers.Sign(seed + 1);
        _spinDir = DrawHelpers.Sign(seed + 2);
        _spinRate = DrawHelpers.HashRange(seed + 3, 0.10f, 0.20f);
        _spinPhase = DrawHelpers.Hash01(seed + 4) * MathF.Tau;
        _baseAngle = DrawHelpers.Hash01(seed + 5) * MathF.Tau;
        _cruise = DrawHelpers.HashRange(seed + 7, 0.85f, 1.15f);
        _pullEvery = DrawHelpers.HashRange(seed + 8, 5.5f, 8.0f);
        _pullPhase = DrawHelpers.Hash01(seed + 9) * MathF.Tau;

        _spiral = Roll();

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

    /// <summary>The cast's spiral: the fog's arm count and twist, and the spiral's own arm count (2 or 4) and gap between bands.</summary>
    private Spiral Roll()
    {
        int s = _seed + 100;
        return new Spiral
        {
            Arms = 3 + (int)(DrawHelpers.Hash01(s + 1) * 3f), Pitch = DrawHelpers.HashRange(s + 2, 0.00065f, 0.00105f),
            BandArms = DrawHelpers.Hash01(s + 3) < 0.65f ? 2 : 4, Gap = DrawHelpers.HashRange(s + 4, 0.075f, 0.105f),
        };
    }

    /// <summary>The cast rushes in: 1 at rest, 1 + rush at the start, easing back.</summary>
    private static float Intro(float age, float rush) => 1f + rush * MathF.Exp(-age / 0.70f);

    /// <summary>How far the view has rolled: a steady turn, a slow surge and the whip of the cast.</summary>
    private float SpinAt(float age)
        => _baseAngle + _spinDir * (_spinRate * age + 0.25f * MathF.Sin(0.29f * age + _spinPhase) + IntroSpin * (1f - MathF.Exp(-age / 1.2f)));

    private static float Prog(float age, float start, float secs) => DrawHelpers.Smooth(DrawHelpers.Saturate((age - start) / secs));

    /// <summary>Fades a thing in out of the far end and out again once it has passed the lens.</summary>
    private static float DepthFade(float near, float inEnd, float outStart)
        => DrawHelpers.Smooth(near / inEnd) * (1f - DrawHelpers.Smooth((near - outStart) / (1f - outStart)));

    // the tunnel and its atmosphere

    private void EmitTunnel(EffectScene scene, float spin, float pull)
    {
        ref readonly CastFrame f = ref _f;
        var v = _field;
        v.Flight = _tunnel; v.Cam = _view.Cam; v.Px = f.Px; v.Short = f.Short; v.Time = f.Time;
        v.Swirl = _hand * _spiral.Pitch * (1f - 0.5f * DrawHelpers.Smooth(f.Leave)); v.Roll = spin;
        v.Breath = 1f - 0.02f * pull; v.Alpha = f.Alpha * (1f - 0.35f * f.Leave);
        v.Arms = _spiral.Arms; v.Appear = _appear;
        v.SpiralArms = _spiral.BandArms; v.SpiralPitch = _spiral.Gap; v.SpiralHand = _hand; v.SpiralTurn = _turn; v.SpiralReach = _appear;

        // The cast lands as a ring of light racing down the tunnel from its far end.
        float ring = f.Age < 1.6f ? f.Age : -1f;
        v.Front = VortexField.FarHeight + _tunnel.Length * DrawHelpers.EaseOutCubic(DrawHelpers.Saturate(ring / 1.1f));
        v.FrontGain = ring >= 0f && ring < 1.6f ? 0.9f * MathF.Exp(-ring / 0.8f) : 0f;

        // The spiral, then the fog over it (the whispers are drawn between the two).
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = f.Size, Alpha = 1f, State = v, ColorOverride = f.Color });
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = f.Size, Alpha = 1f, State = v, ColorOverride = f.Color, Veil = true });
    }

    private void EmitAtmosphere(EffectScene scene, float pull)
    {
        ref readonly CastFrame f = ref _f;
        float intro = DrawHelpers.Smooth(f.Age / IntroSecs);

        // The edges bleach rather than darken, the vignette breathes with the fall, and it sits over the spiral and fog.
        scene.RequestVignette(MemoryColor.Vignette, 0.18f + 0.08f * intro, MathF.Min(1f, f.Alpha * 0.60f * intro * (0.90f + 0.15f * pull)), priority: 22, f.Color, over: true);
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

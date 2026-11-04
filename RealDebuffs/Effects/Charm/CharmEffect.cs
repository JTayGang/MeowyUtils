using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Everything the fog whispers. Edit freely: lowercase letters, spaces and ' , . ! ? - ♥ only (anything else is left blank).</summary>
public static class CharmWhispers
{
    public static readonly string[] Phrases =
    {
        "come closer", "stay with me", "look at me", "you're so lovely", "don't go", "just for me",
        "good", "shh", "yes, like that", "you belong here", "so beautiful", "let go",
        "only me", "forget them", "rest your eyes", "i see you", "be mine", "trust me",
        "darling", "stay", "closer", "listen", "so brave", "so good for me",
        "don't be afraid", "you're safe here", "hold still", "come here", "my favorite", "you're perfect",
        "stay a little longer", "give in", "that's it", "you were made for me", "sweet thing", "all mine ♥",
    };
}

/// <summary>Charm: a rose-scented fog closes in and whispers sweet nothings from every direction.</summary>
public sealed class CharmEffect : SceneEffect, IHasSwappableSlots
{
    // The fog is what a "made of X" phrase replaces.
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        Slot.Particle(PrimitiveRole.Mist, "Fog", "particle.rosefog", hero: true),
        Slot.Particle(PrimitiveRole.Whisper, "Whispers", "particle.whisper"),
        Slot.Particle(PrimitiveRole.Mote, "Stardust", "particle.stardust"),
        Slot.Particle(PrimitiveRole.Glint, "Sparkles", "particle.twinkle"),
    };

    // the cast
    private const float IntroSecs = 1.8f;  // the fog arrives and the vignette closes
    private const float FirstWhisper = 0.55f, FirstBeat = 1.6f;

    // fog: billows in three depths; each slot lives one cycle, then comes back somewhere else
    private readonly record struct Tier(int Count, float SizeMin, float SizeMax, float Speed, float Alpha, float EdgeBias, float PeriodMin, float PeriodMax, float Arrive, int Squash);
    private static readonly Tier[] Tiers =
    {
        new(12, 330f, 560f,  7f, 0.60f, 0.45f, 22f, 34f, 1.00f, 0),  // far: broad, slow, everywhere
        new(14, 250f, 440f, 15f, 0.85f, 0.80f, 17f, 27f, 0.80f, 0),  // mid: banked toward the edges
        new( 8, 320f, 540f, 38f, 0.42f, 1.00f, 11f, 18f, 0.65f, 56),  // near: wisps that sweep past the lens
    };
    private const int TierStride = 64;

    // sparkles: stars that flare and fade where they are, each slot coming back somewhere else
    private const int Sparkles = 22;

    private static readonly uint Haze = DrawHelpers.Pack(0.95f, 0.42f, 0.66f);

    private CastFrame _f;
    private readonly MoteField _motes = new(40, 650f, -420f, (8f, 26f), (10f, 40f), (1.6f, 4.2f));

    private readonly WhisperField _whispers = new(CharmWhispers.Phrases);

    // per-cast layout
    private int _seed;
    private float _density, _beatEvery;

    public CharmEffect() : base(DebuffKind.Charm, "Charm",
        "A rose-scented fog closes in and whispers sweet nothings from every direction, in slow script that comes into focus and fades back into the haze.",
        10, new Dictionary<string, float> { ["Charm"] = 1f, ["Charmed"] = 1f },
        keywords: ["charm", "charmed", "enthralled", "bewitched", "beguiled", "infatuated", "smitten", "entranced"], envelope: true)
    {
    }

    protected override void Emit(EffectScene scene, in CastFrame f)
    {
        _f = f;
        if (f.Fresh || f.Resized) { if (f.Fresh) BeginCast(f.Seed); else _whispers.Clear(); }

        float intro = DrawHelpers.Smooth(f.Age / IntroSecs);
        float beat = f.Age < FirstBeat ? 0f : Pulse((f.Age - FirstBeat) % _beatEvery);

        EmitAtmosphere(scene, intro, beat);
        _whispers.Update(in f);

        // Particles draw in the order they are submitted: far to near.
        EmitBloom(scene);
        EmitFog(scene, 0, beat);
        _whispers.Emit(scene, in f, 2);
        EmitFog(scene, 1, beat);
        _whispers.Emit(scene, in f, 1);
        var cam = new LayerCamera(f.Center, 1500f * f.Px, 0f, 0f);
        _motes.Emit(scene, in cam, 1500f * f.Px, f.Size, f.Px, f.Age, f.Alpha * 0.9f * intro * (1f - f.Leave), blurDim: 0.45f, twinkle: 0.5f, f.Color);
        EmitSparkles(scene, near: false, intro);
        _whispers.Emit(scene, in f, 0);
        EmitSparkles(scene, near: true, intro);
        EmitFog(scene, 2, beat);
    }

    private void BeginCast(int seed)
    {
        _seed = seed;
        _whispers.Begin(seed, FirstWhisper);
        _density = DrawHelpers.HashRange(seed + 6, 0.88f, 1.12f);
        _beatEvery = DrawHelpers.HashRange(seed + 10, 3.4f, 4.4f);
        _motes.Seed(seed);
    }

    // atmosphere

    /// <summary>A slow, deliberate heartbeat, 0..1: seconds since a beat began in, and two soft swells (the second half as strong) out; no sharp edges, so it breathes rather than flickers.</summary>
    internal static float Pulse(float since)
        => MathF.Max(Swell(since, 0.45f, 1.1f), 0.55f * Swell(since - 0.65f, 0.40f, 1.0f));

    private static float Swell(float t, float rise, float fall)
        => t <= 0f || t >= rise + fall ? 0f : t < rise ? DrawHelpers.Smooth(t / rise) : 1f - DrawHelpers.Smooth((t - rise) / fall);

    private void EmitAtmosphere(EffectScene scene, float intro, float beat)
    {
        ref readonly CastFrame f = ref _f;

        // The vignette closes in as the fog arrives and swells a little on every beat.
        scene.RequestVignette(CharmColor.Vignette, 0.16f + 0.22f * intro, MathF.Min(1f, f.Alpha * 0.72f * intro * (0.88f + 0.22f * beat)), priority: 22, f.Color);
        scene.Wash(f.Size, Haze, 0.12f * f.Alpha * intro * (1f + 0.40f * beat), f.Color);

        // The cast flash uses f.Gate so the fade-in can't swallow it.
        float flash = f.Age < 0.8f ? MathF.Exp(-f.Age / 0.17f) : 0f;
        scene.Wash(f.Size, CharmColor.Flash, 0.50f * flash * f.Gate, f.Color);
    }

    /// <summary>The cast: one great breath of haze that opens at the middle of the screen and thins as the fog takes over.</summary>
    private void EmitBloom(EffectScene scene)
    {
        ref readonly CastFrame f = ref _f;
        float life = f.Age / 2.4f;
        if (life >= 1f) return;
        scene.AddParticle(new ParticlePrimitive
        {
            Position = f.Center, Size = f.Short * 0.85f, AgeRatio = life, Brightness = 1.7f * f.Gate,
            Seed = _seed + 17, Role = PrimitiveRole.Mist, ColorOverride = f.Color,
        });
    }

    // fog

    private void EmitFog(EffectScene scene, int tier, float beat)
    {
        ref readonly CastFrame f = ref _f;
        ref readonly Tier T = ref Tiers[tier];
        float px = f.Px, halfW = f.Size.X * 0.5f, halfH = f.Size.Y * 0.5f;
        float arrive = DrawHelpers.Smooth(f.Age / (IntroSecs * T.Arrive + 0.2f));
        float level = f.Alpha * T.Alpha * _density * (1f - 0.6f * f.Leave) * (0.91f + 0.18f * beat);
        float edgeExp = 1f - 0.62f * T.EdgeBias;

        for (int i = 0; i < T.Count; i++)
        {
            int slot = _seed + (tier * TierStride + i) * 977;
            float period = DrawHelpers.HashRange(slot + 1, T.PeriodMin, T.PeriodMax);
            float u = f.Age / period + DrawHelpers.Hash01(slot + 2);
            int cycle = (int)MathF.Floor(u);
            float life = u - cycle;
            int cs = slot + cycle * 7919;

            // Where this cycle's billow forms: banked toward the edges, so the middle stays thinnest.
            float ux = DrawHelpers.Hash01(cs + 3) * 2f - 1f, uy = DrawHelpers.Hash01(cs + 4) * 2f - 1f;
            Vector2 home = f.Center + new Vector2(MathF.Sign(ux) * MathF.Pow(MathF.Abs(ux), edgeExp) * halfW * 1.05f,
                                                  MathF.Sign(uy) * MathF.Pow(MathF.Abs(uy), edgeExp) * halfH * 1.05f);

            // It drifts with the wind from the moment it formed.
            float bearing = _whispers.WindAngle + DrawHelpers.HashRange(cs + 6, -0.35f, 0.35f);
            float born = (cycle - DrawHelpers.Hash01(slot + 2)) * period;
            float run = (_whispers.Wind(f.Age) - _whispers.Wind(born)) * T.Speed * px;
            Vector2 at = home + new Vector2(MathF.Cos(bearing), MathF.Sin(bearing)) * run;

            // On arrival it comes in from beyond the edge; as the status lifts it is blown back out.
            Vector2 away = at - f.Center;
            float al = away.Length();
            away = al > 1f ? away / al : new Vector2(0f, -1f);
            at += away * (f.Short * (0.55f * (1f - arrive) * (1f - arrive) + 0.30f * f.Leave));

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = DrawHelpers.HashRange(cs + 5, T.SizeMin, T.SizeMax) * px * (0.55f + 0.45f * arrive) * (1f + 0.04f * beat),
                AgeRatio = life, Brightness = level * (0.7f + 0.3f * arrive), Seed = cs, Rotation = bearing, Variant = T.Squash, Role = PrimitiveRole.Mist, ColorOverride = f.Color,
            });
        }
    }

    // sparkles

    private void EmitSparkles(EffectScene scene, bool near, float intro)
    {
        ref readonly CastFrame f = ref _f;
        float level = f.Alpha * intro * (1f - f.Leave);
        for (int i = 0; i < Sparkles; i++)
        {
            int slot = _seed + 90001 + i * 1237;
            if ((DrawHelpers.Hash01(slot + 1) < 0.35f) != near) continue;

            float period = DrawHelpers.HashRange(slot + 2, 2.6f, 5.2f);
            float u = f.Age / period + DrawHelpers.Hash01(slot + 3);
            int cycle = (int)MathF.Floor(u);
            int cs = slot + cycle * 7919;

            // A quick flare and a slow fade.
            float flare = MathF.Sin(MathF.PI * MathF.Pow(u - cycle, 0.55f));
            scene.AddParticle(new ParticlePrimitive
            {
                Position = new Vector2(DrawHelpers.Hash01(cs + 4) * f.Size.X, DrawHelpers.Hash01(cs + 5) * f.Size.Y),
                Size = (near ? DrawHelpers.HashRange(cs + 6, 15f, 27f) : DrawHelpers.HashRange(cs + 6, 7f, 14f)) * f.Px,
                Brightness = flare * flare * level * (near ? 1f : 0.7f), Seed = cs, Role = PrimitiveRole.Glint, ColorOverride = f.Color,
            });
        }
    }
}

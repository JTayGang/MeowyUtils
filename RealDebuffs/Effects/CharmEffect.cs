using System.Numerics;
using RealDebuffs.Effects.Framework;
using RealDebuffs.Effects.Framework.Materials;

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
        new("Particle", PrimitiveRole.Mist,    "Fog",      "particle.rosefog", Hero: true),
        new("Particle", PrimitiveRole.Whisper, "Whispers", "particle.whisper"),
        new("Particle", PrimitiveRole.Mote,    "Stardust", "particle.stardust"),
        new("Particle", PrimitiveRole.Glint,   "Sparkles", "particle.twinkle"),
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

    // whispers
    private const int MaxWhispers = 7;
    private static readonly float[] DepthSize  = { 62f, 33f, 20f };   // x-height px at 1080p: near, mid, far
    private static readonly float[] DepthAlpha = { 1.00f, 0.88f, 0.60f };
    private static readonly (float Lo, float Hi)[] DepthLife = { (5.2f, 6.6f), (6.2f, 8.0f), (7.6f, 9.6f) };
    private static readonly float[] DepthDrift = { 16f, 10f, 6f };   // px a second downwind at 1080p: near words drift faster than far

    // sparkles: stars that flare and fade where they are, each slot coming back somewhere else
    private const int Sparkles = 22;

    private static readonly uint Haze = DrawHelpers.Pack(0.95f, 0.42f, 0.66f);

    private readonly CastTracker _cast = new(envelope: true);
    private CastFrame _f;
    private readonly MoteField _motes = new(40, 650f, -420f, (8f, 26f), (10f, 40f), (1.6f, 4.2f));

    private struct Whisper { public bool On; public int Phrase, Seed, Depth; public Vector2 At, Half, Breath; public float Size, Tilt, Spin, Born, Life; public int In, Out; }
    private readonly Whisper[] _w = new Whisper[MaxWhispers];
    private readonly int[] _bag = new int[CharmWhispers.Phrases.Length];
    private int _bagAt, _spawned, _lastIn, _lastOut;

    // per-cast layout
    private int _seed;
    private float _windAngle, _windSpeed, _gustA, _gustB, _density, _beatEvery;
    private float _nextWhisper, _nextChorus, _chorusEnds;
    private Vector2 _chorusAt;

    public CharmEffect() : base(DebuffKind.Charm, "Charm",
        "A rose-scented fog closes in and whispers sweet nothings from every direction, in slow script that comes into focus and fades back into the haze.",
        10, new Dictionary<string, float> { ["Charm"] = 1f, ["Charmed"] = 1f },
        keywords: ["charm", "charmed", "enthralled", "bewitched", "beguiled", "infatuated", "smitten", "entranced"])
    {
    }

    public override void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        var f = _f = _cast.Frame(screenSize, alpha, time, dt, colorOverride);
        if (f.Fresh || f.Resized) { if (f.Fresh) BeginCast(f.Seed); else Array.Clear(_w); }

        float intro = DrawHelpers.Smooth(f.Age / IntroSecs);
        float beat = f.Age < FirstBeat ? 0f : Pulse((f.Age - FirstBeat) % _beatEvery);

        EmitAtmosphere(scene, intro, beat);
        UpdateWhispers();

        // Particles draw in the order they are submitted: far to near.
        EmitBloom(scene);
        EmitFog(scene, 0, beat);
        EmitWhispers(scene, 2);
        EmitFog(scene, 1, beat);
        EmitWhispers(scene, 1);
        var cam = new LayerCamera(f.Center, 1500f * f.Px, 0f, 0f);
        _motes.Emit(scene, in cam, 1500f * f.Px, f.Size, f.Px, f.Age, f.Alpha * 0.9f * intro * (1f - f.Leave), blurDim: 0.45f, twinkle: 0.5f, f.Color);
        EmitSparkles(scene, near: false, intro);
        EmitWhispers(scene, 0);
        EmitSparkles(scene, near: true, intro);
        EmitFog(scene, 2, beat);
    }

    private void BeginCast(int seed)
    {
        _seed = seed;

        // The wind: mostly along the screen, either way, steady with slow gusts.
        _windAngle = DrawHelpers.HashRange(seed + 1, -0.8f, 0.8f) + (DrawHelpers.Hash01(seed + 2) < 0.5f ? 0f : MathF.PI);
        _windSpeed = DrawHelpers.HashRange(seed + 3, 0.75f, 1.30f);
        _gustA = DrawHelpers.Hash01(seed + 4) * MathF.Tau;
        _gustB = DrawHelpers.Hash01(seed + 5) * MathF.Tau;
        _density = DrawHelpers.HashRange(seed + 6, 0.88f, 1.12f);

        _beatEvery = DrawHelpers.HashRange(seed + 10, 3.4f, 4.4f);
        _motes.Seed(seed);

        Array.Clear(_w);
        for (int i = 0; i < _bag.Length; i++) _bag[i] = i;
        for (int i = _bag.Length - 1; i > 0; i--)
        {
            int j = (int)(DrawHelpers.Hash01(seed + 900 + i) * (i + 1));
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }
        _bagAt = 0; _spawned = 0; _lastIn = _lastOut = -1;

        _nextWhisper = FirstWhisper;
        _nextChorus = DrawHelpers.HashRange(seed + 7, 1.7f, 2.4f);
        _chorusEnds = 0f;
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

    /// <summary>How far the wind has carried something by age t (arbitrary units): a steady breeze with slow gusts, integrated.</summary>
    private float Wind(float t)
        => _windSpeed * (t - 0.35f / 0.37f * (MathF.Cos(0.37f * t + _gustA) - MathF.Cos(_gustA)) - 0.18f / 0.91f * (MathF.Cos(0.91f * t + _gustB) - MathF.Cos(_gustB)));

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
            float bearing = _windAngle + DrawHelpers.HashRange(cs + 6, -0.35f, 0.35f);
            float born = (cycle - DrawHelpers.Hash01(slot + 2)) * period;
            float run = (Wind(f.Age) - Wind(born)) * T.Speed * px;
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

    // whispers

    private void UpdateWhispers()
    {
        ref readonly CastFrame f = ref _f;
        float age = f.Age;
        for (int i = 0; i < MaxWhispers; i++)
            if (_w[i].On && age >= _w[i].Born + _w[i].Life) _w[i].On = false;

        if (f.Leaving) return;

        // Now and then several are spoken at once, close together, as if from one mouth.
        if (age >= _nextChorus && age >= _chorusEnds)
        {
            _chorusAt = new Vector2(DrawHelpers.HashRange(_seed + _spawned * 31 + 11, 0.2f, 0.8f) * f.Size.X, DrawHelpers.HashRange(_seed + _spawned * 31 + 12, 0.25f, 0.75f) * f.Size.Y);
            _chorusEnds = age + DrawHelpers.HashRange(_seed + _spawned * 31 + 13, 1.0f, 1.5f);
            _nextChorus = _chorusEnds + DrawHelpers.HashRange(_seed + _spawned * 31 + 14, 11f, 18f);
            _nextWhisper = MathF.Min(_nextWhisper, age);
        }

        if (age < _nextWhisper) return;

        bool chorus = age < _chorusEnds;
        Spawn(age, chorus);
        float gap = chorus ? DrawHelpers.HashRange(_seed + _spawned * 53 + 1, 0.30f, 0.55f)
                  : age < 3.5f ? DrawHelpers.HashRange(_seed + _spawned * 53 + 2, 0.60f, 1.05f)
                  : DrawHelpers.HashRange(_seed + _spawned * 53 + 3, 1.25f, 2.25f);
        _nextWhisper = age + gap;
    }

    private void Spawn(float age, bool chorus)
    {
        ref readonly CastFrame f = ref _f;
        int slot = Array.FindIndex(_w, w => !w.On);
        if (slot < 0) return;

        int s = _seed + _spawned * 6151;
        _spawned++;

        float r = DrawHelpers.Hash01(s + 1);
        int depth = r < 0.22f ? 0 : r < 0.72f ? 1 : 2;
        float size = DepthSize[depth] * DrawHelpers.HashRange(s + 2, 0.85f, 1.2f) * f.Px;
        int phrase = _bag[_bagAt++ % _bag.Length];
        int arrive = Fresh(ref _lastIn, ParticleWhisper.Arrivals, s + 8), depart = Fresh(ref _lastOut, ParticleWhisper.Departures, s + 9);
        float width = ScriptFont.Width(CharmWhispers.Phrases[phrase]) * size, height = ScriptFont.Height * size;
        float life = DrawHelpers.HashRange(s + 4, DepthLife[depth].Lo, DepthLife[depth].Hi);

        // Mostly nearly level, often askew, now and then steeply so; and turning slowly as it hangs.
        float lean = DrawHelpers.Hash01(s + 5);
        float tilt = (DrawHelpers.Hash01(s + 11) < 0.5f ? -1f : 1f) * (lean < 0.40f ? DrawHelpers.HashRange(s + 12, 0f, 0.08f) : lean < 0.85f ? DrawHelpers.HashRange(s + 12, 0.08f, 0.24f) : DrawHelpers.HashRange(s + 12, 0.24f, 0.42f));
        float spin = (DrawHelpers.Hash01(s + 13) < 0.5f ? -1f : 1f) * DrawHelpers.HashRange(s + 14, 0.008f, 0.022f);

        // The room it takes once tilted (at its most turned), and where the wind will have carried it by the end.
        float reach = MathF.Abs(tilt) + MathF.Abs(spin) * life;
        var half = new Vector2(0.5f * (width * MathF.Cos(reach) + height * MathF.Sin(reach)), 0.5f * (width * MathF.Sin(reach) + height * MathF.Cos(reach)));
        var downwind = new Vector2(MathF.Cos(_windAngle), MathF.Sin(_windAngle));
        Vector2 travel = downwind * (DepthDrift[depth] * _windSpeed * life * f.Px);

        // Somewhere clear: not on top of another phrase, and (mostly) wholly on screen, now and when it has drifted.
        float bleed = depth == 0 ? 0.12f : 0f;
        for (int attempt = 0; attempt < 14; attempt++)
        {
            Vector2 at;
            if (chorus)
                at = _chorusAt + new Vector2(DrawHelpers.HashRange(s + 20 + attempt, -0.26f, 0.26f) * f.Size.X, DrawHelpers.HashRange(s + 40 + attempt, -0.20f, 0.20f) * f.Size.Y);
            else
                at = new Vector2(DrawHelpers.Hash01(s + 20 + attempt) * f.Size.X, DrawHelpers.Hash01(s + 40 + attempt) * f.Size.Y);

            float mx = MathF.Max(0f, half.X - width * bleed) + 20f * f.Px, my = half.Y + 20f * f.Px;
            at = new Vector2(Fit(at.X, mx - MathF.Min(0f, travel.X), f.Size.X - mx - MathF.Max(0f, travel.X)),
                             Fit(at.Y, my - MathF.Min(0f, travel.Y), f.Size.Y - my - MathF.Max(0f, travel.Y)));

            bool clear = true;
            for (int j = 0; j < MaxWhispers && clear; j++)
            {
                ref readonly Whisper o = ref _w[j];
                if (!o.On) continue;
                clear = MathF.Abs(at.X - o.At.X) > (half.X + o.Half.X) * 0.9f || MathF.Abs(at.Y - o.At.Y) > (half.Y + o.Half.Y) * 1.05f;
            }
            if (!clear && attempt < 13) continue;
            if (!clear) return;

            float bearing = _windAngle + DrawHelpers.HashRange(s + 3, -0.5f, 0.5f);
            _w[slot] = new Whisper
            {
                On = true, Phrase = phrase, Seed = s, Depth = depth, At = at, Half = half, Size = size, Born = age, In = arrive, Out = depart,
                Life = life, Tilt = tilt, Spin = spin,
                Breath = new Vector2(MathF.Cos(bearing), MathF.Sin(bearing)) * DrawHelpers.HashRange(s + 6, 28f, 64f) * f.Px,
            };
            return;
        }
    }

    /// <summary>v kept within lo..hi; centred if there is no room.</summary>
    private static float Fit(float v, float lo, float hi) => hi <= lo ? 0.5f * (lo + hi) : Math.Clamp(v, lo, hi);

    /// <summary>A style that isn't the one just used, so neighbouring phrases never move alike.</summary>
    private static int Fresh(ref int last, int count, int seed)
    {
        int pick = (int)(DrawHelpers.Hash01(seed) * count) % count;
        if (pick == last) pick = (pick + 1 + (int)(DrawHelpers.Hash01(seed + 1) * (count - 1))) % count;
        return last = pick;
    }

    private void EmitWhispers(EffectScene scene, int depth)
    {
        ref readonly CastFrame f = ref _f;
        float intro = DrawHelpers.Smooth(f.Age / 0.8f);
        var downwind = new Vector2(MathF.Cos(_windAngle), MathF.Sin(_windAngle));
        for (int i = 0; i < MaxWhispers; i++)
        {
            ref readonly Whisper w = ref _w[i];
            if (!w.On || w.Depth != depth) continue;

            // Words hang in the fog: carried downwind with it, and rocking gently.
            float age = f.Age - w.Born;
            Vector2 drift = downwind * ((Wind(f.Age) - Wind(w.Born)) * DepthDrift[depth] * f.Px);
            Vector2 rock = new Vector2(MathF.Sin(age * 0.53f + w.Seed), MathF.Sin(age * 0.37f + w.Seed * 0.7f)) * (0.14f * w.Size);
            scene.AddParticle(new ParticlePrimitive
            {
                Position = w.At + drift + rock, Velocity = w.Breath, Size = w.Size, AgeRatio = age / w.Life,
                Brightness = f.Alpha * DepthAlpha[depth] * intro * (1f - f.Leave), Seed = w.Seed, Rotation = w.Tilt + w.Spin * age + 0.03f * MathF.Sin(age * 0.41f + w.Seed * 1.3f),
                Variant = (w.Phrase + 1) | (depth << ParticleWhisper.DepthShift) | (w.In << ParticleWhisper.ArriveShift) | (w.Out << ParticleWhisper.DepartShift), Role = PrimitiveRole.Whisper, ColorOverride = f.Color,
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

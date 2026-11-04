using System.Numerics;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>Phrases spoken into a breeze: where each hangs, how it arrives and leaves, and how it drifts. Begin per cast, Update per frame, Emit once per depth (0 near, 1 mid, 2 far).</summary>
internal sealed class WhisperField
{
    private const int Max = 7;
    private static readonly float[] DepthSize  = { 62f, 33f, 20f };   // x-height px at 1080p: near, mid, far
    private static readonly float[] DepthAlpha = { 1.00f, 0.88f, 0.60f };
    private static readonly (float Lo, float Hi)[] DepthLife = { (5.2f, 6.6f), (6.2f, 8.0f), (7.6f, 9.6f) };
    private static readonly float[] DepthDrift = { 16f, 10f, 6f };   // px a second downwind at 1080p: near words drift faster than far

    private struct Whisper { public bool On; public int Phrase, Seed, Depth; public Vector2 At, Half, Breath; public float Size, Tilt, Spin, Born, Life; public int In, Out; }

    private readonly string[] _phrases;
    private readonly Whisper[] _w = new Whisper[Max];
    private readonly int[] _bag;
    private int _seed, _bagAt, _spawned, _lastIn, _lastOut;
    private float _gustA, _gustB, _windSpeed;
    private float _nextWhisper, _nextChorus, _chorusEnds;
    private Vector2 _chorusAt;

    public WhisperField(string[] phrases)
    {
        _phrases = phrases;
        _bag = new int[phrases.Length];
    }

    /// <summary>The bearing of the breeze, radians.</summary>
    public float WindAngle { get; private set; }

    /// <summary>Drops every phrase on screen (after a resize, say).</summary>
    public void Clear() => Array.Clear(_w);

    /// <summary>Lets go of every phrase on screen: each begins to leave over the time it has left.</summary>
    public void LetGo(float age)
    {
        for (int i = 0; i < Max; i++)
            if (_w[i].On && age - _w[i].Born < 0.62f * _w[i].Life) _w[i].Born = age - 0.62f * _w[i].Life;
    }

    public void Begin(int seed, float firstAt)
    {
        _seed = seed;

        // The wind: mostly along the screen, either way, steady with slow gusts.
        WindAngle = DrawHelpers.HashRange(seed + 1, -0.8f, 0.8f) + (DrawHelpers.Hash01(seed + 2) < 0.5f ? 0f : MathF.PI);
        _windSpeed = DrawHelpers.HashRange(seed + 3, 0.75f, 1.30f);
        _gustA = DrawHelpers.Hash01(seed + 4) * MathF.Tau;
        _gustB = DrawHelpers.Hash01(seed + 5) * MathF.Tau;

        Array.Clear(_w);
        for (int i = 0; i < _bag.Length; i++) _bag[i] = i;
        for (int i = _bag.Length - 1; i > 0; i--)
        {
            int j = (int)(DrawHelpers.Hash01(seed + 900 + i) * (i + 1));
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }
        _bagAt = 0; _spawned = 0; _lastIn = _lastOut = -1;

        _nextWhisper = firstAt;
        _nextChorus = DrawHelpers.HashRange(seed + 7, 1.7f, 2.4f);
        _chorusEnds = 0f;
    }

    /// <summary>How far the wind has carried something by age t (arbitrary units): a steady breeze with slow gusts, integrated.</summary>
    public float Wind(float t)
        => _windSpeed * (t - 0.35f / 0.37f * (MathF.Cos(0.37f * t + _gustA) - MathF.Cos(_gustA)) - 0.18f / 0.91f * (MathF.Cos(0.91f * t + _gustB) - MathF.Cos(_gustB)));

    public void Update(in CastFrame f)
    {
        float age = f.Age;
        for (int i = 0; i < Max; i++)
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
        Spawn(in f, chorus);
        float gap = chorus ? DrawHelpers.HashRange(_seed + _spawned * 53 + 1, 0.30f, 0.55f)
                  : age < 3.5f ? DrawHelpers.HashRange(_seed + _spawned * 53 + 2, 0.60f, 1.05f)
                  : DrawHelpers.HashRange(_seed + _spawned * 53 + 3, 1.25f, 2.25f);
        _nextWhisper = age + gap;
    }

    private void Spawn(in CastFrame f, bool chorus)
    {
        float age = f.Age;
        int slot = Array.FindIndex(_w, w => !w.On);
        if (slot < 0) return;

        int s = _seed + _spawned * 6151;
        _spawned++;

        float r = DrawHelpers.Hash01(s + 1);
        int depth = r < 0.22f ? 0 : r < 0.72f ? 1 : 2;
        float size = DepthSize[depth] * DrawHelpers.HashRange(s + 2, 0.85f, 1.2f) * f.Px;
        int phrase = _bag[_bagAt++ % _bag.Length];
        int arrive = Fresh(ref _lastIn, ParticleWhisper.Arrivals, s + 8), depart = Fresh(ref _lastOut, ParticleWhisper.Departures, s + 9);
        float width = ScriptFont.Width(_phrases[phrase]) * size, height = ScriptFont.Height * size;
        float life = DrawHelpers.HashRange(s + 4, DepthLife[depth].Lo, DepthLife[depth].Hi);

        // Mostly nearly level, often askew, now and then steeply so; and turning slowly as it hangs.
        float lean = DrawHelpers.Hash01(s + 5);
        float tilt = (DrawHelpers.Hash01(s + 11) < 0.5f ? -1f : 1f) * (lean < 0.40f ? DrawHelpers.HashRange(s + 12, 0f, 0.08f) : lean < 0.85f ? DrawHelpers.HashRange(s + 12, 0.08f, 0.24f) : DrawHelpers.HashRange(s + 12, 0.24f, 0.42f));
        float spin = (DrawHelpers.Hash01(s + 13) < 0.5f ? -1f : 1f) * DrawHelpers.HashRange(s + 14, 0.008f, 0.022f);

        // The room it takes once tilted (at its most turned), and where the wind will have carried it by the end.
        float reach = MathF.Abs(tilt) + MathF.Abs(spin) * life;
        var half = new Vector2(0.5f * (width * MathF.Cos(reach) + height * MathF.Sin(reach)), 0.5f * (width * MathF.Sin(reach) + height * MathF.Cos(reach)));
        var downwind = new Vector2(MathF.Cos(WindAngle), MathF.Sin(WindAngle));
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
            for (int j = 0; j < Max && clear; j++)
            {
                ref readonly Whisper o = ref _w[j];
                if (!o.On) continue;
                clear = MathF.Abs(at.X - o.At.X) > (half.X + o.Half.X) * 0.9f || MathF.Abs(at.Y - o.At.Y) > (half.Y + o.Half.Y) * 1.05f;
            }
            if (!clear && attempt < 13) continue;
            if (!clear) return;

            float bearing = WindAngle + DrawHelpers.HashRange(s + 3, -0.5f, 0.5f);
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

    public void Emit(EffectScene scene, in CastFrame f, int depth)
    {
        float intro = DrawHelpers.Smooth(f.Age / 0.8f);
        var downwind = new Vector2(MathF.Cos(WindAngle), MathF.Sin(WindAngle));
        for (int i = 0; i < Max; i++)
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
}

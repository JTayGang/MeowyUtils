using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Dropsy: water leaking, spraying and pouring in from the top of the screen into a shallow pool.</summary>
public sealed class DropsyEffect : SceneEffect, IHasSwappableSlots
{
    // One liquid for everything: swap it ("made of blood") and the streams, pool, splashes and drips all change with it.
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Particle", PrimitiveRole.Goop,       "Liquid",           "particle.water", Hero: true),
        new("Region",   PrimitiveRole.MainStroke, "Pool and streams", "region.liquid", "EdgeGlow"),
        new("Region",   PrimitiveRole.MainStroke, "Flash",            "region.flat-fill", "FlatFill"),
    };

    private const float PoolDepth = 0.15f;  // of the screen height
    private const float SideZone = 0.20f;  // leaks may also start this far down the side edges
    private const int Leaks = 9, Drips = 12;
    private const float FillSecs = 3.2f;  // the pool takes this long to rise, once water lands

    private enum Size { Gusher, Medium, Pinhole }

    /// <summary>One leak: where it starts, how hard it is pushed, and how it opens, runs and sputters out.</summary>
    private sealed class Leak
    {
        public LiquidJet Jet = null!;
        public bool Live;
        public Vector2 At;
        public bool Side;
        public float Born, Life, Base, Gush, GushSecs;
        public float Speed, Angle, Wander, WanderW;  // 1080p px/s, radians
        public float W1, P1, W2, P2, FlutterW, GlugEvery, GlugAt, GlugLen;
        public float Dribble;  // seconds of drip it leaves behind
    }

    private struct Burst { public Vector2 At; public float Angle, Spread, Speed, Radius, Left, Owed, Rate; }

    private readonly LiquidField _field = new();
    private readonly Leak[] _leaks = new Leak[Leaks];
    private readonly float[] _dripEnd = new float[Drips];
    private readonly CastTracker _cast = new(envelope: true);
    private Burst _burst;
    private float _nextLeak, _nextEvent, _nextDrip, _fill, _breath, _contact;
    private int _target, _spawned;

    public DropsyEffect() : base(DebuffKind.Dropsy, "Dropsy",
        "Water leaks, sprays and pours in from the top of the screen, running and dripping into a shallow pool that splashes, foams and ripples.",
        9, "Dropsy",
        keywords: ["dropsy", "drowning", "drenched", "waterlogged", "flooded"])
    {
        for (int i = 0; i < Leaks; i++) _leaks[i] = new Leak();
    }

    public override void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        var f = _cast.Frame(screenSize, alpha, time, dt, colorOverride);
        var liquid = Liquid.Resolve(scene, Kind, PrimitiveRole.Goop);

        if (f.Fresh || f.Resized) Lay(scene, in f, liquid);
        _field.SetLiquid(liquid);
        _field.Color = f.Color;

        Drive(in f);
        for (float left = f.Dt; left > 1e-4f;)
        {
            float h = MathF.Min(left, 1f / 30f);
            _field.Step(h);
            left -= h;
        }

        Mood(scene, in f, liquid);
        _field.Emit(scene, f.Alpha);
    }

    /// <summary>Starts a cast: how busy it is, and the pool's depth.</summary>
    private void Lay(EffectScene scene, in CastFrame f, Liquid liquid)
    {
        _field.Begin(scene.CurrentOwner, f.Size, liquid, f.Seed, Leaks, Drips);
        for (int i = 0; i < Leaks; i++) { _leaks[i].Jet = _field.Jets[i]; _leaks[i].Live = false; }
        Array.Clear(_dripEnd);
        _burst = default;
        _spawned = 0;
        _target = 4 + (int)(_field.Rand() * 3f);
        _fill = _field.Rand(0.88f, 1.12f);
        _breath = _field.Rand(0f, MathF.Tau);
        _nextLeak = 0f;
        _nextEvent = _field.Rand(2.4f, 4f);
        _nextDrip = 0f;
        _contact = -1f;
    }

    /// <summary>Runs the cast: opens and closes leaks, sets each one's flow and aim, fires the odd spray, and sets the pool's depth.</summary>
    private void Drive(in CastFrame f)
    {
        float t = f.Age, dt = f.Dt, keep = 1f - f.Leave;
        bool open = f.Leave < 0.01f;

        // new leaks open at random places whenever fewer than the cast's share are running
        int live = 0;
        foreach (var l in _leaks) if (l.Live) live++;
        if (open && live < _target + (t < 2f ? 1 : 0) && t >= _nextLeak)
        {
            if (Open(in f)) _nextLeak = t + (_spawned < 3 ? _field.Rand(0.03f, 0.15f) : t < 1.4f ? _field.Rand(0.12f, 0.35f) : _field.Rand(0.7f, 2.2f));
        }

        foreach (var l in _leaks) if (l.Live) Run(l, in f, keep);

        for (int i = 0; i < Drips; i++) Drip(i, in f, open);
        if (open) Events(in f);
        Spritz(dt);

        // the pool starts to rise when the first water reaches the bottom, so it looks filled by what is pouring in
        if (_contact < 0f)
        {
            foreach (var l in _leaks) if (l.Jet.Wet) { _contact = t; break; }
            if (_contact < 0f && t > 1.5f) _contact = t;  // nothing landed: do not hold the pool back
        }
        float rise = _contact < 0f ? 0f : 1f - MathF.Exp(-(t - _contact) / (FillSecs * 0.45f));
        _field.Level = f.Size.Y * PoolDepth * _fill * rise * (0.55f + 0.45f * keep) * (1f + 0.04f * MathF.Sin(0.7f * t + _breath));
        _field.Chop = 0.35f + 1.2f * MathF.Exp(-t / 1.8f);
    }

    /// <summary>Where a leak can start: anywhere along the top, favouring the corners, or the top fifth of a side edge. down is +1 if the water should head right.</summary>
    private Vector2 Origin(in CastFrame f, bool corner, out bool side, out float toward)
    {
        float W = f.Size.X, H = f.Size.Y, px = f.Px;
        if (!corner && _field.Rand() < 0.26f)
        {
            bool left = _field.Rand() < 0.5f;
            side = true; toward = left ? 1f : -1f;
            return new Vector2(left ? -24f * px : W + 24f * px, H * _field.Rand(0.02f, SideZone));
        }
        side = false;
        float x = W * (corner || _field.Rand() < 0.62f ? (_field.Rand() < 0.5f ? _field.Rand(0.01f, 0.28f) : _field.Rand(0.72f, 0.99f)) : _field.Rand(0.02f, 0.98f));
        toward = x < W * 0.5f ? 1f : -1f;
        return new Vector2(x, -24f * px);
    }

    private bool Open(in CastFrame f)
    {
        Leak? l = null;
        foreach (var c in _leaks) if (!c.Live && c.Jet.Idle) { l = c; break; }
        if (l == null) return false;

        // the first two are always big pours, so the opening is a gush
        bool first = _spawned < 2;
        float roll = _field.Rand();
        var size = first || roll < 0.22f ? Size.Gusher : roll < 0.62f ? Size.Medium : Size.Pinhole;
        l.At = Origin(in f, first, out l.Side, out float toward);

        float radius, limit;
        switch (size)
        {
            case Size.Gusher: radius = _field.Rand(34f, 56f); limit = 0.28f; l.Life = _field.Rand(6f, 13f); l.Gush = _field.Rand(1.4f, 2.2f); l.Wander = 0.03f; break;
            case Size.Medium: radius = _field.Rand(9f, 18f); limit = 0.75f; l.Life = _field.Rand(3.5f, 8f); l.Gush = _field.Rand(0.9f, 1.4f); l.Wander = 0.08f; break;
            default: radius = _field.Rand(2.5f, 6f); limit = 1.25f; l.Life = _field.Rand(1.2f, 4.5f); l.Gush = _field.Rand(0.5f, 1.0f); l.Wander = 0.16f; break;
        }

        // pressure: the smaller the hole, the harder it is pushed, so thin leaks shoot further than fat ones
        l.Speed = Math.Clamp(1150f * MathF.Pow(6f / radius, 0.7f) * _field.Rand(0.85f, 1.15f), size == Size.Gusher ? 460f : 240f, 1900f);
        l.Angle = l.Side ? toward * (MathF.PI * 0.5f + _field.Rand(-0.45f, 0.35f)) : toward * _field.Rand(-0.2f, 1f) * limit;

        l.Born = f.Age; l.Live = true; _spawned++;
        l.Base = _field.Rand(0.65f, 1f);
        l.GushSecs = _field.Rand(0.5f, 0.9f);
        l.WanderW = _field.Rand(0.8f, 2.2f);
        l.W1 = _field.Rand(0.45f, 0.9f); l.P1 = _field.Rand(0f, MathF.Tau); l.W2 = _field.Rand(1.4f, 2.6f); l.P2 = _field.Rand(0f, MathF.Tau);
        l.FlutterW = _field.Rand(5f, 9f);
        l.GlugEvery = _field.Rand(2.2f, 4.5f); l.GlugAt = _field.Rand(0f, 3f); l.GlugLen = _field.Rand(0.30f, 0.55f);
        l.Dribble = _field.Rand() < 0.7f ? _field.Rand(3f, 6.5f) : 0f;

        _field.Launch(l.Jet, l.At, l.Speed, l.Angle, radius, f.Seed + _spawned * 977);
        l.Jet.Churn = size == Size.Gusher ? _field.Rand(1f, 1.3f) : _field.Rand(0.7f, 1.1f);
        l.Jet.Flow = 0.01f;
        return true;
    }

    /// <summary>One leak's flow and aim this frame; it sputters out at the end and may leave a dribble.</summary>
    private void Run(Leak l, in CastFrame f, float keep)
    {
        float age = f.Age - l.Born, rem = l.Life - age;
        if (rem < -0.15f || keep < 0.02f)
        {
            l.Live = false; l.Jet.Flow = 0f;
            if (l.Dribble > 0f && keep > 0.5f) Dribble(l, in f);
            return;
        }

        float swell = 1f + 0.13f * MathF.Sin(l.W1 * age + l.P1) + 0.07f * MathF.Sin(l.W2 * age + l.P2);
        float flutter = 1f + 0.20f * l.Jet.Churn * Noise.Value(age * l.FlutterW + l.P1, l.Jet.Seed * 0.1f);  // the quick lumpiness of a ragged flow
        float glug = 1f - 0.34f * Bump(Cycle(age - l.GlugAt, l.GlugEvery) / l.GlugLen);  // air gulped in the pipe
        float gush = 1f + l.Gush * MathF.Exp(-age / l.GushSecs);
        float taper = DrawHelpers.Smooth(rem / 1.6f);
        float sputter = rem < 1.8f ? 0.30f + 0.70f * DrawHelpers.Smooth(0f, 0.55f, 0.5f + 0.5f * MathF.Sin(age * (12f + 8f * (1.8f - rem)) + l.P2)) : 1f;
        float flow = l.Base * DrawHelpers.Smooth(age / 0.012f) * gush * swell * flutter * glug * taper * sputter * keep * keep;

        l.Jet.Flow = MathF.Max(flow, 0.003f);
        l.Jet.Speed = l.Speed * f.Px * (0.97f + 0.03f * swell);  // pressure holds steady, so parcels launched together fly together
        l.Jet.Angle = l.Angle + l.Wander * (Noise.Value(age * l.WanderW + l.P1, 5.5f) + 0.5f * MathF.Sin(age * l.WanderW * 2.3f + l.P2));
    }

    /// <summary>When a leak stops, what is left in the pipe drips from its mouth for a while.</summary>
    private void Dribble(Leak l, in CastFrame f)
    {
        for (int i = 0; i < Drips; i++)
        {
            if (_field.Seeps[i].Live || _field.Seeps[i].Fade > 0.05f) continue;
            float px = f.Px;
            var at = l.Side ? new Vector2(l.At.X < 0f ? 3f * px : f.Size.X - 3f * px, l.At.Y) : new Vector2(l.At.X, 0f);
            _field.Place(_field.Seeps[i], at, _field.Rand(0.9f, 1.7f), _field.Rand(1.2f, 2.2f), f.Seed + 400 + _spawned * 23 + i);
            _dripEnd[i] = f.Age + l.Dribble;
            return;
        }
    }

    /// <summary>Ceiling drips come and go: each runs for a while, then another starts somewhere else.</summary>
    private void Drip(int i, in CastFrame f, bool open)
    {
        var s = _field.Seeps[i];
        s.Boost = 1f + 1.2f * MathF.Exp(-f.Age / 2.5f);
        if (s.Live)
        {
            if (f.Age > _dripEnd[i] || !open) s.Live = false;
            return;
        }
        if (!open || s.Fade > 0.05f || f.Age < _nextDrip) return;

        int running = 0;
        foreach (var o in _field.Seeps) if (o.Live) running++;
        if (running >= 7) return;

        var at = Origin(in f, false, out bool side, out _);
        at = side ? new Vector2(at.X < 0f ? 3f * f.Px : f.Size.X - 3f * f.Px, at.Y) : new Vector2(at.X, 0f);
        _field.Place(s, at, _field.Rand(0.8f, 1.6f), _field.Rand(0.9f, 2.0f), f.Seed + 400 + i * 23 + (int)(f.Age * 10f));
        _dripEnd[i] = f.Age + _field.Rand(4f, 12f);
        _nextDrip = f.Age + _field.Rand(0.3f, 1.4f);
    }

    /// <summary>Now and then: a spritz from a crack, a fat gout falling, or a splat thrown at the lens.</summary>
    private void Events(in CastFrame f)
    {
        if (f.Age < _nextEvent) return;
        _nextEvent = f.Age + _field.Rand(2.2f, 5.5f);
        float px = f.Px, roll = _field.Rand();

        if (roll < 0.45f)
        {
            var at = Origin(in f, false, out bool side, out float toward);
            _burst = new Burst
            {
                At = at, Spread = _field.Rand(0.15f, 0.4f), Speed = _field.Rand(800f, 1700f), Radius = _field.Rand(2f, 4.2f),
                Angle = side ? toward * (MathF.PI * 0.5f + _field.Rand(-0.45f, 0.35f)) : toward * _field.Rand(-0.2f, 1.1f),
                Left = _field.Rand(0.18f, 0.45f), Rate = _field.Rand(45f, 110f),
            };
            _burst.Rate /= _burst.Left;
        }
        else if (roll < 0.70f)
        {
            // a fat gout falls, with a few small ones in its wake
            var at = Origin(in f, false, out _, out float toward);
            float vx = toward * _field.Rand(40f, 260f), vy = _field.Rand(150f, 500f);
            _field.Fling(at, new Vector2(vx, vy), _field.Rand(5.5f, 11f));
            for (int k = 0, n = (int)_field.Rand(2f, 6f); k < n; k++)
                _field.Fling(at + new Vector2(_field.Rand(-12f, 12f) * px, -_field.Rand(20f, 90f) * px), new Vector2(vx * _field.Rand(0.8f, 1.1f), vy * _field.Rand(0.8f, 1.05f)), _field.Rand(1.5f, 3.2f));
        }
        else
            _field.Splat(new Vector2(f.Size.X * _field.Rand(0.05f, 0.95f), f.Size.Y * _field.Rand(0.05f, 0.6f)), _field.Rand(3.5f, 6.5f), (int)_field.Rand(6f, 13f));
    }

    private void Spritz(float dt)
    {
        if (_burst.Left <= 0f) return;
        float step = MathF.Min(dt, _burst.Left);
        _burst.Left -= step;
        _burst.Owed += _burst.Rate * step;
        int n = (int)_burst.Owed;
        _burst.Owed -= n;
        if (n > 0) _field.Spray(_burst.At, _burst.Angle, _burst.Spread, _burst.Speed, n, _burst.Radius);
    }

    private static float Cycle(float t, float period) => t - period * MathF.Floor(t / period);
    private static float Bump(float u) => u is > 0f and < 1f ? MathF.Sin(MathF.PI * u) : 0f;

    /// <summary>A dark vignette, and a flash as the water arrives, in the liquid's own colour.</summary>
    private void Mood(EffectScene scene, in CastFrame f, Liquid liquid)
    {
        var look = liquid.Look;
        Vector3 deep = Matcap.ToDisplay(look.Deep * 0.55f), pale = Matcap.ToDisplay(look.Thin * 1.5f);
        float ramp = DrawHelpers.Saturate(f.Age / 2.4f);
        scene.RequestVignette(DrawHelpers.Pack(deep.X * 0.15f, deep.Y * 0.15f, deep.Z * 0.15f), 0.14f, f.Alpha * 0.85f * ramp, priority: 20, f.Color);
        scene.Wash(f.Size, DrawHelpers.Pack(pale.X, pale.Y, pale.Z), 0.30f * f.Gate * MathF.Exp(-f.Age / 0.22f), f.Color);
    }
}

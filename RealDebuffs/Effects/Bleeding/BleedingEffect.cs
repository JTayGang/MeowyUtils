using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Bleeding: a claw rake of blood across the lens that pulses with a heartbeat, spurting, dripping and spattering beads that run down the glass.</summary>
public sealed class BleedingEffect : SceneEffect, IHasSwappableSlots
{
    // The cuts are what a "made of X" phrase replaces; a particle in that phrase swaps the blood itself.
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        Slot.Stroke(PrimitiveRole.MainStroke, "Cuts", "stroke.gash", hero: true),
        Slot.Particle(PrimitiveRole.Goop, "Blood", "particle.blood", modifier: true),
        Slot.Region(RegionKind.EdgeGlow, "Spatter and runs", "region.liquid"),
    };

    private const int MaxCuts = 4, CutPoints = 30;
    private static readonly uint Gloom = DrawHelpers.Pack(0.12f, 0.000f, 0.008f);

    // the pen: when the first cut starts, and the gap before the next
    private const float PenStart = 0.10f, PenGap = 0.10f;

    // the pulse: the first beat lands after the cuts, and the bleed eases to this share of its strength
    private const float FirstBeat = 1.0f, DrainFloor = 0.38f, DrainSecs = 12f;

    private readonly StrandPath[] _cut = new StrandPath[MaxCuts];
    private readonly float[] _width = new float[MaxCuts], _start = new float[MaxCuts], _secs = new float[MaxCuts];
    private readonly bool[] _struck = new bool[MaxCuts];
    private int _cuts;

    private readonly LiquidField _field = new();
    private readonly Heartbeat _heart = new();
    private bool _high;
    private int _beats;
    private float _nextSplat;

    public BleedingEffect() : base(DebuffKind.Bleeding, "Bleeding",
        "Claw cuts of blood open across the screen and pulse with a heartbeat: blood spurts, drips and spatters, and beads of it run down the glass.",
        6, "Bleeding",
        keywords: ["bleed", "bleeding", "hemorrhage"], envelope: true)
    {
        for (int i = 0; i < MaxCuts; i++) _cut[i] = new StrandPath(CutPoints);
    }

    protected override void Emit(EffectScene scene, in CastFrame f)
    {
        var liquid = Liquid.Resolve(scene, Kind, PrimitiveRole.Goop);

        if (f.Fresh || f.Resized)
        {
            _field.Begin(scene.CurrentOwner, f.Size, liquid, f.Seed, 0, 0);
            Layout(in f);
        }
        if (f.Fresh) Begin(in f);
        _field.SetLiquid(liquid);
        _field.Color = f.Color;

        float beat = _heart.Level(f.Age, 0f);
        float drain = DrainFloor + (1f - DrainFloor) * MathF.Exp(-f.Age / DrainSecs);

        EmitCuts(scene, in f, beat, drain);
        Pulse(in f, beat, drain);
        Spatter(in f);

        for (float left = f.Dt; left > 1e-4f;)
        {
            float h = MathF.Min(left, 1f / 30f);
            _field.Step(h);
            left -= h;
        }

        // The vignette eases in and swells a little on each beat.
        float castIn = DrawHelpers.Smooth(f.Age / 0.9f);
        scene.RequestVignette(Gloom, 0.15f + 0.03f * beat, MathF.Min(1f, f.Alpha * castIn * (0.55f + 0.30f * beat * drain)), priority: 14, f.Color);

        _field.Emit(scene, f.Alpha);
    }

    private void Begin(in CastFrame f)
    {
        Array.Clear(_struck);
        _heart.Begin(f.Seed, FirstBeat);
        _high = false;
        _beats = 0;
        _nextSplat = 3.5f + 3f * DrawHelpers.Hash01(f.Seed + 9);
    }

    /// <summary>Lays the rake from the top corner on one side: a few near-parallel cuts, bowed alike, ending short of the middle.</summary>
    private void Layout(in CastFrame f)
    {
        int seed = f.Seed;
        float W = f.Size.X, H = f.Size.Y, px = f.Px;
        float r = DrawHelpers.Hash01(seed + 1);
        _cuts = r < 0.25f ? 2 : r < 0.80f ? 3 : 4;
        bool right = DrawHelpers.Sign(seed + 2) > 0f;

        // As seen from the left side; the right side is its mirror.
        Vector2 a = new(W * DrawHelpers.HashRange(seed + 3, 0.00f, 0.08f), H * DrawHelpers.HashRange(seed + 4, -0.04f, 0.02f));
        Vector2 b = new(W * DrawHelpers.HashRange(seed + 5, 0.34f, 0.46f), H * DrawHelpers.HashRange(seed + 6, 0.60f, 0.80f));
        float len = Vector2.Distance(a, b);
        Vector2 u = (b - a) / len, n = new(-u.Y, u.X);  // n points toward the screen edge
        float spacing = 0.05f * f.Short * DrawHelpers.HashRange(seed + 7, 0.85f, 1.15f);
        float bow = len * DrawHelpers.HashRange(seed + 8, -0.07f, 0.07f);

        for (int k = 0; k < _cuts; k++)
        {
            int s = seed + 100 + k * 37;
            float centre = k - 0.5f * (_cuts - 1);
            float off = (centre + 0.5f + DrawHelpers.HashRange(s + 1, -0.12f, 0.12f)) * spacing;
            Vector2 p0 = a + u * (len * DrawHelpers.HashRange(s + 2, 0f, 0.10f)) + n * off;
            Vector2 p2 = b - u * (len * DrawHelpers.HashRange(s + 3, 0f, 0.16f)) + n * off;
            Vector2 p1 = (a + b) * 0.5f + n * (off + bow + len * DrawHelpers.HashRange(s + 4, -0.012f, 0.012f));

            var path = _cut[k];
            for (int i = 0; i < CutPoints; i++)
            {
                float t = i / (CutPoints - 1f), v = 1f - t;
                Vector2 p = v * v * p0 + 2f * v * t * p1 + t * t * p2;
                if (right) p.X = W - p.X;
                path.Points[i] = p;
            }
            path.Count = CutPoints;
            path.BuildArc();

            float deep = 1f - MathF.Abs(centre) / (0.5f * _cuts);  // the middle cuts are the widest
            _width[k] = px * (20f + 22f * deep) * DrawHelpers.HashRange(s + 5, 0.85f, 1.15f);
            _start[k] = PenStart + PenGap * k;
            _secs[k] = DrawHelpers.HashRange(s + 6, 0.30f, 0.42f);
        }
    }

    private float Drawn(float age, int k) => DrawHelpers.Smooth(DrawHelpers.Saturate((age - _start[k]) / _secs[k]));

    private void EmitCuts(EffectScene scene, in CastFrame f, float beat, float drain)
    {
        for (int k = 0; k < _cuts; k++)
        {
            float reveal = Drawn(f.Age, k);
            if (reveal <= 0.001f) continue;

            // As the pen lands, the blow throws a little blood and spatters the lens beside the cut.
            if (!_struck[k] && f.Age >= _start[k] + 0.04f)
            {
                _struck[k] = true;
                Strike(scene, in f, k);
            }

            scene.AddStroke(new StrokePrimitive
            {
                Path = _cut[k], Role = PrimitiveRole.MainStroke, Reveal = reveal, WidthHint = _width[k],
                Brightness = f.Alpha, Seed = f.Seed + 31 + k * 977, Phase = k * 0.7f, Agitation = beat * drain,
                Closed = false, ColorOverride = f.Color,
            });
        }
    }

    private void Strike(EffectScene scene, in CastFrame f, int k)
    {
        var path = _cut[k];
        path.SampleAtArc(path.Length * 0.12f, out Vector2 at, out Vector2 tan);
        Vector2 down = Downhill(tan);

        scene.AddImpact(new ImpactPrimitive
        {
            StrokeRole = PrimitiveRole.MainStroke, Position = at, Direction = down, Strength = 0.8f,
            Brightness = f.Alpha, Seed = f.Seed + k * 977 + 5, ColorOverride = f.Color,
        });
        _field.Splat(at + down * (_field.Rand(20f, 46f) * f.Px), Bead(), (int)_field.Rand(6f, 10f));
    }

    /// <summary>A bead big enough to run down the glass: a thicker liquid clings until it is larger.</summary>
    private float Bead() => (2.8f + 6f * _field.Liquid.Phys.Viscosity) * _field.Rand(1.15f, 1.7f);

    /// <summary>The side of a cut that faces down, as a unit vector.</summary>
    private static Vector2 Downhill(Vector2 tan)
    {
        Vector2 d = new(tan.Y, -tan.X);
        return d.Y < 0f ? -d : d;
    }

    /// <summary>On each lub a cut that is fully open spurts: a puff of droplets and now and then a fat gout, weaker as the bleed goes on.</summary>
    private void Pulse(in CastFrame f, float beat, float drain)
    {
        bool high = beat > 0.5f;
        bool lub = high && !_high;
        _high = high;
        if (!lub || f.Leave >= 0.01f) return;

        int k = _beats++ % _cuts;
        if (Drawn(f.Age, k) < 0.95f) return;

        float power = drain * _field.Rand(0.8f, 1.2f);
        var path = _cut[k];
        path.SampleAtArc(path.Length * _field.Rand(0.30f, 0.65f), out Vector2 at, out Vector2 tan);
        Vector2 down = Downhill(tan);

        // Droplets stay above the size the liquid draws as a bare glint, so each one reads as blood.
        float angle = MathF.Atan2(down.X, down.Y) + _field.Rand(-0.35f, 0.35f);
        for (int i = 0, n = 5 + (int)(9f * power); i < n; i++)
        {
            float a = angle + _field.Rand(-0.22f, 0.22f), v = (620f + 620f * power) * _field.Rand(0.35f, 1f);
            _field.Fling(at, new Vector2(MathF.Sin(a), MathF.Cos(a)) * v, 1.8f + 3.4f * MathF.Pow(_field.Rand(), 1.8f));
        }
        if (_field.Rand() < 0.45f)
            _field.Fling(at, down * (_field.Rand(160f, 380f) * power), _field.Rand(4.5f, 7f) * (0.6f + 0.4f * power));
    }

    /// <summary>Every few seconds a spurt lands on the lens beside a cut.</summary>
    private void Spatter(in CastFrame f)
    {
        if (f.Age < _nextSplat || f.Leave >= 0.01f) return;
        _nextSplat = f.Age + _field.Rand(3.5f, 8f);

        int k = (int)(_field.Rand() * _cuts) % _cuts;
        if (Drawn(f.Age, k) < 0.95f) return;
        _cut[k].SampleAtArc(_cut[k].Length * _field.Rand(0.25f, 0.85f), out Vector2 at, out Vector2 tan);
        _field.Splat(at + Downhill(tan) * (_field.Rand(24f, 80f) * f.Px), Bead(), (int)_field.Rand(5f, 10f));
    }
}

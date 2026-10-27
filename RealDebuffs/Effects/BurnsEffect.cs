using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Burns: soot vignette, firelight, smoke, four flame layers and cinders; fire catches along the bottom over ~2s.</summary>
public sealed class BurnsEffect : SceneEffect, IHasSwappableSlots
{
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Particle", PrimitiveRole.Ember,  "Flames",     "particle.ember", Hero: true),
        new("Particle", PrimitiveRole.Cinder, "Cinders",    "particle.cinder"),
        new("Particle", PrimitiveRole.Smoke,  "Smoke",      "particle.smoke"),
        new("Region",   PrimitiveRole.MainStroke, "Firelight", "region.firelight", "EdgeGlow"),
    };

    private static readonly uint Soot = DrawHelpers.Pack(0.045f, 0.012f, 0.006f);
    private static readonly uint Glow = DrawHelpers.Pack(1.00f, 0.42f, 0.07f);

    private const float BuildSeconds = 1.30f, BuildFloor = 0.30f;
    private const int BottomSites = 14, SideSites = 2;

    // Drawn back to front; layers catch in sequence after a site ignites.
    private static readonly FlameLayer[] BottomLayers =
    {
        new(FlameLayer.Back,  1.50f, 6, 46f, 73f, 0.40f),
        new(FlameLayer.Bed,   1.20f, 6, 67f, 106f),
        new(FlameLayer.Body,  1.15f, 6, 31f, 54f, 0.14f),
        new(FlameLayer.Front, 0.82f, 6, 20f, 37f, 0.24f),
    };

    private static readonly FlameLayer[] SideLayers =
    {
        new(FlameLayer.Back,  1.50f, 6, 46f, 70f, 0.40f),
        new(FlameLayer.Body,  1.15f, 6, 30f, 49f, 0.14f),
        new(FlameLayer.Front, 0.82f, 6, 19f, 33f, 0.24f),
    };

    private struct Site
    {
        public byte Edge;  // 0 bottom, 1 left, 2 right
        public float Along, Ignition, Boost, OscPeriod, OscPhase, Spread;
        public int Seed;
    }

    private readonly Site[] _sites = new Site[BottomSites + 2 * SideSites];
    private readonly FireDrift _drift;
    private readonly CastTracker _cast = new();
    private CastFrame _f;
    private bool _burst;

    public BurnsEffect() : base(DebuffKind.Burns, "Burns",
        "Flames lick up from the bottom and sides of the screen, with smoke and rising cinders.",
        1, "Burns",
        keywords: ["flame", "flames", "burning", "scorch", "ignite"])
    {
        for (int i = 0; i < _sites.Length; i++)
            _sites[i].Edge = (byte)(i < BottomSites ? 0 : i < BottomSites + SideSites ? 1 : 2);

        _drift = new FireDrift(0x8405, cinderMax: 96, smokeMax: 24, cinderPos: seed => Spawn(seed, 0f, 0.05f), smokePos: seed => Spawn(seed, 0.10f, 0.26f))
        {
            CinderSpeed = new Vector2(150f, 420f), CinderRate = 4f, SmokeRate = 1.2f,
        };
    }

    // Cinders and smoke leave from the bottom edge; smoke is lifted so it starts where the flames end.
    private Vector2 Spawn(int seed, float liftLo, float liftHi)
    {
        Site s = _sites[(int)((uint)seed % BottomSites)];
        float x = (s.Along + DrawHelpers.HashRange(seed + 20, -1f, 1f) * s.Spread * 1.3f) * _f.Size.X;
        return new Vector2(x, _f.Size.Y + 2f - DrawHelpers.HashRange(seed + 22, liftLo, liftHi) * _f.Short);
    }

    // Per cast: sites jitter inside their own cell so no gaps open; ignition spreads out from a random origin.
    private void BuildLayout(int castSeed)
    {
        float cell = 1f / BottomSites;
        float origin = 0.5f + DrawHelpers.HashRange(castSeed + 11, -0.10f, 0.10f);
        int hotA = Math.Clamp((int)(DrawHelpers.Hash01(castSeed + 21) * BottomSites), 0, BottomSites - 1);
        int hotB = (hotA + 4 + (int)(DrawHelpers.Hash01(castSeed + 22) * (BottomSites - 8))) % BottomSites;

        for (int n = 0; n < _sites.Length; n++)
        {
            ref Site st = ref _sites[n];
            int index = st.Edge == 0 ? n : (n - BottomSites) % SideSites;
            int s = unchecked(castSeed + (st.Edge * 31 + index) * 977 + 31);
            st.Seed = s;

            if (st.Edge == 0)
            {
                st.Along = Math.Clamp((index + 0.5f + DrawHelpers.HashRange(s, -0.28f, 0.28f)) * cell, 0.015f, 0.985f);
                float middle = 1f - Math.Abs(st.Along - 0.5f) * 2f;
                float hot = index == hotA || index == hotB ? DrawHelpers.HashRange(s + 9, 1.20f, 1.40f) : 1f;
                st.Boost = DrawHelpers.HashRange(s + 3, 0.86f, 1.20f) * (0.93f + 0.14f * middle) * hot;
                st.Ignition = Math.Abs(st.Along - origin) * 0.70f;
                st.Spread = cell * DrawHelpers.HashRange(s + 1, 0.45f, 0.60f);
            }
            else
            {
                st.Along = Math.Clamp((index + 0.5f + DrawHelpers.HashRange(s, -0.30f, 0.30f)) / SideSites * 0.58f, 0.02f, 0.60f);
                st.Boost = DrawHelpers.HashRange(s + 3, 0.62f, 0.92f) * (1f - 0.35f * st.Along);
                st.Ignition = 0.55f + st.Along * 0.85f;
                st.Spread = 0.10f;
            }
            st.OscPeriod = DrawHelpers.HashRange(s + 6, 0.70f, 1.90f);
            st.OscPhase = DrawHelpers.HashRange(s + 7, 0f, MathF.PI * 2f);
        }
    }

    public override void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        var f = _f = _cast.Frame(screenSize, alpha, time, dt, colorOverride);
        _drift.Px = f.Px;

        if (f.Fresh)
        {
            _drift.Clear();
            _burst = false;
            BuildLayout(f.Seed);
        }

        // Three-octave flicker drives the vignette, firelight and flame brightness together.
        float flicker = 0.55f * DrawHelpers.Pulse(f.Time, 0.31f)
                      + 0.30f * DrawHelpers.Pulse(f.Time, 0.17f, 0.35f)
                      + 0.15f * DrawHelpers.Pulse(f.Time, 0.53f, 0.60f);

        scene.RequestVignette(Soot, 0.13f, f.Alpha * (0.42f + 0.22f * flicker), priority: 30, f.Color);

        // Firelight grows in over ~1.2s with a bright flash at ignition.
        float reach = f.Short * 0.46f * DrawHelpers.EaseOutCubic(DrawHelpers.Saturate(f.Age / 1.2f));
        scene.Pool(f.Size, reach, Glow, f.Alpha * (0.30f + 0.34f * MathF.Exp(-f.Age / 0.50f)) * (0.76f + 0.24f * flicker), f.Color);

        _drift.EmitSmoke(scene, f.Time, f.Dt, f.Short, f.Alpha, DrawHelpers.Saturate((f.Age - 0.7f) / 1.4f), f.Color);

        float level = f.Alpha * (0.88f + 0.24f * flicker) * (1f + 0.25f * MathF.Exp(-f.Age / 0.60f));
        EmitFlames(scene, in f, BottomLayers, true, level);
        EmitFlames(scene, in f, SideLayers, false, level);

        if (!_burst && f.Age > 0.25f) { _burst = true; _drift.BurstCinders(40, f.Time); }
        float cinders = f.Age > 0.18f ? 0.35f + 0.65f * DrawHelpers.EaseOutCubic(DrawHelpers.Saturate((f.Age - 0.18f) / BuildSeconds)) : 0f;
        _drift.EmitCinders(scene, f.Time, f.Dt, f.Alpha, cinders, f.Color);
    }

    // Layers outer, sites inner, so every back flame is drawn before any front one.
    private void EmitFlames(EffectScene scene, in CastFrame f, FlameLayer[] layers, bool bottom, float level)
    {
        foreach (var layer in layers)
        {
            for (int n = 0; n < _sites.Length; n++)
            {
                ref readonly Site st = ref _sites[n];
                if (bottom != (st.Edge == 0)) continue;

                float since = f.Age - st.Ignition - layer.Delay;
                if (since < 0f) continue;

                // Builds up from BuildFloor, flares briefly as it catches, then breathes.
                float build = BuildFloor + (1f - BuildFloor) * DrawHelpers.EaseOutCubic(DrawHelpers.Saturate(since / BuildSeconds));
                float breath = 0.85f + 0.30f * DrawHelpers.Pulse(f.Time, st.OscPeriod, st.OscPhase + layer.Variant * 0.9f);
                float sizeMul = f.Px * st.Boost * build * breath * (1f + 0.25f * MathF.Exp(-since / 0.40f));

                Vector2 at; Vector2 spread; float lean = 0f;
                switch (st.Edge)
                {
                    case 0: at = new Vector2(st.Along * f.Size.X, f.Size.Y + 2f); spread = new Vector2(st.Spread * f.Size.X, 0f); break;
                    case 1: at = new Vector2(-2f, (1f - st.Along) * f.Size.Y); spread = new Vector2(0f, st.Spread * f.Size.Y); lean = 26f; break;
                    default: at = new Vector2(f.Size.X + 2f, (1f - st.Along) * f.Size.Y); spread = new Vector2(0f, st.Spread * f.Size.Y); lean = -26f; break;
                }
                Flames.Tongues(scene, in layer, at, st.Seed + layer.Variant * 131, f.Time, sizeMul, spread, lean, 0f, level, f.Color);
            }
        }
    }
}

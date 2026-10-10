using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Burns: layered fire (soot vignette, firelight, smoke, four flame layers, cinders) from oscillating grid emitters; ~2s ignition intro. Hero slot is the flames.</summary>
public sealed class BurnsEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Burns;
    public string DisplayName => "Burns";
    public string Description => "Flames lick up from the bottom and sides of the screen, with smoke and rising cinders.";
    public int DrawOrder => 1;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Burns"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[] { "flame", "flames", "burning", "scorch", "ignite" };

    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Particle", PrimitiveRole.Ember),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Particle", PrimitiveRole.Ember,  "Flames",     "particle.ember"),
        new("Particle", PrimitiveRole.Cinder, "Cinders",    "particle.cinder"),
        new("Particle", PrimitiveRole.Smoke,  "Smoke",      "particle.smoke"),
        new("Region",   PrimitiveRole.MainStroke, "Firelight", "region.firelight", "EdgeGlow"),
    };

    private static readonly uint Soot = DrawHelpers.Pack(0.045f, 0.012f, 0.006f);
    private static readonly uint Glow = DrawHelpers.Pack(1.00f, 0.42f, 0.07f);

    // Intro build: after ignition flames start at BuildFloor of full size and ease up over BuildSeconds; layers join in sequence (LayerDelayFor).
    private const float BuildSeconds = 1.30f;
    private const float BuildFloor   = 0.30f;

    // Variants understood by particle.ember.
    private const int VariantBody = 0, VariantBack = 1, VariantFront = 2, VariantBed = 3;

    /// <summary>One fire site: a bottom or side position owning a stack of emitters.</summary>
    private sealed class Site
    {
        public byte  Edge;              // 0 = bottom, 1 = left, 2 = right
        public int   Index;
        public float Along;             // 0..1 along that edge
        public float Ignition;          // seconds after cast before this site catches
        public float Rate;
        public float SizeBoost;
        public float OscPeriod, OscPhase;
        public float Spread;            // spawn spread either side of Along
        public bool  HasCinders, HasSmoke;
        public int   Seed;
    }

    private sealed class FireEmitter
    {
        public Site  Site = null!;
        public ParticleEmitter Pool = null!;
        public PrimitiveRole Role;
        public int   Variant;
        public float StartAfter;
        public float LayerDelay;
        public float RateMul = 1f;
        public float OscPeriodMul = 1f, OscPhaseAdd;
        public float OscBase = 0.72f, OscSpan = 0.56f;
        public float SpreadMul = 1f;
        public float SizeBoost = 1f;
        public float SpeedBoost = 1f;
        public float DriftX;
        public float IntervalMin, IntervalMax;
        public float LifeMin, LifeMax;
        public float SizeMinFrac, SizeMaxFrac;
        public float MinSpeed, MaxSpeed;
        public float LiftMin, LiftMax;
        public float Sway;
        public bool  Burned;
        public Func<int, Vector2> PosFn = null!;
        public Func<int, Vector2> VelFn = null!;
        public Func<int, Vector2> BurstVelFn = null!;
    }

    // Draw order is list order: smoke, then flames back->front, then cinders.
    private readonly List<FireEmitter> _smoke   = new();
    private readonly List<FireEmitter> _back    = new();
    private readonly List<FireEmitter> _bed     = new();
    private readonly List<FireEmitter> _body    = new();
    private readonly List<FireEmitter> _front   = new();
    private readonly List<FireEmitter> _cinders = new();
    private readonly List<FireEmitter> _all     = new();
    private readonly List<FireEmitter>[] _drawOrder;
    private readonly List<Site> _sites = new();

    private Vector2 _screenSize;
    private float _shortSide;

    private readonly CastTracker _cast = new();

    private const int BottomSites = 14;
    private const int SideSites   = 2;

    public BurnsEffect()
    {
        _drawOrder = new[] { _smoke, _back, _bed, _body, _front, _cinders };

        for (int i = 0; i < BottomSites; i++)
        {
            var site = NewSite(0, i);
            AddFlame(site, _back,  VariantBack,  0.30f, 0.52f, 1.15f, 1.90f, 0.043f, 0.068f, 12, rateMul: 0.90f, oscMul: 1.00f, phaseAdd: 0.0f, spreadMul: 0.9f);
            AddFlame(site, _bed,   VariantBed,   0.19f, 0.34f, 0.90f, 1.50f, 0.062f, 0.098f, 10, rateMul: 1.00f, oscMul: 1.10f, phaseAdd: 0.5f, spreadMul: 1.0f);
            AddFlame(site, _body,  VariantBody,  0.21f, 0.40f, 0.85f, 1.45f, 0.029f, 0.050f, 12, rateMul: 1.00f, oscMul: 0.90f, phaseAdd: 1.1f, spreadMul: 1.0f);
            AddFlame(site, _front, VariantFront, 0.20f, 0.37f, 0.60f, 1.05f, 0.019f, 0.034f, 10, rateMul: 1.05f, oscMul: 0.80f, phaseAdd: 2.3f, spreadMul: 1.0f);
            AddCinder(site);
            AddSmoke(site);
        }

        for (int side = 0; side < 2; side++)
        {
            for (int i = 0; i < SideSites; i++)
            {
                var site = NewSite((byte)(side == 0 ? 1 : 2), i);
                AddFlame(site, _back,  VariantBack,  0.30f, 0.52f, 1.10f, 1.80f, 0.043f, 0.065f, 8,  rateMul: 0.85f, oscMul: 1.00f, phaseAdd: 0.0f, spreadMul: 1f);
                AddFlame(site, _body,  VariantBody,  0.16f, 0.30f, 0.85f, 1.40f, 0.028f, 0.045f, 10, rateMul: 1.00f, oscMul: 0.90f, phaseAdd: 1.1f, spreadMul: 1f);
                AddFlame(site, _front, VariantFront, 0.12f, 0.24f, 0.60f, 1.00f, 0.018f, 0.031f, 9,  rateMul: 1.00f, oscMul: 0.80f, phaseAdd: 2.3f, spreadMul: 1f);
                site.HasCinders = site.HasSmoke = false;
            }
        }

        BuildLayout(0x5EED);
    }

    private Site NewSite(byte edge, int index)
    {
        var s = new Site { Edge = edge, Index = index };
        _sites.Add(s);
        return s;
    }

    /// <summary>Lays out this cast's fire (reseeded per cast); sites jitter within their own cell so no gaps open.</summary>
    private void BuildLayout(int castSeed)
    {
        int n = BottomSites;
        float cell = 1f / n;

        float origin = 0.5f + DrawHelpers.HashRange(castSeed + 11, -0.10f, 0.10f);

        int hotA = Math.Clamp((int)(DrawHelpers.Hash01(castSeed + 21) * n), 0, n - 1);
        int hotB = (hotA + 4 + (int)(DrawHelpers.Hash01(castSeed + 22) * (n - 8))) % n;

        int parity = (castSeed >> 3) & 1;

        foreach (var st in _sites)
        {
            int s = unchecked(castSeed + (st.Edge * 31 + st.Index) * 977 + 31);
            st.Seed = s;

            if (st.Edge == 0)
            {
                float along = (st.Index + 0.5f + DrawHelpers.HashRange(s, -0.28f, 0.28f)) * cell;
                st.Along = Math.Clamp(along, 0.015f, 0.985f);

                float middle = 1f - Math.Abs(st.Along - 0.5f) * 2f;
                float hot = (st.Index == hotA || st.Index == hotB) ? DrawHelpers.HashRange(s + 9, 1.20f, 1.40f) : 1f;

                st.Rate      = DrawHelpers.HashRange(s + 60, 0.85f, 1.35f) * (0.92f + 0.08f * middle) * (1f + (hot - 1f) * 0.6f);
                st.SizeBoost = DrawHelpers.HashRange(s + 3, 0.86f, 1.20f) * (0.93f + 0.14f * middle) * hot;
                st.Ignition  = Math.Abs(st.Along - origin) * 0.70f;
                st.Spread    = cell * DrawHelpers.HashRange(s + 1, 0.45f, 0.60f);
                st.HasCinders = ((st.Index + parity) & 1) == 0;
                st.HasSmoke   = !st.HasCinders;
            }
            else
            {
                float along = (st.Index + 0.5f + DrawHelpers.HashRange(s, -0.30f, 0.30f)) / SideSites * 0.58f;
                st.Along = Math.Clamp(along, 0.02f, 0.60f);

                st.Rate      = DrawHelpers.HashRange(s + 60, 0.80f, 1.30f);
                st.SizeBoost = DrawHelpers.HashRange(s + 3, 0.62f, 0.92f) * (1f - 0.35f * st.Along);
                st.Ignition  = 0.55f + st.Along * 0.85f;
                st.Spread    = 0.10f;
                st.HasCinders = st.HasSmoke = false;
            }

            st.OscPeriod = DrawHelpers.HashRange(s + 6, 0.70f, 1.90f);
            st.OscPhase  = DrawHelpers.HashRange(s + 7, 0f, MathF.PI * 2f);
        }

        foreach (var e in _all)
        {
            int es = unchecked(e.Site.Seed + e.Variant * 131 + (int)e.Role * 17);
            switch (e.Role)
            {
                case PrimitiveRole.Cinder:
                    e.RateMul    = DrawHelpers.HashRange(es + 61, 0.65f, 1.05f);
                    e.SpeedBoost = DrawHelpers.HashRange(es + 2, 0.7f, 1.3f);
                    break;
                case PrimitiveRole.Smoke:
                    e.RateMul    = DrawHelpers.HashRange(es + 61, 0.6f, 1.1f);
                    e.SizeBoost  = DrawHelpers.HashRange(es + 8, 0.8f, 1.25f);
                    e.SpeedBoost = DrawHelpers.HashRange(es + 9, 0.8f, 1.25f);
                    break;
            }
        }
    }

    private void AddFlame(Site site, List<FireEmitter> group, int variant,
                          float intervalMin, float intervalMax, float lifeMin, float lifeMax,
                          float sizeMinFrac, float sizeMaxFrac, int maxParticles,
                          float rateMul, float oscMul, float phaseAdd, float spreadMul)
    {
        int idx = _all.Count;
        var e = new FireEmitter
        {
            Site = site,
            Pool = new ParticleEmitter(maxParticles, seedSalt: 0x8105 + idx * 6427 % 9973 + variant * 131),
            Role = PrimitiveRole.Ember, Variant = variant,
            RateMul = rateMul, OscPeriodMul = oscMul, OscPhaseAdd = phaseAdd, SpreadMul = spreadMul,
            IntervalMin = intervalMin, IntervalMax = intervalMax,
            LifeMin = lifeMin, LifeMax = lifeMax,
            SizeMinFrac = sizeMinFrac, SizeMaxFrac = sizeMaxFrac,
            MinSpeed = 0f, MaxSpeed = variant == VariantFront ? 60f : (variant == VariantBed ? 8f : 24f),
            DriftX = site.Edge == 1 ? 26f : (site.Edge == 2 ? -26f : 0f),
            LayerDelay = LayerDelayFor(variant),
        };
        Register(group, e);
    }

    /// <summary>Layers catch in sequence after a site ignites: bed, body, front, back.</summary>
    private static float LayerDelayFor(int variant) => variant switch
    {
        VariantBed   => 0.00f,
        VariantBody  => 0.14f,
        VariantFront => 0.24f,
        VariantBack  => 0.40f,
        _            => 0f,
    };

    private void AddSmoke(Site site)
    {
        int idx = _all.Count;
        var e = new FireEmitter
        {
            Site = site,
            Pool = new ParticleEmitter(maxParticles: 16, seedSalt: 0x8305 + idx * 6427 % 9973)
            {
                Drag = 0.06f, Wander = 26f, WanderHz = 0.35f,
            },
            Role = PrimitiveRole.Smoke,
            StartAfter = 0.65f,
            OscBase = 0.55f, OscSpan = 0.90f, OscPeriodMul = 1.7f,
            SpreadMul = 1.2f,
            IntervalMin = 0.32f, IntervalMax = 0.62f,
            LifeMin = 2.6f, LifeMax = 4.4f,
            SizeMinFrac = 0.050f, SizeMaxFrac = 0.100f,
            MinSpeed = 38f, MaxSpeed = 88f,
            LiftMin = 0.10f, LiftMax = 0.26f,
            Sway = 6f,
        };
        Register(_smoke, e);
    }

    private void AddCinder(Site site)
    {
        int idx = _all.Count;
        var e = new FireEmitter
        {
            Site = site,
            Pool = new ParticleEmitter(maxParticles: 22, seedSalt: 0x8405 + idx * 6427 % 9973)
            {
                Gravity = new Vector2(0f, -26f),
                Drag = 0.30f, Wander = 240f, WanderHz = 0.85f,
            },
            Role = PrimitiveRole.Cinder,
            StartAfter = 0.18f,
            OscBase = 0.55f, OscSpan = 0.90f, OscPeriodMul = 1.3f, OscPhaseAdd = 0.7f,
            SpreadMul = 1.4f,
            IntervalMin = 0.11f, IntervalMax = 0.26f,
            LifeMin = 1.3f, LifeMax = 2.8f,
            SizeMinFrac = 0.0016f, SizeMaxFrac = 0.0034f,
            MinSpeed = 150f, MaxSpeed = 420f,
            LiftMin = 0f, LiftMax = 0.05f,
        };
        Register(_cinders, e);
    }

    private void Register(List<FireEmitter> group, FireEmitter e)
    {
        // Spawn delegates are built once per emitter (not per frame) so the draw loop allocates nothing.
        e.PosFn = seed => SpawnPos(e, seed);
        e.VelFn = seed => SpawnVel(e, seed);
        e.BurstVelFn = seed => SpawnVel(e, seed) * new Vector2(1.5f, 1.55f);
        group.Add(e);
        _all.Add(e);
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        _screenSize = screenSize;
        _shortSide = MathF.Min(screenSize.X, screenSize.Y);

        // A gap since the last Emit is a fresh application: restart ignition, drop leftovers, relay the fire from the cast start time.
        if (_cast.Begin(time))
        {
            foreach (var e in _all) { e.Pool.Clear(); e.Burned = false; }
            BuildLayout(unchecked((int)(_cast.Start * 1000f)));
        }
        float age = time - _cast.Start;

        float shortSide = _shortSide;

        // Three-octave flicker drives the vignette, firelight, and flame brightness together.
        float flicker = 0.55f * DrawHelpers.Pulse(time, 0.31f)
                      + 0.30f * DrawHelpers.Pulse(time, 0.17f, 0.35f)
                      + 0.15f * DrawHelpers.Pulse(time, 0.53f, 0.60f);

        scene.RequestVignette(Soot, 0.13f, alpha * (0.42f + 0.22f * flicker), priority: 30, colorOverride);

        // Firelight grows in over ~1.2s with a bright flash right at ignition.
        float reach = shortSide * 0.46f * DrawHelpers.EaseOutCubic(Math.Clamp(age / 1.2f, 0f, 1f));
        if (reach > 2f)
        {
            float glowAlpha = alpha * (0.30f + 0.34f * MathF.Exp(-age / 0.50f)) * (0.76f + 0.24f * flicker);
            scene.AddRegion(new RegionPrimitive
            {
                Min = new Vector2(0f, screenSize.Y - reach),
                Max = screenSize,
                Tint = Glow,
                Alpha = glowAlpha,
                Bottom = true,
                ColorOverride = colorOverride,
            });
        }

        foreach (var e in _all)
        {
            var site = e.Site;
            if (e.Role == PrimitiveRole.Cinder && !site.HasCinders) continue;
            if (e.Role == PrimitiveRole.Smoke  && !site.HasSmoke)   continue;

            float start = site.Ignition + e.StartAfter + e.LayerDelay;
            if (age < start) continue;
            float since = age - start;

            // Rate flare right after ignition, decaying over ~0.4s. Flames only.
            float ignBoost = e.Role == PrimitiveRole.Ember
                ? 1f + 2.5f * MathF.Exp(-since / 0.40f)
                : 1f;

            float build = DrawHelpers.EaseOutCubic(Math.Clamp(since / BuildSeconds, 0f, 1f));
            float sizeK = e.Role == PrimitiveRole.Ember ? BuildFloor + (1f - BuildFloor) * build : 1f;
            float rampK = e.Role == PrimitiveRole.Cinder ? 0.35f + 0.65f * build : 1f;

            // One-shot spark shower the moment a cinder emitter catches.
            if (e.Role == PrimitiveRole.Cinder && !e.Burned)
            {
                e.Burned = true;
                e.Pool.Burst(7, time, e.PosFn, e.BurstVelFn,
                             e.LifeMin, e.LifeMax,
                             shortSide * e.SizeMinFrac * e.SizeBoost * site.SizeBoost,
                             shortSide * e.SizeMaxFrac * e.SizeBoost * site.SizeBoost);
            }

            // Flames oscillate shallowly (0.72x-1.28x) so no stretch goes quiet; cinders and smoke keep the wide swing.
            float osc = (e.OscBase + e.OscSpan * DrawHelpers.Pulse(time, site.OscPeriod * e.OscPeriodMul, site.OscPhase + e.OscPhaseAdd)) * rampK;

            float rate = site.Rate * e.RateMul * ignBoost * osc;
            float intervalMin = MathF.Max(0.006f, e.IntervalMin / rate);
            float intervalMax = MathF.Max(0.012f, e.IntervalMax / rate);

            e.Pool.Update(
                time, dt,
                spawnIntervalMin: intervalMin, spawnIntervalMax: intervalMax,
                spawnPos: e.PosFn, spawnVelocity: e.VelFn,
                lifespanMin: e.LifeMin, lifespanMax: e.LifeMax,
                sizeMin: shortSide * e.SizeMinFrac * e.SizeBoost * site.SizeBoost * sizeK,
                sizeMax: shortSide * e.SizeMaxFrac * e.SizeBoost * site.SizeBoost * sizeK);
        }

        float flameLevel  = alpha * (0.88f + 0.24f * flicker) * (1f + 0.25f * MathF.Exp(-age / 0.60f));
        float smokeLevel  = alpha * Math.Clamp((age - 0.7f) / 1.4f, 0f, 1f);

        foreach (var group in _drawOrder)
        {
            foreach (var e in group)
            {
                if (e.Pool.Count == 0) continue;

                float level = e.Role switch
                {
                    PrimitiveRole.Smoke  => smokeLevel,
                    PrimitiveRole.Cinder => alpha,
                    _                    => flameLevel,
                };
                e.Pool.Emit(scene, time, e.Role,
                            brightnessMul: level, colorOverride,
                            swayPerParticle: e.Sway, variant: e.Variant);
            }
        }
    }

    /// <summary>Bottom emitters sit just below the edge (bases hidden), side emitters just outside it; smoke LIFTs so it starts where flames end.</summary>
    private Vector2 SpawnPos(FireEmitter e, int seed)
    {
        var site = e.Site;
        float jitter = DrawHelpers.HashRange(seed + 20, -1f, 1f) * site.Spread * e.SpreadMul;
        float lift = e.LiftMax > 0f ? DrawHelpers.HashRange(seed + 22, e.LiftMin, e.LiftMax) * _shortSide : 0f;

        return site.Edge switch
        {
            0 => new Vector2((site.Along + jitter) * _screenSize.X, _screenSize.Y + 2f - lift),
            1 => new Vector2(-2f + lift * 0.4f, (1f - site.Along + jitter) * _screenSize.Y),
            _ => new Vector2(_screenSize.X + 2f - lift * 0.4f, (1f - site.Along + jitter) * _screenSize.Y),
        };
    }

    private static Vector2 SpawnVel(FireEmitter e, int seed)
    {
        float vy = -DrawHelpers.HashRange(seed + 11, e.MinSpeed, e.MaxSpeed) * e.SpeedBoost;
        float spread = e.Role == PrimitiveRole.Cinder ? 55f : (e.Role == PrimitiveRole.Smoke ? 16f : 6f);
        float vx = DrawHelpers.HashRange(seed + 10, -spread, spread) + e.DriftX;
        return new Vector2(vx, vy);
    }
}
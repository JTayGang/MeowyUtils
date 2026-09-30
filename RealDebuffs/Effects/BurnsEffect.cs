using System.Numerics;
using Dalamud.Bindings.ImGui;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Burns: fire damage over time, built to read as a photographed fire rather than a particle
/// effect. The composition is layered back-to-front, the way the light actually stacks:
///
///  1. Soot vignette - a mild darkening of the frame edges (a lit fire makes the rest of the
///                     scene read darker by comparison).
///  2. Firelight     - warm spill along the bottom and lower sides, brighter under tall flames and
///                     flickering column by column (region.firelight).
///  3. Smoke         - dark, expanding puffs rising above the flames (particle.smoke, Role.Smoke).
///  4. Flames        - overlapping layers of flame tongues drawn back to front: back (cool, tall,
///                     faint), bed (wide and low, keeps the base continuous), body, and front (hot,
///                     compact, dense), so the hottest cores always sit on top. All are
///                     particle.ember on Role.Ember; ParticlePrimitive.Variant picks the layer.
///  5. Cinders       - cooling, flickering, motion-smeared flecks that escape upward
///                     (particle.cinder, Role.Cinder).
///
/// WHAT THIS FILE OWNS is WHERE and WHEN things spawn; how a flame, a puff or a cinder LOOKS lives
/// entirely in the materials, so each layer is independently swappable in the Effect generator.
///
/// Spawning keeps the model the previous version used, which is what gave the fire its character:
///  - Cluster: emitter positions are jittered off a grid with a per-emitter pull toward a neighbor,
///    so some spots have dense flames and others have gaps.
///  - Varying rate: every position runs at its own random rate, boosted toward the middle, so the
///    fire is tallest and busiest at bottom-center and lazier toward the corners.
///  - Oscillation: each emitter's rate follows its own sine wave, out of phase with the others, so
///    which stretch is raging and which is calm keeps drifting.
///
/// INTRO (~0 - 2s): the fire BUILDS rather than appearing. Emitters ignite in order of distance
/// from the bottom center - the middle catches first, fire spreads along the bottom, then climbs the
/// lower side corners. After an emitter ignites, its flame layers join in sequence (bed, body,
/// front, then the tall back flames) and every layer eases from ~30% to full size over ~1.3s, so
/// the fire visibly climbs. On ignition an emitter's rate is boosted ~3.5x and decays back over
/// ~0.4s (the "catch and flare"), a cinder shower is thrown up at the moment each catches, and the
/// firelight grows in from nothing with a bright flash at the start. Smoke follows the flames.
///
/// HERO ITEM: the flames (Role.Ember). A "made of X" phrase replaces them; smoke, cinders and the
/// firelight stay put, exactly as the old ground band and vignette did.
/// </summary>
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

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "flame", "flames", "burning", "scorch", "ignite"
    };

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

    // ---- palette (only what the effect itself owns; fire colors live in the materials) ----
    private static readonly uint Soot = DrawHelpers.ToU32(0.045f, 0.012f, 0.006f, 1f);
    private static readonly uint Glow = DrawHelpers.ToU32(1.00f, 0.42f, 0.07f, 1f);

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;

    // Intro build: after an emitter (and its layer) ignites, its flames start at BuildFloor of full
    // size and ease up to 100% over BuildSeconds, so the fire visibly CLIMBS instead of appearing
    // at full height. Layers join in sequence (see LayerDelayFor), which is what makes the build
    // read as catch -> spread -> climb rather than a fade-in.
    private const float BuildSeconds = 1.30f;
    private const float BuildFloor   = 0.30f;

    // Flame layer variants understood by particle.ember.
    private const int VariantBody = 0, VariantBack = 1, VariantFront = 2, VariantBed = 3;

    /// <summary>
    /// One fire site: a spot along the bottom (or a side) that owns a stack of emitters - flame layers,
    /// and possibly cinders or smoke. Everything here is the CAST LAYOUT: rebuilt from a fresh seed at
    /// the start of every cast (see BuildLayout), so no two casts share the same arrangement, and
    /// fixed for the cast's duration so the fire doesn't shuffle while it burns.
    /// </summary>
    private sealed class Site
    {
        public byte  Edge;              // 0 = bottom, 1 = left, 2 = right
        public int   Index;             // slot along that edge (0 .. count-1)
        public float Along;             // 0..1 position along that edge
        public float Ignition;          // seconds after cast before this site catches
        public float Rate;              // baseline rate multiplier for this site
        public float SizeBoost;
        public float OscPeriod, OscPhase;
        public float Spread;            // spawn spread either side of Along (fraction of the edge's length)
        public bool  HasCinders, HasSmoke;
        public int   Seed;              // per-cast seed for this site (derives per-emitter randoms)
    }

    private sealed class FireEmitter
    {
        public Site  Site = null!;
        public ParticleEmitter Pool = null!;
        public PrimitiveRole Role;
        public int   Variant;
        public float StartAfter;        // extra seconds after ignition (smoke, cinders follow the flames)
        public float LayerDelay;        // extra seconds after ignition before this flame layer joins
        public float RateMul = 1f;      // this layer's share of the site's rate
        public float OscPeriodMul = 1f, OscPhaseAdd;
        public float OscBase = 0.72f, OscSpan = 0.56f;   // rate modulation: base + span * pulse
        public float SpreadMul = 1f;
        public float SizeBoost = 1f;     // per-emitter, on top of the site's
        public float SpeedBoost = 1f;
        public float DriftX;             // px/s horizontal velocity added at spawn
        public float IntervalMin, IntervalMax;
        public float LifeMin, LifeMax;
        public float SizeMinFrac, SizeMaxFrac;   // of shortSide
        public float MinSpeed, MaxSpeed;         // px/s upward (positive; sign flipped at spawn)
        public float LiftMin, LiftMax;           // spawn height above the edge, fraction of shortSide
        public float Sway;                       // px of per-particle horizontal sway
        public float Brightness = 1f;
        public bool  Burned;            // one-shot ignition spark shower already fired this cast
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

    // Set at the top of every Emit, before any emitter runs - spawn delegates read them.
    private Vector2 _screenSize;
    private float _shortSide;

    private float _castStart = -1f;
    private float _lastDrawTime = -100f;

    // Fire sites: enough that neighbors overlap, few enough to stay cheap.
    private const int BottomSites = 14;
    private const int SideSites   = 2;      // per side

    public BurnsEffect()
    {
        _drawOrder = new[] { _smoke, _back, _bed, _body, _front, _cinders };

        // Structure only: which sites exist and what each one runs. WHERE they sit, how fast they
        // burn and which ones shed cinders is the cast layout - see BuildLayout.
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

        BuildLayout(0x5EED);   // replaced by a fresh layout at the start of every cast
    }

    private Site NewSite(byte edge, int index)
    {
        var s = new Site { Edge = edge, Index = index };
        _sites.Add(s);
        return s;
    }

    // ---- cast layout ----

    /// <summary>
    /// Lays out this cast's fire. Called at the start of every cast with a seed from the cast start
    /// time (same convention as HeavyEffect), so every cast gets its own arrangement, and it stays
    /// fixed until the cast ends.
    ///
    /// COVERAGE: the previous layout let sites drift toward neighbors and ran rates from 0.6x to 2x,
    /// so some stretches of the bottom edge were dense and others were empty. Now:
    ///  - each site is jittered only WITHIN its own cell (never into the next one), so sites can't
    ///    open a gap between them;
    ///  - rates sit in a narrow band and the oscillation is shallow, so no stretch goes quiet;
    ///  - spawns spread across the whole cell, so a site fills its cell instead of one column;
    ///  - liveliness comes from a couple of randomly chosen HOT sites per cast (taller, busier) and
    ///    from per-site sizes, not from starving other sites.
    /// </summary>
    private void BuildLayout(int castSeed)
    {
        int n = BottomSites;
        float cell = 1f / n;

        // Where ignition starts: near the middle, but not always exactly the middle.
        float origin = 0.5f + DrawHelpers.HashRange(castSeed + 11, -0.10f, 0.10f);

        // Two hot sites, kept apart so they don't merge into one giant flare.
        int hotA = Math.Clamp((int)(DrawHelpers.Hash01(castSeed + 21) * n), 0, n - 1);
        int hotB = (hotA + 4 + (int)(DrawHelpers.Hash01(castSeed + 22) * (n - 8))) % n;

        int parity = (castSeed >> 3) & 1;            // which sites shed cinders vs smoke this cast

        foreach (var st in _sites)
        {
            int s = unchecked(castSeed + (st.Edge * 31 + st.Index) * 977 + 31);
            st.Seed = s;

            if (st.Edge == 0)
            {
                // Stratified: the site's centre wanders ±0.28 of its own cell.
                float along = (st.Index + 0.5f + DrawHelpers.HashRange(s, -0.28f, 0.28f)) * cell;
                st.Along = Math.Clamp(along, 0.015f, 0.985f);

                float middle = 1f - Math.Abs(st.Along - 0.5f) * 2f;          // 1 at centre, 0 at the corners
                float hot = (st.Index == hotA || st.Index == hotB) ? DrawHelpers.HashRange(s + 9, 1.20f, 1.40f) : 1f;

                st.Rate      = DrawHelpers.HashRange(s + 60, 0.85f, 1.35f) * (0.92f + 0.08f * middle) * (1f + (hot - 1f) * 0.6f);
                st.SizeBoost = DrawHelpers.HashRange(s + 3, 0.86f, 1.20f) * (0.93f + 0.14f * middle) * hot;
                st.Ignition  = Math.Abs(st.Along - origin) * 0.70f;
                st.Spread    = cell * DrawHelpers.HashRange(s + 1, 0.45f, 0.60f);   // fraction of screen width either side
                st.HasCinders = ((st.Index + parity) & 1) == 0;
                st.HasSmoke   = !st.HasCinders;
            }
            else
            {
                // Sides: two per side, climbing the lower corners (smaller, weaker, later).
                float along = (st.Index + 0.5f + DrawHelpers.HashRange(s, -0.30f, 0.30f)) / SideSites * 0.58f;
                st.Along = Math.Clamp(along, 0.02f, 0.60f);

                st.Rate      = DrawHelpers.HashRange(s + 60, 0.80f, 1.30f);
                st.SizeBoost = DrawHelpers.HashRange(s + 3, 0.62f, 0.92f) * (1f - 0.35f * st.Along);
                st.Ignition  = 0.55f + st.Along * 0.85f;
                st.Spread    = 0.10f;                                             // fraction of screen height either side
                st.HasCinders = st.HasSmoke = false;
            }

            st.OscPeriod = DrawHelpers.HashRange(s + 6, 0.70f, 1.90f);
            st.OscPhase  = DrawHelpers.HashRange(s + 7, 0f, MathF.PI * 2f);
        }

        // Per-emitter randoms that also belong to the cast layout.
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

    // ---- roster builders ----

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
            // Flames stay rooted at the fuel; the front layer lifts off a bit more so its tips detach.
            MinSpeed = 0f, MaxSpeed = variant == VariantFront ? 60f : (variant == VariantBed ? 8f : 24f),
            DriftX = site.Edge == 1 ? 26f : (site.Edge == 2 ? -26f : 0f),   // side flames drift inward
            LayerDelay = LayerDelayFor(variant),
        };
        Register(group, e);
    }

    /// <summary>
    /// Layers catch in sequence after a site ignites: the low bed first (the ground catching),
    /// then the body, then the hot front, and last the tall faint back flames - the fire climbing.
    /// </summary>
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
                Gravity = new Vector2(0f, -26f),      // hot air keeps lifting them
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
        e.BurstVelFn = seed => SpawnVel(e, seed) * new Vector2(1.5f, 1.55f);   // the ignition shower is thrown harder
        group.Add(e);
        _all.Add(e);
    }

    // ---- per-frame ----

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, Vector4? colorOverride)
    {
        _screenSize = screenSize;
        _shortSide = MathF.Min(screenSize.X, screenSize.Y);

        // A gap since the last Emit means a fresh application: restart the ignition, drop anything
        // left over from the previous cast, and lay out a new fire. The layout is reseeded from the
        // cast start time (the same convention HeavyEffect uses) so every cast looks different.
        if (time - _lastDrawTime > NewCastGapSeconds)
        {
            _castStart = time;
            foreach (var e in _all) { e.Pool.Clear(); e.Burned = false; }
            BuildLayout(unchecked((int)(_castStart * 1000f)));
        }
        _lastDrawTime = time;
        float age = time - _castStart;

        float dt = ImGui.GetIO().DeltaTime;
        float shortSide = _shortSide;

        // Three-octave flicker drives the vignette, the firelight and the flames' brightness together,
        // so the whole scene breathes as one instead of each layer pulsing on its own clock.
        float flicker = 0.55f * DrawHelpers.Pulse(time, 0.31f)
                      + 0.30f * DrawHelpers.Pulse(time, 0.17f, 0.35f)
                      + 0.15f * DrawHelpers.Pulse(time, 0.53f, 0.60f);

        // ---- 1. soot vignette ----
        scene.RequestVignette(Soot, 0.13f, alpha * (0.42f + 0.22f * flicker), priority: 30, colorOverride);

        // ---- 2. firelight ----
        // Grows in from nothing over ~1.2s as the fire builds (the old ground band grew from nothing
        // too), with a bright flash right at ignition that eases down as the fire settles.
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

        // ---- 3. drive every emitter ----
        foreach (var e in _all)
        {
            var site = e.Site;
            if (e.Role == PrimitiveRole.Cinder && !site.HasCinders) continue;
            if (e.Role == PrimitiveRole.Smoke  && !site.HasSmoke)   continue;

            float start = site.Ignition + e.StartAfter + e.LayerDelay;
            if (age < start) continue;
            float since = age - start;

            // Catch-and-flare: right after this emitter ignites its rate runs ~3.5x and decays back
            // over ~0.4s. Smoke and cinders skip the flare (they follow, they don't flare).
            float ignBoost = e.Role == PrimitiveRole.Ember
                ? 1f + 2.5f * MathF.Exp(-since / 0.40f)
                : 1f;

            // Build: flames start small and ease up to full size; cinders ramp their rate in.
            float build = DrawHelpers.EaseOutCubic(Math.Clamp(since / BuildSeconds, 0f, 1f));
            float sizeK = e.Role == PrimitiveRole.Ember ? BuildFloor + (1f - BuildFloor) * build : 1f;
            float rampK = e.Role == PrimitiveRole.Cinder ? 0.35f + 0.65f * build : 1f;

            // Ignition spark shower: the moment a cinder emitter catches, it throws a handful of
            // cinders up at once - the "the fire just caught here" flare, in sparks.
            if (e.Role == PrimitiveRole.Cinder && !e.Burned)
            {
                e.Burned = true;
                e.Pool.Burst(7, time, e.PosFn, e.BurstVelFn,
                             e.LifeMin, e.LifeMax,
                             shortSide * e.SizeMinFrac * e.SizeBoost * site.SizeBoost,
                             shortSide * e.SizeMaxFrac * e.SizeBoost * site.SizeBoost);
            }

            // Per-emitter rate modulation. Flames use a shallow swing (0.72x-1.28x): deep swings are
            // what used to leave stretches of the edge empty. Cinders and smoke keep the wide one.
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

        // ---- 4. emit back -> front ----
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
                            brightnessMul: level * e.Brightness, colorOverride,
                            swayPerParticle: e.Sway, variant: e.Variant);
            }
        }
    }

    /// <summary>
    /// World-space spawn point. Bottom emitters sit just below the bottom edge (so flame bases are
    /// hidden and the fire looks rooted); side emitters sit just outside the left/right edge, and
    /// Along = 0 is the bottom corner, 0.6 is 60% up the side. Each spawn lands anywhere within the
    /// site's spread, which is what lets a site fill its cell instead of piling up on one column.
    /// Smoke spawns LIFT above the edge so it starts where the flames end, cinders a little above
    /// the fire's base.
    /// </summary>
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

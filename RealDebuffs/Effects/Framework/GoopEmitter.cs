using System.Numerics;
using System.Runtime.InteropServices;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// The viscous-drip simulation. A stroke material opts in by declaring an emission with a
/// <see cref="StrokeDripOptions"/> block; StrokeAutoEmitter hands such emissions here instead of
/// scattering free particles.
///
/// The model is the life of a drop on an underside, in four beats:
///   rest     a small bead clings to the surface.
///   swell    liquid feeds it; it grows and hangs lower, stretching a neck behind it.
///   pinch    the neck gets too thin and lets go.
///   fall     the drop drops, pulled out into a teardrop by its speed; the neck snaps back to the
///            surface as a recoiling string, sometimes leaving a small satellite droplet in the gap.
/// then the site starts over. Stringiness decides how long the neck is allowed to stretch: water barely
/// has one, honey has a lot.
///
/// State lives in two small pools, like StrokeAutoEmitter's: sites (places on a strand that grow drops,
/// keyed so they stay put on the strand as it moves) and drops (what has fallen). Everything is handed to
/// the scene at the end of the frame as ordinary <see cref="ParticlePrimitive"/>s: the thread rides on the
/// particle's Anchor/ThreadEnd/Tether fields and the particle material draws it, so nothing here knows
/// whether it is slime, blood or sludge. That is the material's job (see ParticleGoop).
/// </summary>
public static class GoopEmitter
{
    private const int MaxDrops = 192;
    private const int MaxPendants = 256;
    private const int MaxSitesPerStroke = 12;
    private const float MaxStep = 0.05f;                    // a frame hitch must not make a drop teleport

    // Fractions of a cycle: the bead rests, swells and stretches until PinchAt, then lets go.
    private const float RestEnd = 0.10f;
    private const float PinchAt = 0.86f;

    private readonly record struct SiteKey(DebuffKind Owner, int StrokeSeed, int Index);

    private struct Site
    {
        public float Phase;           // 0..1 through the current cycle
        public float Cycle;           // seconds this cycle lasts
        public int   Cycles;          // completed so far; salts every per-cycle random
        public float LastSeen;
    }

    private struct Drop
    {
        public DebuffKind Owner;
        public Vector2 Pos, Vel, Gravity;
        public Vector2 Anchor, Pinch;     // where the string was fixed, and where it broke
        public float Born, Life;
        public float Size, Brightness;
        public float Tether;              // thickness of the string at the moment of release, fraction of Size
        public float RecoilTime;          // how long the string takes to snap back; 0 = no string
        public float Drag;
        public int   Seed;
        public string? Material;
        public Vector4? ColorOverride;
    }

    private static readonly Dictionary<SiteKey, Site> Sites = new();
    private static readonly List<SiteKey> Stale = new();
    private static readonly Drop[] Drops = new Drop[MaxDrops];
    private static int _dropCount;
    private static readonly ParticlePrimitive[] Pendants = new ParticlePrimitive[MaxPendants];
    private static int _pendantCount;
    private static float _lastPrune;

    /// <summary>Called once at the start of each frame: moves what has already fallen, and forgets sites whose strand is gone.</summary>
    public static void Advance(float time, float dt, Vector2 screen)
    {
        _pendantCount = 0;
        dt = MathF.Min(dt, MaxStep);

        int w = 0;
        float floor = screen.Y > 0f ? screen.Y + 80f : float.MaxValue;
        for (int i = 0; i < _dropCount; i++)
        {
            ref Drop d = ref Drops[i];
            if (time - d.Born >= d.Life) continue;
            d.Vel += d.Gravity * dt;
            d.Vel *= MathF.Exp(-d.Drag * dt);               // viscous drag: a drop reaches a terminal speed instead of accelerating forever
            d.Pos += d.Vel * dt;
            if (d.Pos.Y > floor) continue;
            Drops[w++] = d;
        }
        _dropCount = w;

        if (time - _lastPrune > 0.5f)
        {
            _lastPrune = time;
            Stale.Clear();
            foreach (var kv in Sites)
                if (time - kv.Value.LastSeen > 0.6f) Stale.Add(kv.Key);
            for (int i = 0; i < Stale.Count; i++) Sites.Remove(Stale[i]);
        }
    }

    /// <summary>
    /// Grows this frame's drops along one stroke for one drip emission. <paramref name="strokeMaterial"/>
    /// supplies the strand's radius so drops hang from its surface, not its centreline; null (a particle
    /// material acting as emitter) assumes a plain tube.
    /// </summary>
    public static void Tend(in StrokePrimitive s, in StrokeEmission e, in StrokeDripOptions o,
                            IStrokeMaterial? strokeMaterial, string? particleMaterial,
                            float time, float dt, Vector2 screen)
    {
        dt = MathF.Min(dt, MaxStep);
        float px = screen.X > 0f ? Math.Clamp(MathF.Min(screen.X, screen.Y) / 1080f, 0.75f, 2.4f) : 1f;
        float shortSide = screen.X > 0f ? MathF.Min(screen.X, screen.Y) : 1080f;

        float visibleLen = s.Path.Length * s.Reveal;
        if (visibleLen < 24f * px) return;

        float depthScale = 1f - 0.35f * Math.Clamp(s.Depth, 0f, 1f);
        float spacing = MathF.Max(24f, o.SiteSpacingPx) * px;
        int count = Math.Min(MaxSitesPerStroke, (int)(visibleLen / spacing));

        // Index -1 is the tip; 0.. are along the strand.
        int first = o.TipSite && !s.Closed ? -1 : 0;
        for (int index = first; index < count; index++)
        {
            float arc;
            bool tip = index < 0;
            if (tip)
            {
                arc = visibleLen;
            }
            else
            {
                float jitter = DrawHelpers.HashRange(unchecked(s.Seed * 31 + index * 7919), -0.30f, 0.30f);
                arc = (index + 0.5f + jitter) * spacing;
                if (arc > visibleLen - 14f * px) continue;      // not grown out to here yet
            }

            TendSite(in s, in e, in o, strokeMaterial, particleMaterial, index, tip, arc,
                     time, dt, px, shortSide, depthScale, screen);
        }
    }

    private static void TendSite(in StrokePrimitive s, in StrokeEmission e, in StrokeDripOptions o,
                                 IStrokeMaterial? strokeMaterial, string? particleMaterial,
                                 int index, bool tip, float arc, float time, float dt, float px, float shortSide,
                                 float depthScale, Vector2 screen)
    {
        var key = new SiteKey(s.Owner, s.Seed, index);
        ref Site site = ref CollectionsMarshal.GetValueRefOrAddDefault(Sites, key, out bool exists);
        int siteSeed = unchecked(s.Seed * 131 + index * 977 + 17);
        if (!exists)
        {
            site.Cycle = DrawHelpers.HashRange(siteSeed, o.CycleSecondsMin, o.CycleSecondsMax);
            site.Phase = DrawHelpers.Hash01(siteSeed + 3) * PinchAt * 0.95f;     // out of step, so a strand never drips in unison
        }
        site.LastSeen = time;
        site.Phase += dt / site.Cycle;

        // ---- where the drop hangs from ----
        s.Path.SampleAtArc(arc, out Vector2 pos, out Vector2 tan);
        float radius = strokeMaterial is not null ? strokeMaterial.RadiusAt(in s, arc, shortSide) : MathF.Max(1f, s.WidthHint * 0.5f);

        Vector2 anchor;
        float downhill;                                     // 0..1 how much this spot faces down; gates and fades the drop
        if (tip)
        {
            anchor = pos;
            downhill = tan.Y;                               // the tip points along the tangent: hang from it only if that points down
        }
        else
        {
            Vector2 perp = new(-tan.Y, tan.X);
            if (perp.Y < 0f) perp = -perp;                  // the side of the strand that faces the floor
            anchor = pos + perp * (radius * 0.78f);
            downhill = perp.Y;
        }
        float awake = Smooth((downhill - o.MinSlope) / 0.18f);

        // ---- the size of this cycle's drop ----
        int cycleSeed = unchecked(siteSeed + site.Cycles * 7331);
        float rMax = DrawHelpers.HashRange(cycleSeed + 1, e.SizeMin, e.SizeMax) * px * depthScale * (tip ? 1.25f : 1f);
        float lMax = rMax * (1.2f + 5.4f * Math.Clamp(o.Stringiness, 0f, 1f));

        // ---- the shape of the pendant drop at this point in its life ----
        float q = Math.Clamp((site.Phase - RestEnd) / (PinchAt - RestEnd), 0f, 1f);
        float rb = rMax * (0.40f + 0.60f * MathF.Sqrt(q));
        float len = rb * 0.45f + lMax * MathF.Pow(q, 2.4f);
        float neck = Lerp(0.66f, 0.10f + 0.10f * (1f - o.Stringiness), MathF.Pow(q, 1.4f));
        float swing = 0.12f * MathF.Sin(time * 1.9f + siteSeed * 0.37f) * q;
        Vector2 bead = anchor + new Vector2(MathF.Sin(swing), MathF.Cos(swing)) * len;

        bool onScreen = screen.X <= 0f || (anchor.X > -40f && anchor.X < screen.X + 40f && anchor.Y > -40f && anchor.Y < screen.Y + 40f);

        if (awake > 0.01f && onScreen && _pendantCount < MaxPendants)
        {
            Pendants[_pendantCount++] = new ParticlePrimitive
            {
                Owner = s.Owner,
                Position = bead,
                Anchor = anchor,
                ThreadEnd = bead,
                Tether = neck,
                Size = rb,
                AgeRatio = q,
                Brightness = s.Brightness * awake,         // distance shows in the drop's size, not its opacity: a dimmed translucent liquid just turns into a dark ghost
                Seed = cycleSeed,
                Role = e.Role,
                MaterialName = particleMaterial,
                ColorOverride = s.ColorOverride,
            };
        }

        // ---- let go ----
        if (site.Phase >= PinchAt + 0.0001f)
        {
            if (awake > 0.5f && onScreen) Release(in s, in e, in o, particleMaterial, anchor, bead, rb, neck, cycleSeed, time, px);
            site.Cycles++;
            site.Cycle = DrawHelpers.HashRange(unchecked(siteSeed + site.Cycles * 7331 + 1), o.CycleSecondsMin, o.CycleSecondsMax);
            site.Phase = 0f;
        }
    }

    private static void Release(in StrokePrimitive s, in StrokeEmission e, in StrokeDripOptions o, string? material,
                                Vector2 anchor, Vector2 bead, float rb, float neck, int seed, float time, float px)
    {
        if (_dropCount >= MaxDrops) return;

        float g = e.Gravity.Y != 0f ? e.Gravity.Y : 1500f;
        float v0 = DrawHelpers.HashRange(seed + 5, e.SpeedMin, e.SpeedMax) * px;
        float life = DrawHelpers.HashRange(seed + 6, e.LifespanMin, e.LifespanMax);

        Drops[_dropCount++] = new Drop
        {
            Owner = s.Owner,
            Pos = bead,
            Vel = new Vector2(DrawHelpers.HashRange(seed + 7, -10f, 10f) * px, v0),
            Gravity = new Vector2(e.Gravity.X, g) * px,
            Drag = 1.6f,
            Anchor = anchor,
            Pinch = bead,
            Born = time,
            Life = life,
            Size = rb,
            Brightness = s.Brightness,
            Tether = neck,
            RecoilTime = 0.15f + 0.12f * Math.Clamp(o.Stringiness, 0f, 1f),
            Seed = seed,
            Material = material,
            ColorOverride = s.ColorOverride,
        };

        // The string rarely breaks cleanly: sometimes a small droplet is left in the gap, falling a little behind.
        if (_dropCount < MaxDrops && DrawHelpers.Hash01(seed + 8) < o.SatelliteChance)
        {
            Vector2 at = Vector2.Lerp(anchor, bead, 0.62f);
            Drops[_dropCount++] = new Drop
            {
                Owner = s.Owner,
                Pos = at,
                Vel = new Vector2(DrawHelpers.HashRange(seed + 9, -12f, 12f) * px, v0 * 0.35f),
                Gravity = new Vector2(e.Gravity.X, g) * px,
                Drag = 1.6f,
                Born = time,
                Life = life,
                Size = rb * DrawHelpers.HashRange(seed + 10, 0.20f, 0.32f),
                Brightness = s.Brightness,
                Seed = seed + 99,
                Material = material,
                ColorOverride = s.ColorOverride,
            };
        }
    }

    /// <summary>Called once at the end of the frame: hands the scene this frame's hanging and falling liquid.</summary>
    public static void Emit(EffectScene scene, float time)
    {
        for (int i = 0; i < _pendantCount; i++)
            scene.AddParticleForOwner(Pendants[i], Pendants[i].Owner);

        for (int i = 0; i < _dropCount; i++)
        {
            ref readonly Drop d = ref Drops[i];
            float age = time - d.Born;
            float t01 = Math.Clamp(age / d.Life, 0f, 1f);

            var p = new ParticlePrimitive
            {
                Owner = d.Owner,
                Position = d.Pos,
                Velocity = d.Vel,
                Size = d.Size,
                AgeRatio = t01,
                Brightness = d.Brightness * ParticleEmitter.FadeFor(t01),
                Seed = d.Seed,
                Role = PrimitiveRole.Goop,
                MaterialName = d.Material,
                ColorOverride = d.ColorOverride,
            };

            // The broken string snaps back up to where it was fixed, thinning as it goes.
            if (d.RecoilTime > 0f && age < d.RecoilTime)
            {
                float k = age / d.RecoilTime;
                float eased = 1f - (1f - k) * (1f - k);
                p.Anchor = d.Anchor;
                p.ThreadEnd = Vector2.Lerp(d.Pinch, d.Anchor, eased);
                p.Tether = d.Tether * (1f - 0.65f * k);
            }
            scene.AddParticleForOwner(p, d.Owner);
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

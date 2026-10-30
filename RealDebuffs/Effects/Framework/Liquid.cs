using System.Numerics;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>How a liquid moves, from one thickness axis (0 water, BloodAt blood, 1 slime); slime and blood reproduce their hand-tuned drips exactly.</summary>
public readonly struct LiquidPhysics
{
    public const float BloodAt = 0.45f;

    // Piecewise-linear through the three calibrated liquids; exact at each one.
    private static float At(float v, float water, float blood, float slime)
    {
        if (v <= 0f) return water;
        if (v < BloodAt) return water + (blood - water) * (v / BloodAt);
        if (v == BloodAt) return blood;
        float t = (v - BloodAt) / (1f - BloodAt);
        return t >= 1f ? slime : blood + (slime - blood) * t;
    }

    public readonly float Viscosity;

    // drops and drips (px at 1080p, seconds)
    public readonly float Gravity, Drag, Stringiness, Satellites, MinSlope, Spacing;
    public readonly float CycleMin, CycleMax, ReleaseMin, ReleaseMax, LifeMin, LifeMax, SizeMin, SizeMax;

    // jets and pools
    public readonly float Breakup;  // 1/s: how fast a falling stream's ripples grow, at an 8 px radius
    public readonly float Ripple;  // size of the ripples a stream starts with
    public readonly float Wave, Damping, Smooth;  // pool surface: wave speed px/s, decay 1/s, viscous relaxation px^2/s
    public readonly float Splash, Foam, FoamLife;  // droplets thrown, foam laid down, seconds it lasts
    public readonly float Bubbles, Rise;  // bubbles dragged under, and how fast they climb (px/s)
    public readonly float Cling;  // how fast a bead runs down glass, px/s
    public readonly float Churn;  // how much a falling stream writhes and aerates

    public LiquidPhysics(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        Viscosity = v;
        Gravity = At(v, 3000f, 1800f, 1500f);
        Drag = At(v, 1.1f, 1.6f, 1.6f);
        Stringiness = At(v, 0.04f, 0.50f, 0.85f);
        Satellites = At(v, 0.35f, 0.30f, 0.55f);
        MinSlope = At(v, 0.34f, 0.38f, 0.42f);
        Spacing = At(v, 90f, 110f, 150f);
        CycleMin = At(v, 1.2f, 2.2f, 3.0f); CycleMax = At(v, 2.6f, 4.2f, 5.4f);
        ReleaseMin = At(v, 40f, 70f, 60f); ReleaseMax = At(v, 90f, 150f, 130f);
        LifeMin = At(v, 1.6f, 2.0f, 2.4f); LifeMax = At(v, 2.2f, 2.8f, 3.2f);
        SizeMin = At(v, 2.4f, 2.8f, 3.4f); SizeMax = At(v, 4.0f, 4.6f, 5.6f);

        Breakup = At(v, 5.5f, 2.2f, 0f);
        Ripple = At(v, 0.045f, 0.035f, 0.010f);
        Wave = At(v, 460f, 300f, 55f);
        Damping = At(v, 1.7f, 3.6f, 11f);
        Smooth = At(v, 0f, 500f, 7000f);
        Splash = At(v, 1f, 0.55f, 0.10f);
        Foam = At(v, 1f, 0.45f, 0f);
        FoamLife = At(v, 2.4f, 1.5f, 0.8f);
        Bubbles = At(v, 1f, 0.5f, 0.2f);
        Rise = At(v, 150f, 85f, 22f);
        Cling = At(v, 70f, 28f, 5f);
        Churn = At(v, 1f, 0.6f, 0.15f);
    }

    /// <summary>The drip emission this liquid sheds from a strand.</summary>
    public StrokeEmission Shed() => new(
        Role: PrimitiveRole.Goop,
        DensityPer100px: 0f,  // ignored: a drip block spaces its own sites
        SpeedMin: ReleaseMin, SpeedMax: ReleaseMax,
        LifespanMin: LifeMin, LifespanMax: LifeMax,
        SizeMin: SizeMin, SizeMax: SizeMax,
        SpreadRadians: 0f,
        BiasVelocity: Vector2.Zero,
        Gravity: new Vector2(0f, Gravity),
        Drip: new StrokeDripOptions(Spacing, CycleMin, CycleMax, MinSlope, Stringiness, Satellites, Drag));
}

/// <summary>A liquid: look, physics, and the shading table baked from the look.</summary>
public sealed class Liquid
{
    public static readonly Liquid Water = new(LiquidPresets.Water);

    public readonly LiquidSpec Look;
    public readonly LiquidPhysics Phys;
    public readonly LiquidMatcap Matcap;

    public Liquid(in LiquidSpec look)
    {
        Look = look;
        Phys = new LiquidPhysics(look.Viscosity);
        Matcap = new LiquidMatcap(in look);
    }

    /// <summary>The liquid a particle slot resolves to after overrides; water if it isn't one.</summary>
    public static Liquid Resolve(EffectScene scene, DebuffKind owner, PrimitiveRole role) =>
        MaterialRegistry.TryGetParticle(MaterialOverrideKey.ResolveParticle(owner, role, scene.Overrides)) is ParticleGoop g ? g.Liquid : Water;
}

/// <summary>Falling drops, each optionally trailing the thread it let go of; shared by GoopEmitter and LiquidField.</summary>
internal sealed class DropPool
{
    public struct Drop
    {
        public DebuffKind Owner;
        public Vector2 Pos, Vel, Gravity;
        public Vector2 Anchor, Pinch;  // where the string was fixed, and where it broke
        public float Born, Life;
        public float Size, Brightness;
        public float Tether;  // thickness of the string at the moment of release, fraction of Size
        public float RecoilTime;  // how long the string takes to snap back; 0 = no string
        public float Drag;
        public int   Seed;
        public int   HostSeed;  // the strand it came from, so it is drawn with that strand
        public int   Variant;  // GoopDraw.Speck for a droplet too small to mesh, GoopDraw.Free for one that wobbles as it falls
        public float Released;  // when it came loose (Free drops animate their shape from it)
        public string? Material;
        public Vector4? ColorOverride;
    }

    /// <summary>Asked of each drop after it moves; true removes it.</summary>
    public interface IHook { bool Hit(ref Drop d); }
    public struct NoHook : IHook { public bool Hit(ref Drop d) => false; }

    public readonly Drop[] Items;
    public int Count;

    public DropPool(int capacity) => Items = new Drop[capacity];

    public bool Full => Count >= Items.Length;

    public void Add(in Drop d) { if (Count < Items.Length) Items[Count++] = d; }

    /// <summary>Retires expired drops, then moves the rest.</summary>
    public void Advance<T>(float time, float dt, float floor, ref T hook) where T : struct, IHook
    {
        int w = 0;
        for (int i = 0; i < Count; i++)
        {
            ref Drop d = ref Items[i];
            if (time - d.Born >= d.Life) continue;
            d.Vel += d.Gravity * dt;
            d.Vel *= MathF.Exp(-d.Drag * dt);
            d.Pos += d.Vel * dt;
            if (d.Pos.Y > floor || hook.Hit(ref d)) continue;
            Items[w++] = d;
        }
        Count = w;
    }

    /// <summary>Hands every drop to the scene as a Goop particle; a broken string snaps back to its anchor.</summary>
    public void Emit(EffectScene scene, float time, bool hosted, float brightness = 1f)
    {
        for (int i = 0; i < Count; i++)
        {
            ref readonly Drop d = ref Items[i];
            float age = time - d.Born;
            float t01 = Math.Clamp(age / d.Life, 0f, 1f);

            var p = new ParticlePrimitive
            {
                Owner = d.Owner,
                Position = d.Pos,
                Velocity = d.Vel,
                Size = d.Size,
                AgeRatio = t01,
                Brightness = d.Brightness * brightness * ParticleEmitter.FadeFor(t01),
                Seed = d.Seed,
                HasHost = hosted, HostSeed = d.HostSeed,
                Role = PrimitiveRole.Goop,
                Variant = d.Variant,
                Age = time - d.Released,
                MaterialName = d.Material,
                ColorOverride = d.ColorOverride,
            };

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
}

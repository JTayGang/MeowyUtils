namespace RealDebuffs.Effects.Framework;

/// How a liquid moves, from one thickness axis (0 water, BloodAt blood, 1 slime); each value is calibrated at the three liquids and blended.
public readonly struct LiquidPhysics
{
    public const float BloodAt = 0.45f;

    // Piecewise-linear through the three calibrated liquids; exact at each one.
    static float At(float v, float water, float blood, float slime)
    {
        if (v <= 0f) return water;
        if (v < BloodAt) return Lerp(water, blood, (v / BloodAt));
        if (v == BloodAt) return blood;
        float t = (v - BloodAt) / (1f - BloodAt);
        return t >= 1f ? slime : Lerp(blood, slime, t);
    }

    public readonly float Viscosity;

    // drops and drips (px at 1080p, seconds)
    public readonly float Gravity, Drag, Stringiness, Satellites, MinSlope, Spacing;
    public readonly float CycleMin, CycleMax, ReleaseMin, ReleaseMax, LifeMin, LifeMax, SizeMin, SizeMax;

    // jets and pools
    public readonly float Breakup;  // 1/s: how fast a falling stream's ripples grow, at an 8 px radius
    public readonly float Ripple;  // size of the ripples a stream starts with
    public readonly float Wave, Damping, Smooth;  // pool surface: long-wave speed px/s at 140 px deep, drag 1/s, extra viscosity px^2/s
    public readonly float Splash, Foam, FoamLife;  // droplets thrown, foam laid down, seconds it lasts
    public readonly float Bubbles, Rise;  // bubbles dragged under, and how fast they climb (px/s)
    public readonly float Cling;  // how fast a bead runs down glass, px/s
    public readonly float Churn;  // how much a falling stream writhes and aerates

    // a film on a strand (Coat): px² per second per 100 px of strand, px of thickness, px/s, 1/s, seconds
    public readonly float Supply;  // matter arriving
    public readonly float DropArea;  // area a hanging drop holds before it necks (surface tension vs weight)
    public readonly float Hold;  // film thinner than this clings and does not run
    public readonly float MaxThick;  // most a film piles up before it bulges
    public readonly float Run;  // how fast a film at twice Hold runs down a vertical strand
    public readonly float Feed;  // how fast the underside hands matter to a hanging drop
    public readonly float Thread;  // how long a neck takes to thin to nothing under a full load
    public readonly float Stretch;  // how fast it lengthens meanwhile
    public readonly float Level;  // how fast a lump spreads out along a strand (1/s, per cell)
    public readonly float Index;  // power-law index n: 1 flows like water; below 1 it thins under strain (runs only on steep slopes, strings out long)
    public readonly float Absorb;  // how fast light is lost through a film (1/px of thickness): thick blood goes dark
    public readonly float WetMin;  // the film thickness, in holds, below which it will not wet a surface and leaves it bare: water beads off, slime clings
    public readonly float Bridge;  // chance a lengthening drop that touches another wet host sticks to it instead of falling

    public LiquidPhysics(float v)
    {
        v = Saturate(v);
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
        Wave = At(v, 650f, 424f, 78f);
        Damping = At(v, 2.4f, 4.2f, 11f);
        Smooth = At(v, 0f, 500f, 7000f);
        Splash = At(v, 1f, 0.55f, 0.10f);
        Foam = At(v, 1f, 0.45f, 0f);
        FoamLife = At(v, 2.4f, 1.5f, 0.8f);
        Bubbles = At(v, 1f, 0.5f, 0.2f);
        Rise = At(v, 150f, 85f, 22f);
        Cling = At(v, 70f, 28f, 5f);
        Churn = At(v, 1f, 0.6f, 0.15f);

        Supply = At(v, 26f, 46f, 70f);
        DropArea = At(v, 40f, 80f, 170f);
        Hold = At(v, 1.1f, 2.2f, 4.5f);
        MaxThick = At(v, 3.0f, 6.0f, 14.0f);
        Run = At(v, 150f, 60f, 16f);
        Feed = At(v, 1.6f, 1.0f, 0.6f);
        Thread = At(v, 0.10f, 0.55f, 1.9f);
        Stretch = At(v, 90f, 55f, 34f);
        Bridge = At(v, 0f, 0.1f, 0.4f);
        Level = At(v, 3f, 1.5f, 0.6f);
        Index = At(v, 1f, 0.7f, 0.25f);
        Absorb = At(v, 0f, 0.20f, 0.07f);
        WetMin = At(v, 0.34f, 0.24f, 0.14f);
    }

}

/// A liquid: look, physics, and the shading table baked from the look.
public sealed class Liquid
{
    public static readonly Liquid Water = new(LiquidPresets.Water);

    public readonly LiquidSpec Look;
    public readonly LiquidPhysics Phys;
    public readonly LiquidMatcap Matcap;

    public Liquid(in LiquidSpec look)
    {
        Look = look;
        Phys = new(look.Viscosity);
        Matcap = new(in look);
    }

    /// The liquid a particle slot resolves to after overrides; water if it isn't one.
    public static Liquid Resolve(EffectScene scene, DebuffKind owner, PrimitiveRole role) =>
        scene.Materials.Particle(owner, role) is ParticleGoop g ? g.Liquid : Water;
}

/// Falling drops, each optionally trailing the thread it let go of; shared by Coat and LiquidField.
internal sealed class DropPool
{
    public struct Drop
    {
        public DebuffKind Owner;
        public Vector2 Pos, Vel, Gravity;
        public Vector2 Anchor, Pinch;  // where the string was fixed, and where it broke
        public float Born, Life, Size, Brightness;
        public float Amount;  // the liquid it carries (px²); 0 for drops that don't track it
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

    /// Asked of each drop after it moves; true removes it.
    public interface IHook { bool Hit(ref Drop d); }
    public struct NoHook : IHook { public bool Hit(ref Drop d) => false; }

    public readonly Drop[] Items;
    public int Count;

    public DropPool(int capacity) => Items = new Drop[capacity];

    public bool Full => Count >= Items.Length;

    /// What expired or fell past the floor, by amount: a sink the owner can book.
    public double Left;

    /// False when the pool is full; the caller keeps the liquid.
    public bool Add(in Drop d)
    {
        if (Count >= Items.Length) return false;
        Items[Count++] = d;
        return true;
    }

    /// Retires expired drops, then moves the rest.
    public void Advance<T>(float time, float dt, float floor, ref T hook) where T : struct, IHook
    {
        int w = 0;
        for (int i = 0; i < Count; i++)
        {
            ref Drop d = ref Items[i];
            if (time - d.Born >= d.Life) { Left += d.Amount; continue; }
            d.Vel += d.Gravity * dt;
            d.Vel *= Exp(-d.Drag * dt);
            d.Pos += d.Vel * dt;
            if (d.Pos.Y > floor) { Left += d.Amount; continue; }
            if (hook.Hit(ref d)) continue;
            Items[w++] = d;
        }
        Count = w;
    }

    /// Hands every drop to the scene as a Goop particle; a broken string snaps back to its anchor.
    public void Emit(EffectScene scene, float time, bool hosted, float brightness = 1f)
    {
        for (int i = 0; i < Count; i++)
        {
            ref readonly Drop d = ref Items[i];
            float age = time - d.Born;
            float t01 = Saturate(age / d.Life);

            var p = new ParticlePrimitive
            {
                Owner = d.Owner,
                Position = d.Pos,
                Velocity = d.Vel,
                Size = d.Size,
                AgeRatio = t01,
                Brightness = d.Brightness * brightness * ParticleSystem.FadeFor(t01),
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

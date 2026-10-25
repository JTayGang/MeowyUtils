using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>One layer of flame tongues: ember variant, cycle seconds, tongues per site, size range (px at 1080p), start delay.</summary>
internal readonly record struct FlameLayer(int Variant, float Period, int PerSite, float SizeLo, float SizeHi, float Delay = 0f)
{
    // Variants understood by particle.ember.
    public const int Body = 0, Back = 1, Front = 2, Bed = 3;
}

internal static class Flames
{
    /// <summary>Stateless tongues at a site; each cycles birth to fade on its own phase, so nothing is stored.</summary>
    public static void Tongues(EffectScene scene, in FlameLayer layer, Vector2 at, int siteSeed, float time, float sizeMul,
                               Vector2 spread, float lean, float rise, float brightness, Vector4? color)
    {
        for (int t = 0; t < layer.PerSite; t++)
        {
            float cyc = time / (layer.Period * DrawHelpers.HashRange(siteSeed + 3, 0.85f, 1.15f))
                      + DrawHelpers.Hash01(siteSeed + 4 + t * 7) + t / (float)layer.PerSite;
            float life = cyc - MathF.Floor(cyc);
            int seed = siteSeed * 31 + (int)MathF.Floor(cyc) * 7 + t * 13;

            float size = DrawHelpers.HashRange(seed + 1, layer.SizeLo, layer.SizeHi) * sizeMul;
            scene.AddParticle(new ParticlePrimitive
            {
                Position = at + new Vector2(DrawHelpers.HashRange(seed + 2, -1f, 1f) * spread.X, DrawHelpers.HashRange(seed + 5, -1f, 1f) * spread.Y - size * rise),
                Velocity = new Vector2(DrawHelpers.HashRange(seed + 3, -12f, 12f) + lean, -30f),
                AgeRatio = life, Size = size, Brightness = ParticleEmitter.FadeFor(life) * brightness,
                Seed = seed, Role = PrimitiveRole.Ember, Variant = layer.Variant, ColorOverride = color,
            });
        }
    }
}

/// <summary>Cinders and smoke carried off a fire. The effect says where they leave from.</summary>
internal sealed class FireDrift
{
    private readonly ParticleEmitter _cinders, _smoke;
    private readonly Func<int, Vector2> _cinderPos, _smokePos, _cinderVel, _smokeVel;

    /// <summary>px scale, set each frame.</summary>
    public float Px = 1f;

    /// <summary>Upward cinder speed range (px/s at 1080p) and spawn-rate multipliers.</summary>
    public Vector2 CinderSpeed = new(90f, 300f);
    public float CinderRate = 1f, SmokeRate = 1f;

    public FireDrift(int salt, int cinderMax, int smokeMax, Func<int, Vector2> cinderPos, Func<int, Vector2> smokePos)
    {
        _cinders = new ParticleEmitter(cinderMax, seedSalt: salt) { Gravity = new Vector2(0f, -26f), Drag = 0.30f, Wander = 240f, WanderHz = 0.85f };
        _smoke = new ParticleEmitter(smokeMax, seedSalt: salt + 0x10) { Drag = 0.06f, Wander = 26f, WanderHz = 0.35f };
        _cinderPos = cinderPos;
        _smokePos = smokePos;
        _cinderVel = seed => new Vector2(DrawHelpers.HashRange(seed + 10, -55f, 55f), -DrawHelpers.HashRange(seed + 11, CinderSpeed.X, CinderSpeed.Y)) * Px;
        _smokeVel = seed => new Vector2(DrawHelpers.HashRange(seed + 10, -14f, 14f), -DrawHelpers.HashRange(seed + 11, 30f, 70f)) * Px;
    }

    public void Clear() { _cinders.Clear(); _smoke.Clear(); }

    public void BurstCinders(int count, float time)
        => _cinders.Burst(count, time, _cinderPos, _cinderVel, 1.1f, 2.6f, 1.9f * Px, 4.2f * Px);

    public void EmitSmoke(EffectScene scene, float time, float dt, float shortSide, float alpha, float fire, Vector4? color)
    {
        float k = SmokeRate * MathF.Max(0.2f, fire);
        _smoke.Update(time, dt, 0.18f / k, 0.38f / k, _smokePos, _smokeVel, 3.0f, 5.0f, 0.055f * shortSide, 0.105f * shortSide);
        if (fire <= 0.01f) { _smoke.Clear(); return; }
        _smoke.Emit(scene, time, PrimitiveRole.Smoke, brightnessMul: MathF.Min(1f, alpha * fire), colorOverride: color, swayPerParticle: 6f * Px);
    }

    public void EmitCinders(EffectScene scene, float time, float dt, float alpha, float fire, Vector4? color)
    {
        float k = CinderRate * MathF.Max(0.15f, fire);
        _cinders.Update(time, dt, 0.06f / k, 0.16f / k, _cinderPos, _cinderVel, 1.3f, 2.8f, 1.7f * Px, 3.6f * Px);
        if (fire <= 0.01f) { _cinders.Clear(); return; }
        _cinders.Emit(scene, time, PrimitiveRole.Cinder, brightnessMul: MathF.Min(1f, alpha * fire), colorOverride: color);
    }
}

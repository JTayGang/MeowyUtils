namespace RealDebuffs.Effects.Framework;

/// One layer of flame tongues: ember variant, cycle seconds, tongues per site, size range (px at 1080p), start delay.
internal readonly record struct FlameLayer(int Variant, float Period, int PerSite, float SizeLo, float SizeHi, float Delay = 0f)
{
    // Variants understood by particle.ember.
    public const int Body = 0, Back = 1, Front = 2, Bed = 3;
}

internal static class Flames
{
    /// Stateless tongues at a site; each cycles birth to fade on its own phase, so nothing is stored.
    public static void Tongues(EffectScene scene, in FlameLayer layer, Vector2 at, int siteSeed, float time, float sizeMul,
                               Vector2 spread, float lean, float rise, float brightness, Vector4? color, string? material = null)
    {
        for (int t = 0; t < layer.PerSite; t++)
        {
            float cyc = time / (layer.Period * HashRange(siteSeed + 3, 0.85f, 1.15f))
                      + Hash01(siteSeed + 4 + t * 7) + t / (float)layer.PerSite;
            float life = cyc - Floor(cyc);
            int seed = siteSeed * 31 + (int)Floor(cyc) * 7 + t * 13;

            float size = HashRange(seed + 1, layer.SizeLo, layer.SizeHi) * sizeMul;
            scene.AddParticle(new ParticlePrimitive
            {
                Position = at + new Vector2(HashRange(seed + 2, -1f, 1f) * spread.X, HashRange(seed + 5, -1f, 1f) * spread.Y - size * rise),
                Velocity = new(HashRange(seed + 3, -12f, 12f) + lean, -30f),
                AgeRatio = life, Size = size, Brightness = ParticleSystem.FadeFor(life) * brightness,
                Seed = seed, Role = PrimitiveRole.Ember, Variant = layer.Variant, ColorOverride = color, MaterialName = material,
            });
        }
    }
}

/// Cinders and smoke carried off a fire. The effect says where they leave from.
internal sealed class FireDrift
{
    readonly ParticleSystem _cinders, _smoke;
    readonly Func<int, Vector2> _cinderPos, _smokePos, _cinderVel, _smokeVel;

    /// px scale, set each frame.
    public float Px = 1f;

    /// Upward cinder speed range (px/s at 1080p) and spawn-rate multipliers.
    public Vector2 CinderSpeed = new(90f, 300f);
    public float CinderRate = 1f, SmokeRate = 1f;

    public FireDrift(int salt, int cinderMax, int smokeMax, Func<int, Vector2> cinderPos, Func<int, Vector2> smokePos)
    {
        _cinders = new(cinderMax, salt, new(new(0f, -26f), Drag: 0.30f, Wander: 240f, WanderHz: 0.85f));
        _smoke = new(smokeMax, salt + 0x10, new(Drag: 0.06f, Wander: 26f, WanderHz: 0.35f));
        _cinderPos = cinderPos;
        _smokePos = smokePos;
        _cinderVel = seed => new Vector2(HashRange(seed + 10, -55f, 55f), -HashRange(seed + 11, CinderSpeed.X, CinderSpeed.Y)) * Px;
        _smokeVel = seed => new Vector2(HashRange(seed + 10, -14f, 14f), -HashRange(seed + 11, 30f, 70f)) * Px;
    }

    public void Clear() { _cinders.Clear(); _smoke.Clear(); }

    public void BurstCinders(int count, float time)
        => _cinders.Burst(count, time, new ParticleSpawn(_cinderPos, _cinderVel, (1.1f, 2.6f), (1.9f * Px, 4.2f * Px)));

    public void EmitSmoke(EffectScene scene, float time, float dt, float shortSide, float alpha, float fire, Vector4? color)
    {
        float k = SmokeRate * Max(0.2f, fire);
        _smoke.Stream(time, dt, (0.18f / k, 0.38f / k), new ParticleSpawn(_smokePos, _smokeVel, (3.0f, 5.0f), (0.055f * shortSide, 0.105f * shortSide)));
        if (fire <= 0.01f) { _smoke.Clear(); return; }
        _smoke.Emit(scene, time, new ParticleStyle(PrimitiveRole.Smoke, Min(1f, alpha * fire), color, Sway: 6f * Px));
    }

    public void EmitCinders(EffectScene scene, float time, float dt, float alpha, float fire, Vector4? color)
    {
        float k = CinderRate * Max(0.15f, fire);
        _cinders.Stream(time, dt, (0.06f / k, 0.16f / k), new ParticleSpawn(_cinderPos, _cinderVel, (1.3f, 2.8f), (1.7f * Px, 3.6f * Px)));
        if (fire <= 0.01f) { _cinders.Clear(); return; }
        _cinders.Emit(scene, time, new ParticleStyle(PrimitiveRole.Cinder, Min(1f, alpha * fire), color));
    }
}

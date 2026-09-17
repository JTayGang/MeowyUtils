using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// A small pool of short-lived particles that spawn near the screen edges, drift, and fade out.
/// Shared by any effect that wants floating bits instead of every effect reimplementing its own
/// spawn/lifespan/drift bookkeeping (Silence's runes, Sleep's Zs, Poison's drips all use one of
/// these). Rendering is intentionally NOT baked in beyond the glyph convenience method below -
/// Poison, for example, draws its own droplet shape per particle instead.
/// </summary>
internal sealed class EdgeParticleField
{
    public struct Particle
    {
        public Vector2 Pos;
        public Vector2 Velocity;
        public float Born;
        public float Lifespan;
        public float Size;
        public string Glyph;
    }

    private readonly Particle[] _pool;
    private int _count;
    private float _nextSpawnAt;
    private readonly int _seedSalt;

    public EdgeParticleField(int maxParticles, int seedSalt)
    {
        _pool = new Particle[maxParticles];
        _seedSalt = seedSalt;
    }

    public int Count => _count;
    public ref readonly Particle this[int i] => ref _pool[i];

    /// <summary>0 at spawn/despawn, 1 for the steady middle of its life - use this to fade each particle in/out individually.</summary>
    public static float FadeFor(float age01) =>
        age01 < 0.25f ? age01 / 0.25f : age01 > 0.7f ? MathF.Max(0f, (1f - age01) / 0.3f) : 1f;

    /// <summary>Call once per frame while the owning effect is active (alpha &gt; 0). Spawns new particles and drops expired ones.</summary>
    public void Update(
        float time, float dt,
        float spawnIntervalMin, float spawnIntervalMax,
        Func<int, Vector2> spawnPos, Func<int, Vector2> spawnVelocity,
        Func<int, string> pickGlyph, float lifespanMin, float lifespanMax, float sizeMin, float sizeMax)
    {
        int w = 0;
        for (int i = 0; i < _count; i++)
        {
            if (time - _pool[i].Born < _pool[i].Lifespan)
                _pool[w++] = _pool[i];
        }
        _count = w;

        if (time >= _nextSpawnAt && _count < _pool.Length)
        {
            int seed = _seedSalt + (int)(time * 977f);
            _pool[_count] = new Particle
            {
                Pos = spawnPos(seed),
                Velocity = spawnVelocity(seed),
                Born = time,
                Lifespan = DrawHelpers.HashRange(seed + 1, lifespanMin, lifespanMax),
                Size = DrawHelpers.HashRange(seed + 2, sizeMin, sizeMax),
                Glyph = pickGlyph(seed + 3),
            };
            _count++;
            _nextSpawnAt = time + DrawHelpers.HashRange(seed + 4, spawnIntervalMin, spawnIntervalMax);
        }

        for (int i = 0; i < _count; i++)
            _pool[i].Pos += _pool[i].Velocity * dt;
    }

    /// <summary>Convenience renderer for glyph-based particles (used by Silence and Sleep). Effects that need a custom shape (Poison's drips) should iterate the indexer instead.</summary>
    public void DrawGlyphs(ImDrawListPtr dl, float time, uint color, float alpha)
    {
        for (int i = 0; i < _count; i++)
        {
            ref readonly var p = ref _pool[i];
            float fade = FadeFor((time - p.Born) / p.Lifespan);
            DrawHelpers.DrawGlowText(dl, p.Pos, p.Glyph, DrawHelpers.WithAlpha(color, alpha * fade), p.Size);
        }
    }
}

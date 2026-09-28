using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

public static class MaterialBootstrap
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered) return;
        _registered = true;

        // Region materials.
        MaterialRegistry.Register(new RegionFlatFill());
        MaterialRegistry.Register(new RegionEdgeGlow());

        // Particle materials.
        MaterialRegistry.Register(new ParticleEmber());
        MaterialRegistry.Register(new ParticleSnowflake());
        MaterialRegistry.Register(new ParticleSnow());
        MaterialRegistry.Register(new ParticleFog());
    }
}
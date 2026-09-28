using System;
using System.Collections.Generic;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Name-keyed registry for every material. Adding a material is "write a class, add a Register
/// call to the static constructor". Config stores names as strings, so no enum churn.
///
/// Registration runs from the static constructor, so it fires on the first touch of any
/// MaterialRegistry method (typically the first frame's renderer lookup). Callers never need
/// to invoke a separate init method - the previous MaterialBootstrap class is gone.
/// </summary>
public static class MaterialRegistry
{
    private static readonly Dictionary<string, IStrokeMaterial>   StrokeMaterials   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IParticleMaterial> ParticleMaterials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IRegionMaterial>   RegionMaterials   = new(StringComparer.OrdinalIgnoreCase);

    static MaterialRegistry()
    {
        // Region materials.
        Register(new RegionFlatFill());
        Register(new RegionEdgeGlow());

        // Stroke materials.
        Register(new StrokeSimple());
        Register(new StrokeChain());
        Register(new StrokeParasite());

        // Particle materials.
        Register(new ParticleEmber());
        Register(new ParticleSnowflake());
        Register(new ParticleSnow());
        Register(new ParticleFog());
        Register(new ParticleSpark());
        Register(new ParticleDrip());
    }

    public static void Register(IStrokeMaterial m)   => StrokeMaterials[m.Name] = m;
    public static void Register(IParticleMaterial m) => ParticleMaterials[m.Name] = m;
    public static void Register(IRegionMaterial m)   => RegionMaterials[m.Name] = m;

    public static IStrokeMaterial GetStroke(string name) =>
        StrokeMaterials.TryGetValue(name, out var m)
            ? m
            : throw new KeyNotFoundException($"No stroke material registered as '{name}'.");

    public static IParticleMaterial GetParticle(string name) =>
        ParticleMaterials.TryGetValue(name, out var m)
            ? m
            : throw new KeyNotFoundException($"No particle material registered as '{name}'.");

    public static IRegionMaterial GetRegion(string name) =>
        RegionMaterials.TryGetValue(name, out var m)
            ? m
            : throw new KeyNotFoundException($"No region material registered as '{name}'.");

    public static IStrokeMaterial? TryGetStroke(string name) =>
        StrokeMaterials.TryGetValue(name, out var m) ? m : null;

    public static IParticleMaterial? TryGetParticle(string name) =>
        ParticleMaterials.TryGetValue(name, out var m) ? m : null;

    public static IRegionMaterial? TryGetRegion(string name) =>
        RegionMaterials.TryGetValue(name, out var m) ? m : null;

    public static IEnumerable<string> StrokeNames   => StrokeMaterials.Keys;
    public static IEnumerable<string> ParticleNames => ParticleMaterials.Keys;
    public static IEnumerable<string> RegionNames   => RegionMaterials.Keys;
}
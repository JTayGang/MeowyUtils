using System;
using System.Collections.Generic;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Name-keyed registry for every material. Adding a material is "write a class, call Register".
/// Config stores names as strings, so no enum churn.
/// </summary>
public static class MaterialRegistry
{
    private static readonly Dictionary<string, IStrokeMaterial>   StrokeMaterials   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IParticleMaterial> ParticleMaterials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IRegionMaterial>   RegionMaterials   = new(StringComparer.OrdinalIgnoreCase);

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

    public static IEnumerable<string> StrokeNames   => StrokeMaterials.Keys;
    public static IEnumerable<string> ParticleNames => ParticleMaterials.Keys;
    public static IEnumerable<string> RegionNames   => RegionMaterials.Keys;
}
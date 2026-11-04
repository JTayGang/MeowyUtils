namespace RealDebuffs.Effects.Framework.Materials;

// Palette variants of the base materials, grouped by the effect theme they serve; a new variant is one line here.

internal sealed class PuffMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials => ParticlePuff.All;
}

internal sealed class HellMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials { get; } = new IMaterial[]
    {
        new RegionFirelight("region.hellglow", HellColor.Flame),
        new StrokeSigil("stroke.brand", HellColor.Glow, "particle.hellmote", "brand", "brands", "branded", "scorched"),
        new StrokeGlow("stroke.hellglow", HellColor.Glow, "hellglow", "hellglare", "brimglow"),
        new ParticleEmber("particle.hellfire", HellColor.Flame, "hellfire", "hellflame", "hellflames", "inferno", "infernal"),
        new ParticleRune("particle.hellrune", HellColor.Glow, RuneGlyphs.FirstInfernal, RuneGlyphs.InfernalCount, "occult", "hex", "hexes", "hexglyph"),
        new ParticleMote("particle.hellmote", HellColor.Glow, false, "emberlight", "fireflies", "firefly"),
    };
}

internal sealed class HauntMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials { get; } = new IMaterial[]
    {
        new StrokeSigil("stroke.haunt", HauntColor.Glow, "particle.spirit", "ghostlight", "spectral", "specter", "wraithlight"),
        new StrokeGlow("stroke.hauntglow", HauntColor.Glow, "hauntglow", "ghostglow", "pallor"),
        new ParticleRune("particle.omen", HauntColor.Glow, 0, RuneGlyphs.FirstSilence, "omen", "omens", "hallucination", "hallucinations"),
        new ParticleMote("particle.spirit", HauntColor.Glow, false, "spirit", "spirits", "soul", "souls", "wraith", "wraiths"),
    };
}

internal sealed class CharmMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials { get; } = new IMaterial[]
    {
        new ParticleGlint("particle.twinkle", CharmColor.Glow.Sample(0.99f), CharmColor.Glow.Sample(0.62f), "twinkle", "twinkles", "starlight"),
        new ParticleMote("particle.stardust", CharmColor.Glow, true, "stardust", "pixie", "pixies", "faerie", "faeries"),
    };
}

internal sealed class MemoryMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials { get; } = new IMaterial[]
    {
        new ParticleWhisper("particle.fugue", MemoryColor.Glow, MemoryColor.Ink, AmnesiaWhispers.Phrases, "fugue", "forgotten", "oblivion", "mumbling"),
        new ParticleMote("particle.lethe", MemoryColor.Glow, true, "lethe", "memory", "memories", "recollections"),
    };
}

internal sealed class StormMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials { get; } = new IMaterial[]
    {
        new StrokeGlow("stroke.corona", StormColor.Glow, "corona", "flare", "flares"),
        new ParticleMote("particle.static", StormColor.Glow, false, "static", "plasma", "ions"),
    };
}

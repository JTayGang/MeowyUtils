using Dalamud.Configuration;

namespace StatusBridge;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Master on/off switch for all mirroring.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Mirror statuses you create in Moodles over to Loci.</summary>
    public bool MirrorMoodlesToLoci { get; set; } = true;

    /// <summary>Mirror statuses you create in Loci over to Moodles.</summary>
    public bool MirrorLociToMoodles { get; set; } = true;

    /// <summary>
    /// Extra heuristic: before creating a new mirror, skip it if the destination already has a
    /// status (not one of ours) with the exact same title. Off by default because it's a fuzzy
    /// text match and can hide two genuinely-different statuses that happen to share a name.
    /// </summary>
    public bool SkipIfMatchingTitleExists { get; set; }

    /// <summary>Verbose per-action logging to the Dalamud log, useful for confirming it's working.</summary>
    public bool VerboseLogging { get; set; }

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
}

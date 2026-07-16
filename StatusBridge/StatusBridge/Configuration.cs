using Dalamud.Configuration;

namespace StatusBridge;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    /// <summary>
    /// Bump this whenever a persisted default changes in a way that needs a one-time migration
    /// for configs saved under an older version - see <see cref="MigrateIfNeeded"/>.
    /// </summary>
    private const int LatestVersion = 2;

    public int Version { get; set; } = LatestVersion;

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

    /// <summary>
    /// Reaches into Moodles' loaded assembly via reflection and flips an internal flag that
    /// fixes a Moodles-side icon-offset miscalculation whenever Loci's icons render before
    /// Moodles' do on a given client. On by default as of Version 2 - testers ran it opt-in for
    /// a while with consistently good results, so new and existing installs now get the fix
    /// automatically; uncheck it in Settings -> Experimental to opt back out. Still touches
    /// unversioned internals with no contract protecting it - see
    /// Experimental/MoodlesOffsetPatch.cs and the README for the full explanation.
    /// </summary>
    public bool EnableExperimentalMoodlesOffsetFix { get; set; } = true;

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);

    /// <summary>
    /// One-time migration for configs saved before <see cref="EnableExperimentalMoodlesOffsetFix"/>
    /// defaulted to true. Without this, only brand-new installs (no config file yet) would pick
    /// up the new default - anyone who has ever saved a config under Version 1 already has an
    /// explicit "false" written for it (that was the only default that ever existed before now)
    /// and would keep silently overriding the new default forever. Note this can't tell "never
    /// touched this setting" apart from "explicitly turned it off" - both serialize the same way
    /// - so it forces everyone on Version 1 forward rather than guessing. If some testers turned
    /// it off on purpose, they'll need to flip it back off post-update (or switch this field to
    /// a nullable bool if you'd rather a deliberate opt-out survive the migration instead).
    /// </summary>
    public void MigrateIfNeeded()
    {
        if (Version >= LatestVersion)
            return;

        EnableExperimentalMoodlesOffsetFix = true;
        Version = LatestVersion;
        Save();
    }
}

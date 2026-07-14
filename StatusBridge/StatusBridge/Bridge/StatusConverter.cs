namespace StatusBridge.Bridge;

/// <summary>
/// Field-level snapshot of a status used purely for cheap "did anything meaningful change"
/// comparisons. Deliberately excludes GUID (that's the dictionary key already), Version
/// (unvalidated by either plugin), and every chain-related field (never mirrored - see
/// StatusConverter's remarks on why).
/// </summary>
internal readonly record struct BridgeSignature(
    long IconID,
    string Title,
    string Description,
    string CustomVFXPath,
    long ExpireTicks,
    int Type,
    int Stacks,
    int StackSteps,
    uint Modifiers,
    string Applier,
    string Dispeller)
{
    public static BridgeSignature FromMoodles(MoodlesStatusInfo m) => new(
        m.IconID, m.Title, m.Description, m.CustomVFXPath, m.ExpireTicks,
        m.Type, m.Stacks, m.StackSteps, m.Modifiers, m.Applier, m.Dispeller);

    public static BridgeSignature FromLoci(LociStatusInfo l) => new(
        l.IconID, l.Title, l.Description, l.CustomVFXPath, l.ExpireTicks,
        l.Type, l.Stacks, l.StackSteps, l.Modifiers, l.Applier, l.Dispeller);
}

/// <summary>
/// Converts statuses between Moodles' and Loci's tuple shapes. Both systems agree closely on
/// units and enum values (StatusType and the first 7 Modifiers flags are numerically identical,
/// ExpireTicks is milliseconds-with-(-1)-meaning-permanent in both), so most fields are a
/// straight copy. The two known gaps, documented rather than papered over:
///
///   - Chain-trigger data (ChainedStatus/ChainedGUID, ChainTrigger, ChainType, StackToChain)
///     references GUIDs from the *source* plugin's own saved-status library. Copying it across
///     verbatim would point at nothing (or, astronomically unlikely, at an unrelated status
///     that happens to share a GUID). We strip it on every mirror.
///
///   - Moodles' "Permanent" (Sticky) flag has no equivalent slot in Loci's tuple - Loci only
///     has the ExpireTicks==-1 "no timer" concept, which is already carried over via ExpireTicks
///     itself. Mirroring Loci -> Moodles always produces Permanent = false.
/// </summary>
internal static class StatusConverter
{
    public static LociStatusInfo ToLoci(MoodlesStatusInfo m) => (
        Version: 1,
        GUID: m.GUID,
        IconID: (uint)Math.Max(0, m.IconID),
        Title: m.Title,
        Description: m.Description,
        CustomVFXPath: m.CustomVFXPath,
        ExpireTicks: m.ExpireTicks,
        Type: (byte)Math.Clamp(m.Type, 0, 2),
        Stacks: m.Stacks,
        StackSteps: m.StackSteps,
        StackToChain: 0,
        Modifiers: m.Modifiers,
        ChainedGUID: Guid.Empty,
        ChainType: 0,
        ChainTrigger: 0,
        Applier: m.Applier,
        Dispeller: m.Dispeller
    );

    public static MoodlesStatusInfo ToMoodles(LociStatusInfo l) => (
        Version: 1,
        GUID: l.GUID,
        IconID: (int)l.IconID,
        Title: l.Title,
        Description: l.Description,
        CustomVFXPath: l.CustomVFXPath,
        ExpireTicks: l.ExpireTicks,
        Type: Math.Clamp((int)l.Type, 0, 2),
        Stacks: l.Stacks,
        StackSteps: l.StackSteps,
        Modifiers: l.Modifiers,
        ChainedStatus: Guid.Empty,
        ChainTrigger: 0,
        Applier: l.Applier,
        Dispeller: l.Dispeller,
        Permanent: false
    );
}

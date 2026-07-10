// -----------------------------------------------------------------------------------------
// Locally-declared mirror of Moodles' IPC tuple shape.
//
// Moodles defines its real tuple in its own project as:
//
//   global using MoodlesStatusInfo = (
//       int Version, Guid GUID, int IconID, string Title, string Description,
//       string CustomVFXPath, long ExpireTicks, Moodles.Data.StatusType Type,
//       int Stacks, int StackSteps, uint Modifiers, Guid ChainedStatus,
//       Moodles.Data.ChainTrigger ChainTrigger, string Applier, string Dispeller,
//       bool Permanent
//   );
//
// We can't reference Moodles.Data.StatusType / Moodles.Data.ChainTrigger directly without
// taking a full ProjectReference on Moodles' main assembly (which would drag in ECommons,
// OtterGui, MemoryPack, ImGui bindings, etc. for no benefit). Instead we substitute plain
// `int` for those two fields. This is exactly the pattern Loci's own MoodlesWatcher.cs uses
// to read Moodles IPC data (see Loci/Services/MoodlesWatcher.cs upstream), and it works
// because Dalamud's tuple-based IPC round-trips through Newtonsoft.Json rather than requiring
// identical CLR types on both ends of the call - Moodles' own IPCTypedef.cs even notes this
// explicitly ("dalamud's Newtonsoft parsing does not play nice with [Flag] Enums... ").
//
// Field order below MUST exactly match Moodles' real tuple - it's positional.
// -----------------------------------------------------------------------------------------

global using MoodlesStatusInfo = (
    int Version,
    System.Guid GUID,
    int IconID,
    string Title,
    string Description,
    string CustomVFXPath,
    long ExpireTicks,      // milliseconds remaining; -1 means "no expiry"
    int Type,               // Moodles.Data.StatusType: Positive=0, Negative=1, Special=2
    int Stacks,
    int StackSteps,
    uint Modifiers,          // bit flags, see MoodlesModifiers below
    System.Guid ChainedStatus,
    int ChainTrigger,       // Moodles.Data.ChainTrigger: Dispel=0, HitMaxStacks=1, TimerExpired=2
    string Applier,
    string Dispeller,
    bool Permanent           // Moodles' "Sticky" flag - distinct from ExpireTicks == -1
);

namespace StatusBridge.Interop;

/// <summary>Mirrors Moodles.Data.StatusType (Moodles/Data/Enums/StatusType.cs) for readability only.</summary>
internal enum MoodlesStatusType
{
    Positive = 0,
    Negative = 1,
    Special = 2
}

/// <summary>Mirrors Moodles.Data.ChainTrigger (Moodles/Data/Enums/ChainTrigger.cs) for readability only.</summary>
internal enum MoodlesChainTrigger
{
    Dispel = 0,
    HitMaxStacks = 1,
    TimerExpired = 2
}

/// <summary>Mirrors Moodles.Data.Modifiers (Moodles/Data/Enums/Modifiers.cs) for readability only. Same bit layout as Loci's.</summary>
[Flags]
internal enum MoodlesModifiers : uint
{
    None = 0,
    CanDispel = 1u << 0,
    StacksIncrease = 1u << 1,
    StacksRollOver = 1u << 2,
    PersistExpireTime = 1u << 3,
    StacksMoveToChain = 1u << 4,
    StacksCarryToChain = 1u << 5,
    PersistAfterTrigger = 1u << 6
}

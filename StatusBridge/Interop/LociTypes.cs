// -----------------------------------------------------------------------------------------
// Locally-declared mirror of Loci's IPC tuple shape.
//
// Loci's real tuple (Loci.Api / LociTypeDef.cs, a separate shared project Loci publishes
// specifically so other plugins can reference the exact same enum types) is:
//
//   global using LociStatusInfo = (
//       int Version, Guid GUID, uint IconID, string Title, string Description,
//       string CustomVFXPath, long ExpireTicks, LociApi.Enums.StatusType Type,
//       int Stacks, int StackSteps, int StackToChain, uint Modifiers, Guid ChainedGUID,
//       LociApi.Enums.ChainType ChainType, LociApi.Enums.ChainTrigger ChainTrigger,
//       string Applier, string Dispeller
//   );
//
// We deliberately do NOT take a submodule/ProjectReference on Loci.Api here (no LICENSE file
// is published in either the Loci or Loci.Api repos at the time of writing, so we avoid
// redistributing any of their source and instead write everything from scratch). Same
// reasoning and substitution approach as MoodlesTypes.cs: swap each custom enum field for its
// underlying primitive type. Loci's own enums are explicitly sized (`: byte`, `: int`), so we
// match those exact widths below rather than defaulting everything to `int`.
//
// Field order below MUST exactly match Loci's real tuple - it's positional.
// -----------------------------------------------------------------------------------------

global using LociStatusInfo = (
    int Version,
    System.Guid GUID,
    uint IconID,
    string Title,
    string Description,
    string CustomVFXPath,
    long ExpireTicks,      // milliseconds remaining; -1 means "no expiry"
    byte Type,               // LociApi.Enums.StatusType : byte -> Positive=0, Negative=1, Special=2
    int Stacks,
    int StackSteps,
    int StackToChain,
    uint Modifiers,          // bit flags, see LociModifiers below - same layout as Moodles'
    System.Guid ChainedGUID,
    byte ChainType,          // LociApi.Enums.ChainType : byte -> Status=0, Preset=1, Event=2
    int ChainTrigger,       // LociApi.Enums.ChainTrigger : int -> Dispel=0..ClickedOff=4
    string Applier,
    string Dispeller
);

namespace StatusBridge.Interop;

/// <summary>Mirrors LociApi.Enums.StatusType for readability only.</summary>
internal enum LociStatusType : byte
{
    Positive = 0,
    Negative = 1,
    Special = 2
}

/// <summary>Mirrors LociApi.Enums.ChainTrigger for readability only.</summary>
internal enum LociChainTrigger
{
    Dispel = 0,
    HitMaxStacks = 1,
    TimerExpired = 2,
    HitSetStacks = 3,
    ClickedOff = 4
}

/// <summary>Mirrors LociApi.Enums.ChainType for readability only.</summary>
internal enum LociChainType : byte
{
    Status = 0,
    Preset = 1,
    Event = 2
}

/// <summary>Mirrors LociApi.Enums.Modifiers for readability only. Same bit layout as Moodles'.</summary>
[Flags]
internal enum LociModifiers : uint
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

/// <summary>Mirrors LociApi.Enums.LociApiEc for readability only (diagnostic logging of return codes).</summary>
internal enum LociApiEc
{
    Success = 0,
    NoChange = 1,
    PartialSuccess = 2,
    TargetNotFound = 3,
    TargetInvalid = 4,
    DataNotFound = 5,
    DataInvalid = 6,
    ItemLocked = 7,
    InvalidKey = 8,
    ItemIsPersistent = 9,
    ClientForbidden = 10,
    FSPathFaulted = 11,
    UnkError = int.MaxValue
}

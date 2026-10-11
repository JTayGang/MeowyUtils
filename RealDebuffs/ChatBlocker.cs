using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Shell;

namespace RealDebuffs;

/// <summary>
/// Optional (see Configuration.SilenceBlocksChat): swallows outgoing chat while silenced, by
/// hooking the game's post-Enter chat-input processor. Credit: Project GagSpeak
/// (github.com/Project-GagSpeak/client), GameInternals/Signatures.cs. An earlier version hooked
/// UIModule.ProcessChatBoxEntry - the function programmatic helpers call INTO, not the one the
/// game itself calls on Enter - so it silently did nothing.
///
/// Swallowed messages are counted (BlockedCount) so effects can react to them; see IReactsToBlockedChat.
///
/// Signature hooks can stop resolving after a game patch. Fails safe: logs a warning once and
/// leaves _hook null; every visual effect is unaffected either way.
/// </summary>
public sealed unsafe class ChatBlocker : IDisposable
{
    private const string ProcessChatInputSignature = "E8 ?? ?? ?? ?? FE 87 ?? ?? ?? ?? C7 87";

    private delegate void ProcessChatInputDelegate(ShellCommandModule* self, Utf8String* message, UIModule* uiModule);

    private readonly IPluginLog _log;
    private readonly Hook<ProcessChatInputDelegate>? _hook;
    private volatile bool _silenced;
    private int _blockedCount;

    public ChatBlocker(IGameInteropProvider hooks, IPluginLog log)
    {
        _log = log;
        try
        {
            _hook = hooks.HookFromSignature<ProcessChatInputDelegate>(ProcessChatInputSignature, Detour);
            _hook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "RealDebuffs: couldn't hook the chat-input function (game may have updated). " +
                              "The hard chat-block option will stay a no-op.");
            _hook = null;
        }
    }

    /// <summary>Set every frame by EffectManager from config + active statuses.</summary>
    public void SetSilenced(bool silenced) => _silenced = silenced;

    /// <summary>
    /// How many outgoing messages have been swallowed so far. A counter rather than an event: the hook runs inside
    /// the game, so all it does is one lock-free increment, and EffectManager polls the difference each frame to
    /// tell effects (see IReactsToBlockedChat) that a message just bounced.
    /// </summary>
    public int BlockedCount => Volatile.Read(ref _blockedCount);

    private void Detour(ShellCommandModule* self, Utf8String* message, UIModule* uiModule)
    {
        // Slash commands go through this same function, so they're blocked too. To let them
        // through, check message->ToString() for a leading '/' here and call Original() for those.
        if (_silenced)
        {
            Interlocked.Increment(ref _blockedCount);
            return;
        }
        _hook!.Original(self, message, uiModule);
    }

    public void Dispose()
    {
        _hook?.Disable();
        _hook?.Dispose();
    }
}
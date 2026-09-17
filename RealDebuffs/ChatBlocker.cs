using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace RealDebuffs;

/// <summary>
/// Optional, OFF BY DEFAULT (see Configuration.SilenceBlocksChat): actually stops outgoing chat
/// while Silenced, instead of only showing SilenceEffect's visual. Works by hooking the game's own
/// chat-submit function - <c>UIModule.ProcessChatBoxEntry</c> - and swallowing the call while
/// silenced instead of forwarding it to the game. This is the exact same native function every
/// "send chat from code" helper calls INTO (see e.g. ECommons' Automation/Chat.cs or ChatTwo's
/// GameFunctions/ChatBox.cs); here we intercept calls the other direction, when the *game* invokes
/// it because the player pressed Enter with text in the chat box.
///
/// ADVANCED / here be dragons: this hooks a raw byte-pattern signature into the game's own code -
/// a completely normal technique for Dalamud plugins, but like ANY signature hook it can stop
/// resolving after a game patch until someone re-derives the new bytes. The signature below is
/// copied directly from FFXIVClientStructs' UIModule.cs (github.com/aers/FFXIVClientStructs) as of
/// this writing, since that project tracks game patches closely and is the same source ECommons
/// and ChatTwo build on - re-check that file first if this ever needs updating.
///
/// This class fails safe: if the signature doesn't resolve (e.g. after an unpatched game update),
/// the constructor logs a warning once and leaves the hook null - every visual effect in the
/// plugin, including SilenceEffect itself, keeps working completely normally regardless.
/// </summary>
public sealed unsafe class ChatBlocker : IDisposable
{
    private const string ProcessChatBoxEntrySignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F2 48 8B F9 45 84 C9";

    private delegate void ProcessChatBoxEntryDelegate(UIModule* self, Utf8String* message, nint a4, byte saveToHistory);

    private readonly IPluginLog _log;
    private readonly Hook<ProcessChatBoxEntryDelegate>? _hook;
    private volatile bool _silenced;

    public ChatBlocker(IGameInteropProvider hooks, IPluginLog log)
    {
        _log = log;

        try
        {
            _hook = hooks.HookFromSignature<ProcessChatBoxEntryDelegate>(ProcessChatBoxEntrySignature, Detour);
            _hook.Enable();
        }
        catch (Exception ex)
        {
            // If IGameInteropProvider.HookFromSignature isn't the exact method name on your
            // installed Dalamud version, ECommons' DalamudServices/Legacy/SignatureHelper.cs shows
            // the equivalent attribute-based pattern ([Signature(...)] field + InitializeFromAttributes)
            // as a drop-in alternative - same signature string, different wiring.
            _log.Warning(ex, "RealDebuffs: couldn't hook the chat-send function (game may have updated, " +
                              "or the hook API shape changed). The hard chat-block option will stay a " +
                              "no-op; every visual effect, including Silence's, is unaffected.");
            _hook = null;
        }
    }

    /// <summary>Whether outgoing chat should currently be swallowed. Set every frame from EffectManager based on config + active statuses.</summary>
    public void SetSilenced(bool silenced) => _silenced = silenced;

    private void Detour(UIModule* self, Utf8String* message, nint a4, byte saveToHistory)
    {
        if (_silenced)
        {
            // Swallowed: whatever was typed never reaches the server. Slash commands go through
            // this same function, so they're blocked too - if you'd rather let commands (macros,
            // targeting, etc) through while silenced, check message->ToString() for a leading '/'
            // here and call Original() for those.
            return;
        }

        _hook!.Original(self, message, a4, saveToHistory);
    }

    public void Dispose()
    {
        _hook?.Disable();
        _hook?.Dispose();
    }
}

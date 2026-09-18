using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Shell;

namespace RealDebuffs;

/// <summary>
/// Optional, OFF BY DEFAULT (see Configuration.SilenceBlocksChat): actually stops outgoing chat
/// while Silenced, instead of only showing SilenceEffect's visual. Works by hooking
/// <c>ShellCommandModule</c>'s chat-input processor - the function that runs right after the
/// player presses Enter in the chat box, before the typed text is evaluated as a command or sent
/// as a message - and swallowing the call while silenced instead of forwarding it.
///
/// CORRECTED: an earlier version of this hooked <c>UIModule.ProcessChatBoxEntry</c> instead (the
/// same function ECommons' Automation/Chat.cs and similar "send chat from code" helpers call
/// *into* to inject a message programmatically). That function is real and that hook resolved
/// fine, but it turns out not to be the function the game itself calls when a player actually
/// types and hits Enter - hooking it silently intercepted nothing, which is why chat kept working
/// with the block "on". Retargeted against Project GagSpeak's client
/// (github.com/Project-GagSpeak/client), whose chat garbler needs exactly this same interception
/// point and confirms it works against real keyboard input:
/// GameInternals/Detours/Static/StaticDetours.ChatInput.cs and GameInternals/Signatures.cs.
///
/// ADVANCED / here be dragons: this hooks a raw byte-pattern signature into the game's own code -
/// a completely normal technique for Dalamud plugins, but like ANY signature hook it can stop
/// resolving after a game patch until someone re-derives the new bytes. This class fails safe: if
/// the signature doesn't resolve, the constructor logs a warning once and leaves the hook null -
/// every visual effect in the plugin, including SilenceEffect itself, is unaffected either way.
/// </summary>
public sealed unsafe class ChatBlocker : IDisposable
{
    // Credit: Project GagSpeak (github.com/Project-GagSpeak/client), GameInternals/Signatures.cs.
    private const string ProcessChatInputSignature = "E8 ?? ?? ?? ?? FE 87 ?? ?? ?? ?? C7 87";

    private delegate void ProcessChatInputDelegate(ShellCommandModule* self, Utf8String* message, UIModule* uiModule);

    private readonly IPluginLog _log;
    private readonly Hook<ProcessChatInputDelegate>? _hook;
    private volatile bool _silenced;

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
                              "The hard chat-block option will stay a no-op; every visual effect, " +
                              "including Silence's, is unaffected.");
            _hook = null;
        }
    }

    /// <summary>Whether outgoing chat should currently be swallowed. Set every frame from EffectManager based on config + active statuses.</summary>
    public void SetSilenced(bool silenced) => _silenced = silenced;

    private void Detour(ShellCommandModule* self, Utf8String* message, UIModule* uiModule)
    {
        if (_silenced)
        {
            // Swallowed: whatever was typed never reaches the server. Slash commands go through
            // this same function, so they're blocked too - if you'd rather let commands (macros,
            // targeting, etc) through while silenced, check message->ToString() for a leading '/'
            // here and call Original() for those.
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

using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace StatusBridge;

/// <summary>
/// Standard Dalamud service locator, populated via <see cref="IDalamudPluginInterface.Create{T}"/>
/// in Plugin's constructor. Trimmed down to just what this placeholder build needs - see git
/// history for the fuller service list the real feature set used (ObjectTable, Framework,
/// CommandManager, etc.), and restore as needed when that comes back.
/// </summary>
internal class Svc
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] public static IPluginLog Log { get; set; } = null!;
}

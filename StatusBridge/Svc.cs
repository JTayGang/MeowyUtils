using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace StatusBridge;

/// <summary>
/// Standard Dalamud service locator. Populated via <see cref="IDalamudPluginInterface.Create{T}"/>
/// in Plugin's constructor.
/// </summary>
internal class Svc
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; set; } = null!;
    [PluginService] public static IPluginLog Log { get; set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] public static IFramework Framework { get; set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; set; } = null!;
}

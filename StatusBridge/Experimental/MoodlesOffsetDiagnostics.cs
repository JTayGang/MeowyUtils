using System.Reflection;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace StatusBridge.Experimental;

/// <summary>
/// EXPERIMENTAL, READ-ONLY. Exists purely to make the bug <see cref="MoodlesOffsetPatch"/>
/// works around directly observable instead of something you have to infer from icon position
/// by eye.
///
/// Reads two numbers for your current target:
///   - <c>MoodlesScannedCount</c>: what Moodles' own visual-scan heuristic currently believes is
///     the vanilla status count (<c>Moodles.GameGuiProcessors.TargetInfoProcessor.NumStatuses</c>,
///     reached the same way <see cref="MoodlesOffsetPatch"/> reaches <c>CommonProcessor</c>, one
///     field deeper).
///   - <c>RealVanillaCount</c>: the actual vanilla status count from Dalamud's own
///     <c>IPlayerCharacter.StatusList</c> - the same source Moodles' own "else" branch uses.
///
/// If these two numbers differ while you're targeting a character with Loci icons showing, the
/// miscount is actively happening on your client, for that target, right now - independent of
/// whether the patch itself is enabled. If they match, either there's no Loci icon in the mix
/// for this target, or your client's hook ordering happens to be the lucky one this session -
/// either way, nothing to fix for this particular observation.
///
/// This only ever reads. It never calls SetValue on anything. Same fragility disclaimer as
/// MoodlesOffsetPatch: touches unversioned internals, fails soft, logs nothing on its own since
/// it's meant to be polled interactively from the settings window rather than run in the
/// background.
/// </summary>
internal sealed class MoodlesOffsetDiagnostics
{
    private const string AssemblyName = "Moodles";
    private const string PluginTypeName = "Moodles.Moodles";

    public bool TryCompare(out int moodlesScannedCount, out int realVanillaCount, out string status)
    {
        moodlesScannedCount = 0;
        realVanillaCount = 0;

        if (Svc.TargetManager.Target is not IPlayerCharacter target)
        {
            status = "Target a player character to compare counts";
            return false;
        }

        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == AssemblyName);
            if (assembly == null)
            {
                status = "Moodles assembly not found (not loaded, or not installed)";
                return false;
            }

            var pluginType = assembly.GetType(PluginTypeName);
            var pluginInstance = pluginType?.GetField("P", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var commonProcessor = pluginType?.GetField("CommonProcessor", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(pluginInstance);
            if (commonProcessor == null)
            {
                status = "Moodles' structure has changed - diagnostic unavailable";
                return false;
            }

            var targetInfoProcessor = commonProcessor.GetType()
                .GetField("TargetInfoProcessor", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(commonProcessor);
            var numStatusesField = targetInfoProcessor?.GetType()
                .GetField("NumStatuses", BindingFlags.Public | BindingFlags.Instance);
            if (targetInfoProcessor == null || numStatusesField == null)
            {
                status = "Moodles' structure has changed - diagnostic unavailable";
                return false;
            }

            moodlesScannedCount = (int)numStatusesField.GetValue(targetInfoProcessor)!;
            realVanillaCount = target.StatusList.Count(x => x.StatusId != 0);
            status = moodlesScannedCount == realVanillaCount
                ? "Counts match - nothing wrong for this target right now"
                : "MISMATCH - Moodles is currently miscounting this target's icons";
            return true;
        }
        catch (Exception e)
        {
            status = $"Failed: {e.GetType().Name}";
            return false;
        }
    }
}

using System.Reflection;

namespace StatusBridge.Experimental;

/// <summary>
/// EXPERIMENTAL. ON BY DEFAULT AS OF CONFIG VERSION 2. NOT A SUPPORTED INTEGRATION.
///
/// Reaches into Moodles' own loaded assembly via reflection and flips an internal flag that
/// controls how Moodles decides where to draw its own status icons relative to the game's
/// native ones.
///
/// <para><b>Background:</b> Moodles has two ways to compute that offset (see
/// Moodles/GameGuiProcessors/TargetInfoProcessor.cs and four sibling processor files in
/// Moodles' own repo). The default (<c>CommonProcessor.NewMethod = true</c>) visually scans the
/// shared native icon UI region and counts populated slots, assuming anything visible there is
/// a vanilla game status. That miscounts any icon another plugin has already drawn into that
/// same region - specifically Loci, if Loci's render hook happens to run before Moodles' does
/// on a given client that session. The other path (<c>NewMethod = false</c>) asks Dalamud's own
/// <c>IPlayerCharacter.StatusList</c> for the real, authoritative vanilla status count instead
/// of visually scanning shared UI, which sidesteps the problem entirely since it never looks at
/// the shared icon region at all. Both paths already exist and are already wired up inside
/// Moodles' own code - this patch does not add new behavior to Moodles, it just flips which of
/// Moodles' own existing paths gets used.</para>
///
/// <para><b>Why this stays labeled Experimental despite being on by default:</b> every step
/// below touches Moodles' internal, unversioned implementation details - a public static
/// self-reference, and two public instance fields - with zero contract protecting any of it. It
/// will silently stop doing anything useful (safely - see below) the moment Moodles renames or
/// restructures any of: the main plugin class, the "P" static field, the "CommonProcessor"
/// field, or the "NewMethod" field. It's also broader in effect than the name suggests: flipping
/// this changes how Moodles computes its icon offset everywhere, for every character, all the
/// time - not just for the specific Loci-overlap case - and we still don't actually know why
/// Moodles' own developer chose <c>NewMethod = true</c> as the default. If the older path has
/// some other limitation that motivated the switch away from it, this patch quietly reintroduces
/// that limitation for everyone now, not just people who went looking for it. It moved from
/// opt-in to on-by-default after testers ran it for a while with consistently good results, but
/// "no reports of the Loci-overlap fix backfiring" isn't the same claim as "we know what
/// NewMethod=true was protecting against" - this exists as a stopgap because Moodles'
/// maintainer isn't currently reviewing outside changes, not a substitute for a real fix on
/// their end, and Settings -> Experimental still has a checkbox to turn it back off per-user.</para>
///
/// <para><b>Failure mode by design:</b> every reflection step is individually null-checked
/// before use, with a specific, distinguishable <see cref="Status"/> message per failure point,
/// and the whole thing is wrapped in try/catch besides. If Moodles changes shape, this fails
/// soft - it leaves Moodles at whatever its own default behavior is and reports why in
/// <see cref="Status"/> - it does not throw into caller code or leave anything partially
/// modified.</para>
/// </summary>
internal sealed class MoodlesOffsetPatch
{
    private const string AssemblyName = "Moodles";
    private const string PluginTypeName = "Moodles.Moodles";
    private const string StaticInstanceFieldName = "P";
    private const string ProcessorFieldName = "CommonProcessor";
    private const string FlagFieldName = "NewMethod";

    /// <summary>Human-readable outcome of the last apply/revert attempt, for display in the settings UI.</summary>
    public string Status { get; private set; } = "Not yet attempted";

    /// <summary>True only once we've successfully set the flag to false ourselves.</summary>
    public bool IsApplied { get; private set; }

    /// <summary>Sets Moodles' offset calculation to the StatusList-based (Loci-safe) path.</summary>
    public bool TryApply()
    {
        try
        {
            var field = FindFlagField(out var processorInstance);
            if (field == null || processorInstance == null)
            {
                IsApplied = false;
                return false; // Status already set by FindFlagField with the specific reason.
            }

            field.SetValue(processorInstance, false);

            // Read back rather than trusting SetValue not throwing - cheap, and it's the
            // difference between "the write call completed" and "the value is actually false".
            var confirmedValue = (bool)field.GetValue(processorInstance)!;
            if (!confirmedValue)
            {
                IsApplied = true;
                Status = "Applied (confirmed: NewMethod = false)";
                Svc.Log.Information("[StatusBridge] Experimental Moodles offset patch applied and confirmed.");
                return true;
            }

            IsApplied = false;
            Status = "Set the value but read-back still shows true - something is intercepting the write";
            Svc.Log.Warning("[StatusBridge] Experimental Moodles offset patch: read-back mismatch after apply.");
            return false;
        }
        catch (Exception e)
        {
            IsApplied = false;
            Status = $"Failed: {e.GetType().Name} - {e.Message}";
            Svc.Log.Warning(e, "[StatusBridge] Experimental Moodles offset patch threw while applying.");
            return false;
        }
    }

    /// <summary>Restores Moodles' own default (the visual-scan path). Safe to call even if never applied.</summary>
    public bool TryRevert()
    {
        try
        {
            var field = FindFlagField(out var processorInstance);
            if (field == null || processorInstance == null)
                return false; // Nothing to revert, or Moodles isn't reachable right now.

            field.SetValue(processorInstance, true); // Moodles' own real default.
            IsApplied = false;
            Status = "Reverted to Moodles' default";
            Svc.Log.Information("[StatusBridge] Experimental Moodles offset patch reverted.");
            return true;
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Experimental Moodles offset patch threw while reverting.");
            return false;
        }
    }

    private FieldInfo? FindFlagField(out object? processorInstance)
    {
        processorInstance = null;

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == AssemblyName);
        if (assembly == null)
        {
            Status = "Moodles assembly not found (not loaded, or not installed)";
            return null;
        }

        var pluginType = assembly.GetType(PluginTypeName);
        if (pluginType == null)
        {
            Status = $"Moodles' plugin type has changed shape (expected '{PluginTypeName}') - patch is stale";
            return null;
        }

        var instanceField = pluginType.GetField(StaticInstanceFieldName, BindingFlags.Public | BindingFlags.Static);
        var pluginInstance = instanceField?.GetValue(null);
        if (pluginInstance == null)
        {
            Status = "Moodles hasn't finished initializing yet";
            return null;
        }

        var processorField = pluginType.GetField(ProcessorFieldName, BindingFlags.Public | BindingFlags.Instance);
        processorInstance = processorField?.GetValue(pluginInstance);
        if (processorInstance == null)
        {
            Status = $"Moodles' plugin class no longer has a '{ProcessorFieldName}' field - patch is stale";
            return null;
        }

        var flagField = processorInstance.GetType()
            .GetField(FlagFieldName, BindingFlags.Public | BindingFlags.Instance);
        if (flagField == null || flagField.FieldType != typeof(bool))
        {
            Status = $"Moodles' processor no longer has a bool '{FlagFieldName}' field - patch is stale";
            return null;
        }

        return flagField;
    }
}

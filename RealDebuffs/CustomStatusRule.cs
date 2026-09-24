using System;
using System.Text.RegularExpressions;

namespace RealDebuffs;

/// <summary>
/// One "while I have THIS custom Moodles/Loci status, show THAT effect" link. The list of these
/// lives in <see cref="Configuration.CustomStatusRules"/>.
///
/// Custom statuses aren't real game statuses - they never appear in the player's StatusList - so
/// there's no status ID to match on. The one thing a user can actually read off the screen (and
/// that a mirror like SkyrimCompass carries across unchanged) is the title, so rules match on the
/// title. See <see cref="StatusNames"/> for exactly what "match" means.
///
/// Rules can freely overlap: one name can drive several effects, and several names can drive the
/// same effect. EffectManager only ever asks "which effect kinds are active?" (a set), so no
/// combination of rules and real debuffs can draw an effect twice or restart one that's already on.
/// </summary>
public class CustomStatusRule
{
    private string _name = "";
    private string? _key;

    /// <summary>The status title to watch for, as the user typed or picked it.</summary>
    public string Name
    {
        get => _name;
        set { _name = value ?? ""; _key = null; }
    }

    /// <summary>
    /// The effect to show while the status is on. Saved as the enum's number, so any new
    /// <see cref="DebuffKind"/> should be added at the END of that enum - reordering it would
    /// silently re-point saved rules at different effects.
    /// </summary>
    public DebuffKind Kind { get; set; } = DebuffKind.Bind;

    /// <summary>Lets a rule be switched off without deleting it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The comparison key for <see cref="Name"/> (see <see cref="StatusNames.Key"/>). Cached because
    /// EffectManager asks every frame. A method rather than a property on purpose: a public property
    /// would get written into the saved config.
    /// </summary>
    public string GetKey() => _key ??= StatusNames.Key(_name);
}

/// <summary>
/// How status titles are compared. Titles are free text typed by people (and, for Moodles and Loci,
/// can carry formatting tags), so an exact-string match would be needlessly fragile:
///  - case is ignored ("Tormented By Shadows" is the same status as "tormented by shadows"),
///  - formatting tags are stripped, so a colored/italic title still matches the plain name you'd
///    type. The tag set is exactly the one Moodles and Loci both parse in titles:
///    [color=..] [glow=..] [i] and their closing tags,
///  - runs of whitespace collapse to one space and the ends are trimmed.
/// </summary>
public static class StatusNames
{
    private static readonly Regex Markup = new(
        @"\[color=[0-9a-z]+\]|\[/color\]|\[glow=[0-9a-z]+\]|\[/glow\]|\[i\]|\[/i\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Readable form: tags removed, whitespace tidied, original casing kept. This is what gets shown
    /// in the settings window and what a picked status is saved under.
    /// </summary>
    public static string Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        return Spaces.Replace(Markup.Replace(title, ""), " ").Trim();
    }

    /// <summary>
    /// Comparison form: <see cref="Clean"/> plus lower-casing. Two titles are "the same status"
    /// exactly when their keys are equal. Empty means "no usable name" and never matches anything.
    /// </summary>
    public static string Key(string? title) => Clean(title).ToLowerInvariant();
}

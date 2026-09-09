using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HousingToBrio;

/// <summary>
/// Resolves a housing item's game Item ID to the .sgb asset path that the game
/// (and Brio) load to display that piece of furniture.
///
/// The HousingFurniture / HousingYardObject sheets, the "ModelKey" field, and
/// the path-construction formula below were confirmed by reading Brio's own
/// (GPL-3.0 licensed) FurnitureDatabase, purely to learn the on-disk asset
/// naming convention - this class does not use or embed any of Brio's code:
/// https://github.com/Etheirys/Brio/blob/main/Brio/Resources/Extra/FurnitureDatabase.cs
/// </summary>
public sealed class FurnitureModelResolver
{
    private readonly Dictionary<uint, uint> _indoorModelKeyByItemId = new();
    private readonly Dictionary<uint, uint> _outdoorModelKeyByItemId = new();

    public int IndoorEntryCount => _indoorModelKeyByItemId.Count;
    public int OutdoorEntryCount => _outdoorModelKeyByItemId.Count;

    public FurnitureModelResolver(IDataManager dataManager)
    {
        var indoorSheet = dataManager.GetExcelSheet<HousingFurniture>();
        if (indoorSheet is not null)
        {
            foreach (var row in indoorSheet)
            {
                var item = row.Item.ValueNullable;
                if (item is null)
                    continue;

                _indoorModelKeyByItemId[item.Value.RowId] = row.ModelKey;
            }
        }

        var outdoorSheet = dataManager.GetExcelSheet<HousingYardObject>();
        if (outdoorSheet is not null)
        {
            foreach (var row in outdoorSheet)
            {
                var item = row.Item.ValueNullable;
                if (item is null)
                    continue;

                _outdoorModelKeyByItemId[item.Value.RowId] = row.ModelKey;
            }
        }
    }

    /// <summary>
    /// Attempts to resolve the given housing Item ID to an .sgb path. Returns
    /// false for item IDs that aren't in the current game's housing sheets
    /// (e.g. a placeholder "0" entry, or an item that no longer exists).
    /// </summary>
    public bool TryResolvePath(uint itemId, bool indoors, out string sgbPath)
    {
        var table = indoors ? _indoorModelKeyByItemId : _outdoorModelKeyByItemId;

        if (table.TryGetValue(itemId, out var modelKey))
        {
            var model = modelKey.ToString("0000");
            var location = indoors ? "indoor" : "outdoor";
            var funGar = indoors ? "fun" : "gar";

            sgbPath = $"bgcommon/hou/{location}/general/{model}/asset/{funGar}_b0_m{model}.sgb";
            return true;
        }

        sgbPath = string.Empty;
        return false;
    }
}

public sealed class ConversionOptions
{
    public bool IncludeInterior { get; set; } = true;
    public bool IncludeExterior { get; set; } = true;
    public bool ApplyDyeColors { get; set; } = true;
}

public sealed class ConversionResult
{
    public List<BrioWorldObjectDto> WorldObjects { get; } = new();
    public int PlacedCount { get; set; }
    public int SkippedUnknownItemCount { get; set; }
    public List<string> SkippedItemNames { get; } = new();
}

public static class LayoutToBrioConverter
{
    public static ConversionResult Convert(LayoutFile layout, FurnitureModelResolver resolver, ConversionOptions options)
    {
        var result = new ConversionResult();

        if (options.IncludeInterior)
            ConvertList(layout.InteriorFurniture, indoors: true, layout.InteriorScale, resolver, options, result);

        if (options.IncludeExterior)
            ConvertList(layout.ExteriorFurniture, indoors: false, layout.ExteriorScale, resolver, options, result);

        return result;
    }

    private static void ConvertList(
        List<FurnitureEntry> entries,
        bool indoors,
        float scaleField,
        FurnitureModelResolver resolver,
        ConversionOptions options,
        ConversionResult result)
    {
        // "interiorScale"/"exteriorScale" is the divisor the source format uses
        // to turn its stored location numbers back into real yalms (matches
        // ReMakePlace's own descale() helper). Guard against 0 in malformed files.
        var divisor = scaleField == 0f ? 100f : scaleField;

        foreach (var entry in entries)
            ConvertEntry(entry, indoors, divisor, resolver, options, result);
    }

    private static void ConvertEntry(
        FurnitureEntry entry,
        bool indoors,
        float divisor,
        FurnitureModelResolver resolver,
        ConversionOptions options,
        ConversionResult result)
    {
        if (resolver.TryResolvePath(entry.ItemId, indoors, out var sgbPath))
        {
            var loc = entry.Transform.Location;
            var rot = entry.Transform.Rotation;
            var scl = entry.Transform.Scale;

            // The layout format stores location as [gameX, gameZ, gameY] - Y and
            // Z are swapped relative to the game's own Vector3 convention - in
            // units scaled by interior/exteriorScale. Confirmed against
            // ReMakePlace's SaveLayoutManager.ConvertToHousingItem()/descale().
            var position = new Vector3(
                SafeGet(loc, 0) / divisor,
                SafeGet(loc, 2) / divisor,
                SafeGet(loc, 1) / divisor);

            // The rotation quaternion is stored in that SAME Y/Z-swapped frame -
            // confirmed by reading ReMakePlace's own RotationToQuat()/
            // ComputeZAngle() round-trip, which encodes a housing item's single
            // vertical-axis spin as a rotation around what its own convention
            // calls "Z" (RotationToQuat(angle) is literally
            // Quaternion.CreateFromYawPitchRoll(0, 0, angle)).
            //
            // Swapping two axes of a coordinate system flips its handedness, and
            // carrying a *rotation* across a handedness flip is not just a
            // component permutation the way it is for a position: the axis
            // permutes AND the angle inverts, which works out to negating every
            // swapped/kept axis component while leaving w untouched. Skipping
            // that (the previous code copied x/y/z straight through) leaves the
            // quaternion rotating the item around the depth (Z) axis instead of
            // its vertical (Y) axis - i.e. it tips furniture over sideways
            // instead of spinning it flat, which is exactly the "very broken"
            // rotations reported. Verified end-to-end against a known transform
            // (rotate a local test point, compare world-space results) as well
            // as against ReMakePlace's own reverse converter.
            var rotation = new Quaternion(
                -SafeGet(rot, 0),
                -SafeGet(rot, 2),
                -SafeGet(rot, 1),
                SafeGet(rot, 3, 1f));

            var scale = new Vector3(
                SafeGet(scl, 0, 1f),
                SafeGet(scl, 1, 1f),
                SafeGet(scl, 2, 1f));

            Vector4? color = null;
            if (options.ApplyDyeColors && entry.TryGetColorHex(out var hex) && TryParseColor(hex, out var parsedColor))
                color = parsedColor;

            result.WorldObjects.Add(new BrioWorldObjectDto
            {
                FriendlyName = string.IsNullOrEmpty(entry.Name) ? $"Item #{entry.ItemId}" : entry.Name,
                ObjectType = BrioWorldObjectType.Furniture,
                Path = sgbPath,
                Transform = new BrioTransform { Position = position, Rotation = rotation, Scale = scale },
                // Transform.Position above is already an absolute position in
                // the room's own local-space coordinates - the same coordinate
                // space real housing items use inside any interior of a
                // matching shape, regardless of where the player is standing.
                // That's what actually gets used on import IF Brio's "Relative
                // Object Positions" import option is unchecked - confirmed by
                // reading Brio's own WorldObjectService.SpawnFromDTO(), which
                // uses Transform.Position directly whenever no anchor is
                // supplied, and only falls back to (anchor + RelativePosition)
                // when that option is on.
                //
                // That option defaults to ON in Brio's Load Project window,
                // where "anchor" is wherever the player is standing at the
                // moment they click Load - which is the actual cause of
                // layouts spawning relative to the player instead of the room.
                // This plugin can't flip that checkbox for the user (Brio
                // exposes no IPC for it) - see the README/this plugin's UI for
                // the "uncheck Relative Object Positions" step that fixes it.
                //
                // RelativePosition is still populated here (matching
                // Transform.Position) purely so that if the user leaves that
                // option checked anyway, the layout at least keeps its correct
                // internal shape - anchored on the player instead of the room -
                // rather than every item collapsing onto a single point.
                RelativePosition = position,
                StainID = 0,
                Color = color,
            });

            result.PlacedCount++;
        }
        else
        {
            result.SkippedUnknownItemCount++;
            if (result.SkippedItemNames.Count < 25)
                result.SkippedItemNames.Add(string.IsNullOrEmpty(entry.Name) ? $"Item #{entry.ItemId}" : entry.Name);
        }

        if (entry.Attachments is not null)
        {
            foreach (var child in entry.Attachments)
                ConvertEntry(child, indoors, divisor, resolver, options, result);
        }
    }

    private static float SafeGet(float[] array, int index, float fallback = 0f)
        => array.Length > index ? array[index] : fallback;

    /// <summary>
    /// Parses the layout format's "RRGGBB" (or "RRGGBBAA") hex color into a
    /// normalized RGBA Vector4. The trailing byte in the source data is not a
    /// meaningful alpha value (housing dyes are opaque), so alpha is always
    /// forced to 1 - matching how Brio's FurnitureObject.SetCustomColor() forces
    /// full opacity on custom colors too.
    /// </summary>
    private static bool TryParseColor(string hex, out Vector4 color)
    {
        color = default;
        var clean = hex.TrimStart('#');
        if (clean.Length < 6)
            return false;

        if (!byte.TryParse(clean.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)) return false;
        if (!byte.TryParse(clean.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)) return false;
        if (!byte.TryParse(clean.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return false;

        color = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
        return true;
    }
}

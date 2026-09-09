using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HousingToBrio;

/// <summary>
/// Maps a housing Item ID to its .sgb asset path via the game's own
/// HousingFurniture/HousingYardObject sheets. Formula confirmed against
/// (not reproduced from) Brio's FurnitureDatabase (GPL-3.0):
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

    /// <summary>False if itemId isn't in the current game's housing sheets.</summary>
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
        // Divisor back to yalms (ReMakePlace's descale()); guard against 0.
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

            // Y/Z-swapped frame ReMakePlace uses; confirmed against its
            // SaveLayoutManager.ConvertToHousingItem()/descale().
            var position = new Vector3(
                SafeGet(loc, 0) / divisor,
                SafeGet(loc, 2) / divisor,
                SafeGet(loc, 1) / divisor);

            // Same Y/Z-swapped frame as position, but a swap flips handedness -
            // so rotation isn't a pure permutation like position is: x/y/z must
            // also be negated (w untouched), or items spin around the wrong
            // (depth) axis instead of vertically. Verified against Lush.json
            // (rotations are always single-axis) and ReMakePlace's
            // RotationToQuat()/ComputeZAngle().
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
                // Position is absolute room-local space - used directly when
                // Brio's "Relative Object Positions" import option is off (see
                // UI/README). RelativePosition is also set so the layout still
                // holds together, anchored on the player, if that option is
                // left on instead. See Brio's WorldObjectService.SpawnFromDTO().
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

    /// <summary>Parses "RRGGBB[AA]" hex; alpha is ignored/forced to 1 (dyes are opaque, matching Brio's SetCustomColor()).</summary>
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

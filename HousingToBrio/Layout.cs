using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HousingToBrio;

/// <summary>
/// Root of a ReMakePlace-style housing layout file - the format produced by the
/// ReMakePlace Dalamud plugin and the various layout-sharing sites/tools that
/// build on it (see https://github.com/RemakePlace/plugin for the reference
/// implementation). Only the fields we actually use are declared here;
/// System.Text.Json silently ignores any other top-level fields a particular
/// export might include (e.g. "lightLevel", "metaData").
/// </summary>
public sealed class LayoutFile
{
    [JsonPropertyName("houseSize")]
    public string HouseSize { get; set; } = string.Empty;

    [JsonPropertyName("interiorScale")]
    public float InteriorScale { get; set; } = 100f;

    [JsonPropertyName("exteriorScale")]
    public float ExteriorScale { get; set; } = 100f;

    [JsonPropertyName("interiorFurniture")]
    public List<FurnitureEntry> InteriorFurniture { get; set; } = new();

    [JsonPropertyName("exteriorFurniture")]
    public List<FurnitureEntry> ExteriorFurniture { get; set; } = new();

    [JsonPropertyName("interiorFixture")]
    public List<FixtureEntry> InteriorFixture { get; set; } = new();

    [JsonPropertyName("exteriorFixture")]
    public List<FixtureEntry> ExteriorFixture { get; set; } = new();
}

/// <summary>
/// A single piece of placed furniture/furnishing that has its own transform.
/// </summary>
public sealed class FurnitureEntry
{
    [JsonPropertyName("itemId")]
    public uint ItemId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("transform")]
    public TransformEntry Transform { get; set; } = new();

    [JsonPropertyName("properties")]
    public Dictionary<string, JsonElement>? Properties { get; set; }

    /// <summary>
    /// Some layout sources nest items placed on top of another item (tabletop
    /// decorations, etc.) inside an "attachments" array on the parent. They use
    /// the same absolute transform space as top-level furniture, so we flatten
    /// them rather than trying to model a parent/child relationship.
    /// </summary>
    [JsonPropertyName("attachments")]
    public List<FurnitureEntry>? Attachments { get; set; }

    /// <summary>
    /// Reads the "color" property, if present, as a raw "RRGGBB[AA]" hex string.
    /// </summary>
    public bool TryGetColorHex(out string hex)
    {
        hex = string.Empty;

        if (Properties is null || !Properties.TryGetValue("color", out var element))
            return false;

        if (element.ValueKind != JsonValueKind.String)
            return false;

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        hex = value!;
        return true;
    }
}

/// <summary>
/// A structural element of the house shell (wall/floor/roof/door/window/fence/
/// light/district) that has no transform of its own. These describe the house
/// itself rather than a placeable prop, so they are surfaced only for the
/// summary shown to the user - they are never turned into world objects.
/// </summary>
public sealed class FixtureEntry
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("itemId")]
    public uint ItemId { get; set; }
}

public sealed class TransformEntry
{
    [JsonPropertyName("location")]
    public float[] Location { get; set; } = { 0f, 0f, 0f };

    [JsonPropertyName("rotation")]
    public float[] Rotation { get; set; } = { 0f, 0f, 0f, 1f };

    [JsonPropertyName("scale")]
    public float[] Scale { get; set; } = { 1f, 1f, 1f };
}

public static class LayoutParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static LayoutFile LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Layout file not found.", path);

        var json = File.ReadAllText(path);
        var layout = JsonSerializer.Deserialize<LayoutFile>(json, Options);

        if (layout is null)
            throw new InvalidDataException("The selected file does not contain a valid housing layout.");

        return layout;
    }
}

/// <summary>
/// Detects the size category of the indoor housing area the player is
/// currently standing in, using only Dalamud's own client state and game
/// data - no unsafe memory access or signature scanning.
///
/// The current TerritoryType's internal codename encodes the size as a
/// 2-character substring ("i1"/"i2"/"i3"/"i4"). This is the same technique
/// ReMakePlace's own Memory.GetIndoorHouseSize() uses (it otherwise reaches
/// this territory ID via its own unsafe HousingModule pointer, which we don't
/// need - IClientState.TerritoryType gives us the same ID directly):
///   https://github.com/RemakePlace/plugin/blob/main/ReMakePlacePlugin/Memory.cs
/// </summary>
public static class HouseSizeDetector
{
    /// <summary>
    /// Returns "Small", "Medium", "Large", or "Apartment" if the player is
    /// currently inside a recognizable private housing interior, otherwise
    /// null (outdoors, in a different kind of zone, or an unrecognized name).
    /// </summary>
    public static string? GetCurrentIndoorHouseSize(IClientState clientState, IDataManager dataManager)
    {
        var territoryId = clientState.TerritoryType;
        if (territoryId == 0)
            return null;

        var sheet = dataManager.GetExcelSheet<TerritoryType>();
        if (sheet is null || !sheet.TryGetRow(territoryId, out var row))
            return null;

        var placeName = row.Name.ToString();
        if (placeName.Length < 4)
            return null;

        return placeName.Substring(2, 2) switch
        {
            "i1" => "Small",
            "i2" => "Medium",
            "i3" => "Large",
            "i4" => "Apartment",
            _ => null,
        };
    }
}

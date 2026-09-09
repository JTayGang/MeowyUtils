using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HousingToBrio;

/// <summary>
/// ReMakePlace-style housing layout format: https://github.com/RemakePlace/plugin
/// Unlisted fields (e.g. "lightLevel", "metaData") are ignored by System.Text.Json.
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

    /// <summary>Nested items (tabletop decor, etc.) share the parent's absolute transform space, so these get flattened rather than nested.</summary>
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

/// <summary>House shell (walls/floors/roof/etc.) - no transform, so never spawned; shown in the summary only.</summary>
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
/// Decodes house size from the current TerritoryType's internal name (same
/// technique as ReMakePlace's Memory.GetIndoorHouseSize(), but via
/// IClientState.TerritoryType instead of its unsafe HousingModule pointer):
/// https://github.com/RemakePlace/plugin/blob/main/ReMakePlacePlugin/Memory.cs
/// </summary>
public static class HouseSizeDetector
{
    /// <summary>Small/Medium/Large/Apartment, or null if not in a recognizable interior.</summary>
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

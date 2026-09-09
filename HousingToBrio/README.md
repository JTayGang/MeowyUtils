# Housing To Brio

A GPose plugin: load a housing layout file and it spawns the furniture as
Brio props, so you can shoot in any house layout without owning the house,
buying the furniture, or decorating anything yourself - just more
environments and options for your photos.

## Install

Add this URL as a custom plugin repository in Dalamud (Settings > Experimental > Custom Plugin Repositories), then install HousingToBrio from the plugin list.

```
https://raw.githubusercontent.com/JTayGang/MeowyUtils/main/repo.json
```

## Using it

1. `/housingtobrio` or `/h2b` opens the window.
2. Load a layout `.json` file.
3. Pick interior/exterior furniture and dye color options.
4. Confirm the Brio folder, name the project, and **Save as Brio Project**.
5. Reload Brio (or tick **Auto-reload Brio after saving**) - don't use
   New/Save/Delete Project in Brio first, or it'll wipe the entry.
6. In Brio's **Load Project** window, click the gear icon and uncheck
   **Relative Object Positions**, then Load.

Items that can't be matched to a current in-game furniture model are skipped
and listed after saving.

## Building From Source 

- A Dalamud plugin dev setup (Visual Studio / `dotnet build`, XIVLauncher).
- Brio installed.
- NuGet access to restore `MessagePack` 3.1.7.

```
dotnet build HousingToBrio.csproj -c Release
```

Load it as a dev plugin the way you normally do.

## Limitations

- Fixtures (walls, floors, roof, doors, etc.) aren't converted - furniture only.
- Alternate "material" item variants use the base item's look.
- Depends on Brio's project file format staying stable across updates.

## Licensing

Brio (<https://github.com/Etheirys/Brio>) is GPL-3.0. This plugin doesn't
reference or embed Brio's code, only matches its file format for
compatibility - but if you redistribute this publicly, worth reading Brio's
license yourself.

## Credits

File formats and conventions confirmed by reading (not reusing) source from:

- [Brio](https://github.com/Etheirys/Brio) (GPL-3.0)
- [ReMakePlace plugin](https://github.com/RemakePlace/plugin)
- [Dalamud](https://github.com/goatcorp/Dalamud)

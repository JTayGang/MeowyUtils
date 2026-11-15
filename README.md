# MeowyUtils

A small collection of [Dalamud](https://github.com/goatcorp/Dalamud) plugins for FFXIV, formerly
published as separate repos and now merged into one so there's a single place to grab any of
them from.

| Plugin | What it does |
|---|---|
| [**Skyrim Compass**](./SkyrimCompass) | Fully fixes Loci+Moodle compatibility in its own way. Has a Skyrim-style horizontal compass bar, limit break display, and full target info display (Healthbar, casting, statuses etc) |
| [**StatusBridge**](./StatusBridge) | *BROKEN, DONT INSTALL* First attempt at a Loci+Moodles fix, only fixed half the problem and was full of issues and experimental stuff that did nothing. |

Each plugin has its own README (linked above) with full feature lists, requirements, and
build/usage instructions specific to that plugin.

## Installing

Add this repo to Dalamud's plugin installer.

```
https://raw.githubusercontent.com/JTayGang/MeowyUtils/main/repo.json
```

In-game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories** → paste the URL above
→ restart or reload the plugin installer. Both plugins will then show up in `/xlplugins.

## Building from source

Each plugin is a self-contained project, so building one doesn't require the other:

```
dotnet build SkyrimCompass/SkyrimCompass.csproj -c Release
dotnet build StatusBridge/StatusBridge.csproj -c Release
```

Or open `MeowyUtils.sln` in Visual Studio / Rider to work on both at once.

## Repo layout

```
MeowyUtils/
├── repo.json              master list read by Dalamud — one entry per plugin
├── MeowyUtils.sln         solution referencing both plugin projects
├── SkyrimCompass/         plugin source + its own README
└── StatusBridge/          plugin source + its own README
```

## License

MIT — see [LICENSE](./LICENSE). Applies to everything in this repo unless a subfolder says
otherwise.

---

*This repo was previously named `SkyrimCompass` and held only that one plugin. GitHub
automatically redirects the old URL (including raw file links) to this one, so existing
installs of Skyrim Compass keep updating without any action needed — but if you're on the old
custom-repo URL, switching to the one above is a good idea since it's the one that'll actually
show StatusBridge too.*

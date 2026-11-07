# Merge notes: natural-language rework + current Amnesia implementation

- The natural-language rework was applied to the Amnesia-updated GitHub project.
- The supplied Amnesia reference patch was already byte-for-byte present in the GitHub project.
- The language overlay's `Effects/Amnesia/AmnesiaEffect.cs` was intentionally not copied. It is a separate visual rewrite that requires `stroke.silk` / `StrokeSilk` and additional `VortexField` members not present in the current project. Copying it alone would leave missing material/type/member references.
- The current Amnesia tunnel, fog, whispers and lapse behavior were retained. Its existing `Murmurs` particle slot is marked as the modifier layer so the new natural-language parser can target it with `with ...` phrases and modifier colours without changing the default visuals.
- The language README was retained, with its Vortex entry corrected to describe the Amnesia implementation actually included.
- The development preview controls were hardened so expired previews and `All off` clear cached material and colour overrides immediately.

The .NET SDK / Dalamud build toolchain was not available in the merge environment, so a `dotnet build` could not be run here. Please build with the Dalamud development install used by the project before loading it in-game.

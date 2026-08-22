using System;
using System.Collections.Generic;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace SkyrimCompass;

/// <summary>
/// Hands out an IFontHandle baked at the exact pixel size requested, instead of a single
/// fixed-size font that gets stretched at draw time. Dalamud's game fonts are prebaked
/// bitmaps: asking ImGui to draw them at anything other than their native size just
/// resamples that bitmap, which blurs when stretched up and turns thin/jagged when
/// shrunk down. Baking a fresh handle per size avoids both, since every size gets its
/// own glyphs rasterized straight from the game's font data instead of a scaled copy.
///
/// Sizes are rounded to the nearest pixel and cached so that dragging a scale slider
/// around doesn't ask Dalamud to rebuild the shared font atlas every single frame -
/// a given size is only baked once, the first time something actually lands on it.
/// </summary>
public sealed class DynamicGameFontCache : IDisposable
{
    // Sanity clamp so a stray/corrupt config value can't ask the atlas to bake
    // something absurd. Comfortably wider than the 0.5x-2.5x slider range in
    // Configuration.cs (9px-45px off an 18px base).
    private const int MinPx = 6, MaxPx = 96;

    private readonly IFontAtlas _atlas;
    private readonly GameFontFamily _family;
    private readonly Dictionary<int, IFontHandle> _handles = new();
    private IFontHandle? _lastAvailable;

    public DynamicGameFontCache(IFontAtlas atlas, GameFontFamily family)
    {
        _atlas = atlas;
        _family = family;
    }

    /// <summary>
    /// Returns a handle baked as close as possible to <paramref name="desiredPx"/>.
    /// If that exact size was just requested for the first time and hasn't finished
    /// baking yet, returns the most recently available handle instead, so text doesn't
    /// disappear for a frame while the new size builds asynchronously in the background.
    /// </summary>
    public IFontHandle Get(float desiredPx)
    {
        int px = Math.Clamp((int)MathF.Round(desiredPx), MinPx, MaxPx);

        if (!_handles.TryGetValue(px, out var handle))
        {
            handle = _atlas.NewGameFontHandle(new GameFontStyle(_family, px));
            _handles[px] = handle;
        }

        if (handle.Available)
        {
            _lastAvailable = handle;
            return handle;
        }

        return _lastAvailable ?? handle;
    }

    public void Dispose()
    {
        foreach (var handle in _handles.Values)
            handle.Dispose();
        _handles.Clear();
    }
}

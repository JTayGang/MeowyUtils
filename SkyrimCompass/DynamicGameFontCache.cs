using System;
using System.Collections.Generic;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace SkyrimCompass;

// Game font baked at the exact pixel size asked for (one handle per rounded size), so text never blurs from scaling.
public sealed class DynamicGameFontCache : IDisposable
{
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

    // Falls back to the last ready handle while a new size is still baking.
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

using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace FPSBooster;

// Public fields (not properties) so the settings window can bind them with ref.
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool Enabled = true;
    public bool PauseInGpose = true;
    public bool RequirePlayerInView = true;

    public bool CullOutsideView = true;
    public float FovMarginDeg = 12f;
    public float AlwaysShowRadius = 6f;
    public float MaxDistance;
    public float MaxHeightDelta;

    public void Sanitize()
    {
        Version = 2;
        FovMarginDeg = Math.Clamp(FovMarginDeg, 0f, 45f);
        AlwaysShowRadius = Math.Clamp(AlwaysShowRadius, 0f, 50f);
        MaxDistance = Math.Clamp(MaxDistance, 0f, 500f);
        MaxHeightDelta = Math.Clamp(MaxHeightDelta, 0f, 50f);
    }

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}

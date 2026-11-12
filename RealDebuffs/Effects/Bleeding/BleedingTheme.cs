using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>Bleeding's palette: a near-black opening, torn flesh lips, and wet blood with a pale sheen.</summary>
internal static class BleedColor
{
    public static readonly Vector3 Void = new(0.035f, 0.000f, 0.008f), WallDark = new(0.15f, 0.010f, 0.020f), WallLit = new(0.46f, 0.070f, 0.070f);
    public static readonly Vector3 LipLit = new(0.82f, 0.33f, 0.31f), LipDark = new(0.42f, 0.070f, 0.080f);
    public static readonly Vector3 Wet = new(0.42f, 0.012f, 0.020f), WetHot = new(0.72f, 0.040f, 0.050f), Sheen = new(1.00f, 0.74f, 0.70f);
    public static readonly Vector3 Bruise = new(0.30f, 0.020f, 0.040f);
}

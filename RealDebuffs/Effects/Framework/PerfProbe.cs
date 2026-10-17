using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Per-phase timing for the tentacle renderer. Compiles to nothing unless REALDEBUFFS_PROFILE is defined (add it to DefineConstants in the csproj).
/// Wrap a phase in <c>long t = PerfProbe.Start(); ... PerfProbe.Stop(PerfProbe.Slot.Body, t);</c>, then call <see cref="Report"/> now and then
/// (e.g. from a debug command): it returns the average milliseconds per frame since the last report and resets.
/// </summary>
internal static class PerfProbe
{
    public enum Slot { Rows, Suckers, Film, Shadow, CupsBack, Body, Glints, Sparkles, CupsFront, BeadGlints, Replay, Count }

#if REALDEBUFFS_PROFILE
    private static readonly long[] Ticks = new long[(int)Slot.Count];
    private static int _frames;

    public static long Start() => Stopwatch.GetTimestamp();
    public static void Stop(Slot slot, long t0) => Ticks[(int)slot] += Stopwatch.GetTimestamp() - t0;
    public static void EndFrame() => _frames++;

    /// <summary>"Rows 0.08  Suckers 0.64 ..." in ms per frame, and resets the counters.</summary>
    public static string Report()
    {
        if (_frames == 0) { Array.Clear(Ticks); return "(no frames)"; }
        var sb = new System.Text.StringBuilder();
        double total = 0;
        for (int i = 0; i < (int)Slot.Count; i++)
        {
            double ms = Ticks[i] * 1000.0 / Stopwatch.Frequency / _frames;
            total += ms;
            sb.Append($"{(Slot)i} {ms:F3}  ");
            Ticks[i] = 0;
        }
        sb.Append($"| total {total:F3} ms/frame over {_frames} frames");
        _frames = 0;
        return sb.ToString();
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static long Start() => 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Stop(Slot slot, long t0) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void EndFrame() { }
    public static string Report() => "(profiling disabled: define REALDEBUFFS_PROFILE)";
#endif
}

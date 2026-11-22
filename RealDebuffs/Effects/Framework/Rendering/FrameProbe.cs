using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RealDebuffs.Effects.Framework;

/// <summary>Opt-in per-frame cost recorder shared by plugin and harness; arming applies at the next <see cref="BeginFrame"/>, counts cover only <see cref="MeshDraw"/>, draw-thread only.</summary>
public static class FrameProbe
{
    /// <summary>Emit runs from BeginFrame (the scene clear) to the shed; Gap is whatever the host does between shed and render.</summary>
    public enum Phase { Emit, Shed, Gap, Vignette, Regions, BelowVeil, Resolve, Strokes, Free, Count }

    private static readonly string[] PhaseNames = { "emit", "shed", "gap", "vignette", "regions", "below-veil", "resolve", "strokes", "free" };

    private static readonly int Owners = Enum.GetValues<DebuffKind>().Max(k => (int)k) + 1;
    private static int FlushSlot => Owners;  // draw time spent copying a staged batch into the draw list, kept apart from whichever owner happened to trigger it

    private const int PhaseCount = (int)Phase.Count;
    private const int CFrame = PhaseCount, CVert = CFrame + 1, CIdx = CVert + 1, CRes = CIdx + 1, CStroke = CRes + 1, CPart = CStroke + 1, CCoat = CPart + 1, CRegion = CCoat + 1;
    private static readonly int CEmit = CRegion + 1;
    private static readonly int CDraw = CEmit + Owners;
    private static readonly double Ms = 1000.0 / Stopwatch.Frequency;

    public static bool Armed { get; private set; }

    private static bool _on;  // latched per frame
    private static int _ringLen, _count, _pos;
    private static float[][]? _col;
    private static long[] _phaseAcc = new long[PhaseCount], _emitAcc = new long[Owners], _drawAcc = new long[Owners + 1];
    private static long _frameT0, _t, _curT;
    private static int _phase, _cur = -1, _vtx, _idx, _res;

    /// <summary>Starts recording the last <paramref name="frames"/> frames; clears what was there.</summary>
    public static void Arm(int frames = 600)
    {
        _ringLen = Math.Max(8, frames);
        var col = new float[CDraw + Owners + 1][];
        for (int i = 0; i < col.Length; i++) col[i] = new float[_ringLen];
        _count = _pos = 0;
        _col = col;
        Armed = true;
    }

    /// <summary>Stops recording; what was recorded stays readable.</summary>
    public static void Disarm() => Armed = false;

    public static int Frames => _count;

    // ---- hooks ----

    public static void BeginFrame()
    {
        _on = Armed;
        if (!_on) return;
        Array.Clear(_phaseAcc); Array.Clear(_emitAcc); Array.Clear(_drawAcc);
        _vtx = _idx = _res = 0; _cur = -1;
        _frameT0 = _t = Stopwatch.GetTimestamp();
        _phase = (int)Phase.Emit;
    }

    /// <summary>Ticks to hand back to <see cref="Emit"/>; 0 when disarmed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Start() => _on ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Emit(DebuffKind owner, long start) { if (start != 0) _emitAcc[(int)owner] += Stopwatch.GetTimestamp() - start; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mark(Phase p) { if (_on) MarkSlow(p); }

    private static void MarkSlow(Phase p)
    {
        long now = Stopwatch.GetTimestamp();
        CloseOwner(now);
        _phaseAcc[_phase] += now - _t;
        _t = now; _phase = (int)p;
    }

    /// <summary>Draw time from here on is this owner's, until the owner changes or a phase mark.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Owner(DebuffKind owner) { if (_on && (int)owner != _cur) OwnerSlow((int)owner); }

    private static void OwnerSlow(int slot)
    {
        long now = Stopwatch.GetTimestamp();
        if (_cur >= 0) _drawAcc[_cur] += now - _curT;
        _cur = slot; _curT = now;
    }

    /// <summary>Switches draw time to the flush column; give the result back to <see cref="Leave"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Enter() { if (!_on) return -1; int prev = _cur; OwnerSlow(FlushSlot); return prev; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Leave(int prev) { if (_on) LeaveSlow(prev); }

    private static void LeaveSlow(int prev)
    {
        long now = Stopwatch.GetTimestamp();
        _drawAcc[FlushSlot] += now - _curT;
        _cur = prev; _curT = now;
    }

    /// <summary>One draw-list reservation of the mesh path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reserve(int vertices, int indices) { if (_on) { _res++; _vtx += vertices; _idx += indices; } }

    private static void CloseOwner(long now)
    {
        if (_cur < 0) return;
        _drawAcc[_cur] += now - _curT;
        _cur = -1;
    }

    public static void EndFrame(EffectScene scene)
    {
        if (!_on) return;
        _on = false;
        long now = Stopwatch.GetTimestamp();
        CloseOwner(now);
        _phaseAcc[_phase] += now - _t;

        var col = _col!; int i = _pos;
        for (int p = 0; p < PhaseCount; p++) col[p][i] = (float)(_phaseAcc[p] * Ms);
        col[CFrame][i] = (float)((now - _frameT0) * Ms);
        col[CVert][i] = _vtx; col[CIdx][i] = _idx; col[CRes][i] = _res;
        col[CStroke][i] = scene.Strokes.Count; col[CPart][i] = scene.Particles.Count; col[CCoat][i] = scene.Coats.Count; col[CRegion][i] = scene.Regions.Count;
        for (int k = 0; k < Owners; k++) col[CEmit + k][i] = (float)(_emitAcc[k] * Ms);
        for (int k = 0; k <= Owners; k++) col[CDraw + k][i] = (float)(_drawAcc[k] * Ms);
        _pos = (_pos + 1) % _ringLen;
        if (_count < _ringLen) _count++;
    }

    // ---- reading ----

    public readonly record struct Stat(double Avg, double P50, double P95, double P99, double Max);

    private static Stat Of(float[] x, int n)
    {
        if (n == 0) return default;
        var s = new float[n]; Array.Copy(x, s, n);  // the ring is not ordered once it wraps, and the statistics do not care
        Array.Sort(s);
        double sum = 0; for (int i = 0; i < n; i++) sum += s[i];
        float P(double q) => s[Math.Min(n - 1, (int)(q * n))];
        return new Stat(sum / n, P(.5), P(.95), P(.99), s[n - 1]);
    }

    public static Stat PhaseStat(Phase p) => Of(_col![(int)p], _count);
    public static Stat FrameStat() => Of(_col![CFrame], _count);
    public static Stat Vertices() => Of(_col![CVert], _count);
    public static Stat Indices() => Of(_col![CIdx], _count);
    public static Stat Reservations() => Of(_col![CRes], _count);
    public static Stat Strokes() => Of(_col![CStroke], _count);
    public static Stat Particles() => Of(_col![CPart], _count);
    public static Stat Coats() => Of(_col![CCoat], _count);
    public static Stat Regions() => Of(_col![CRegion], _count);
    public static Stat EmitStat(DebuffKind k) => Of(_col![CEmit + (int)k], _count);
    public static Stat DrawStat(DebuffKind k) => Of(_col![CDraw + (int)k], _count);

    public static string[] Report()
    {
        if (_col is null || _count == 0) return new[] { "FrameProbe: nothing recorded (arm it, let some frames draw, then ask again)." };
        var o = new List<string> { $"FrameProbe: last {_count} frames (ms unless noted)", "phase              avg       p50       p95       p99       max" };
        string Row(string name, Stat s, string f = "F3") => $"{name,-12} {s.Avg.ToString(f),9} {s.P50.ToString(f),9} {s.P95.ToString(f),9} {s.P99.ToString(f),9} {s.Max.ToString(f),9}";
        for (int p = 0; p < PhaseCount; p++) o.Add(Row(PhaseNames[p], Of(_col[p], _count)));
        o.Add(Row("FRAME", Of(_col[CFrame], _count)));
        o.Add("per frame          avg       p50       p95       p99       max");
        o.Add(Row("vertices", Vertices(), "F0")); o.Add(Row("indices", Indices(), "F0")); o.Add(Row("reservations", Reservations(), "F0"));
        o.Add(Row("strokes", Strokes(), "F0")); o.Add(Row("particles", Particles(), "F0")); o.Add(Row("coats", Coats(), "F0")); o.Add(Row("regions", Regions(), "F0"));
        o.Add("owner         emit avg  emit p95  emit max |  draw avg  draw p95  draw max");
        string Own(string name, Stat e, Stat d) => $"{name,-12} {e.Avg,8:F3}  {e.P95,8:F3}  {e.Max,8:F3} | {d.Avg,8:F3}  {d.P95,8:F3}  {d.Max,8:F3}";
        for (int k = 0; k < Owners; k++)
        {
            var e = Of(_col[CEmit + k], _count); var d = Of(_col[CDraw + k], _count);
            if (e.Max > 0 || d.Max > 0) o.Add(Own(((DebuffKind)k).ToString(), e, d));
        }
        var fl = Of(_col[CDraw + FlushSlot], _count);
        if (fl.Max > 0) o.Add(Own("(flush)", default, fl));
        return o.ToArray();
    }

    public static string ReportText() => string.Join("\n", Report());
}

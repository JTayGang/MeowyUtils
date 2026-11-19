using System;
using System.Diagnostics;
using System.Text;
using Dalamud.Plugin.Services;

namespace FPSBooster;

/// <summary>
/// Opt-in frame-pacing and FPSBooster culling profiler.
/// Frame samples are intervals between IFramework.Update callbacks, not isolated
/// measurements of the game's CPU or GPU frame execution time.
/// </summary>
public sealed class PerformanceProfiler : IDisposable
{
    // Five minutes at 60 FPS. Captures stop automatically rather than growing without bound.
    private const int MaximumSamples = 18_000;
    private const double SnapshotRefreshSeconds = 0.5d;

    private readonly IFramework _framework;
    private readonly CullingService _culling;
    private readonly NativeTextureLifecycleProbe _nativeTextureLifecycle;
    private readonly FrameSample[] _samples = new FrameSample[MaximumSamples];

    private int _sampleCount;
    private long _lastUpdateTimestamp;
    private long _captureStartTimestamp;
    private long _lastSnapshotTimestamp;
    private DateTime _captureStartedUtc;
    private DateTime? _captureStoppedUtc;
    private bool _isCapturing;
    private bool _disposed;
    private bool _hasCachedSnapshot;
    private PerformanceProfileSnapshot _cachedSnapshot;

    private readonly struct FrameSample
    {
        public readonly double FrameIntervalMilliseconds;
        public readonly double CullingUpdateMilliseconds;
        public readonly bool CullingActive;
        public readonly bool NativeTextureCaptureEnabled;

        public FrameSample(double frameIntervalMilliseconds, double cullingUpdateMilliseconds,
            bool cullingActive, bool nativeTextureCaptureEnabled)
        {
            FrameIntervalMilliseconds = frameIntervalMilliseconds;
            CullingUpdateMilliseconds = cullingUpdateMilliseconds;
            CullingActive = cullingActive;
            NativeTextureCaptureEnabled = nativeTextureCaptureEnabled;
        }
    }

    public PerformanceProfiler(IFramework framework, CullingService culling,
        NativeTextureLifecycleProbe nativeTextureLifecycle)
    {
        _framework = framework;
        _culling = culling;
        _nativeTextureLifecycle = nativeTextureLifecycle;

        // Subscribe only during an active capture so the profiler adds no per-frame
        // callback overhead during normal plugin use.
    }

    public bool IsCapturing => _isCapturing;
    public bool HasSamples => _sampleCount > 0;
    public int SampleCount => _sampleCount;
    public int MaximumSampleCount => MaximumSamples;
    public DateTime CaptureStartedUtc => _captureStartedUtc;
    public DateTime? CaptureStoppedUtc => _captureStoppedUtc;

    public double CaptureElapsedSeconds
    {
        get
        {
            if (!_isCapturing || _captureStartTimestamp == 0)
                return GetSnapshot().SampledDurationSeconds;

            return Stopwatch.GetElapsedTime(_captureStartTimestamp).TotalSeconds;
        }
    }

    public void StartCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopCapture();

        _sampleCount = 0;
        _captureStartedUtc = DateTime.UtcNow;
        _captureStoppedUtc = null;
        _captureStartTimestamp = Stopwatch.GetTimestamp();
        _lastUpdateTimestamp = _captureStartTimestamp;
        _hasCachedSnapshot = false;
        _isCapturing = true;

        _culling.ResetPerformanceTiming();
        _culling.PerformanceTimingEnabled = true;
        // Subscribe after CullingService so its timing for this update is ready first.
        _framework.Update += OnFrameworkUpdate;
    }

    public void StopCapture()
    {
        if (!_isCapturing)
            return;

        _isCapturing = false;
        _framework.Update -= OnFrameworkUpdate;
        _culling.PerformanceTimingEnabled = false;
        _captureStoppedUtc = DateTime.UtcNow;
        _lastUpdateTimestamp = 0;
        _hasCachedSnapshot = false;
    }

    public void ClearResults()
    {
        StopCapture();
        _sampleCount = 0;
        _captureStartedUtc = DateTime.MinValue;
        _captureStoppedUtc = null;
        _captureStartTimestamp = 0;
        _hasCachedSnapshot = false;
    }

    public PerformanceProfileSnapshot GetSnapshot()
    {
        if (_hasCachedSnapshot && _lastSnapshotTimestamp != 0
            && Stopwatch.GetElapsedTime(_lastSnapshotTimestamp).TotalSeconds < SnapshotRefreshSeconds)
        {
            return _cachedSnapshot;
        }

        _cachedSnapshot = BuildSnapshot();
        _lastSnapshotTimestamp = Stopwatch.GetTimestamp();
        _hasCachedSnapshot = true;
        return _cachedSnapshot;
    }

    public string BuildReport()
    {
        var snapshot = BuildSnapshot();
        var sb = new StringBuilder(4096);
        string version = typeof(PerformanceProfiler).Assembly.GetName().Version?.ToString() ?? "unknown";

        sb.AppendLine("FPSBooster performance profile");
        sb.AppendLine($"Generated UTC: {DateTime.UtcNow:O}");
        sb.AppendLine($"Plugin version: {version}");
        sb.AppendLine($"Capture started UTC: {(_captureStartedUtc == DateTime.MinValue ? "(not captured)" : _captureStartedUtc.ToString("O"))}");
        sb.AppendLine($"Capture stopped UTC: {(_captureStoppedUtc?.ToString("O") ?? (_isCapturing ? "(still running)" : "(not stopped)"))}");
        sb.AppendLine($"Capture status: {(_isCapturing ? "Running" : "Stopped")}");
        sb.AppendLine($"Samples: {snapshot.SampleCount:N0} / {MaximumSamples:N0}");
        sb.AppendLine($"Sampled duration: {snapshot.SampledDurationSeconds:F2} seconds");
        sb.AppendLine();

        sb.AppendLine("=== Frame pacing ===");
        sb.AppendLine("Measurement: elapsed time between IFramework.Update callbacks, measured with Stopwatch.");
        sb.AppendLine("This is a frame-pacing proxy. It does not isolate game main-thread CPU execution, GPU execution, or presentation/display latency.");
        if (!snapshot.HasSamples)
        {
            sb.AppendLine("No frame samples were captured.");
        }
        else
        {
            sb.AppendLine($"Average frame interval: {snapshot.AverageFrameMilliseconds:F3} ms");
            sb.AppendLine($"Average FPS (1000 / average interval): {snapshot.AverageFps:F2}");
            sb.AppendLine($"Frame interval percentiles: P50 {snapshot.P50FrameMilliseconds:F3} ms · P95 {snapshot.P95FrameMilliseconds:F3} ms · P99 {snapshot.P99FrameMilliseconds:F3} ms");
            sb.AppendLine($"Worst frame interval: {snapshot.WorstFrameMilliseconds:F3} ms");
            sb.AppendLine($"1% low: {FormatFps(snapshot.OnePercentLowFps)} · 0.1% low: {FormatFps(snapshot.OneTenthPercentLowFps)}");
            sb.AppendLine($"Frames > 33.3 ms: {snapshot.FramesOver33Milliseconds:N0} · > 50 ms: {snapshot.FramesOver50Milliseconds:N0} · > 100 ms: {snapshot.FramesOver100Milliseconds:N0}");
            sb.AppendLine($"Frame interval buckets: <16.7 ms {snapshot.FramesUnder16Milliseconds:N0} · 16.7–33.3 ms {snapshot.Frames16To33Milliseconds:N0} · 33.3–50 ms {snapshot.Frames33To50Milliseconds:N0} · 50–100 ms {snapshot.Frames50To100Milliseconds:N0} · ≥100 ms {snapshot.FramesAtLeast100Milliseconds:N0}");
        }
        sb.AppendLine();

        sb.AppendLine("=== FPSBooster housing-culling CPU time ===");
        sb.AppendLine("Timing scope: CullingService.OnUpdate/Tick only; excludes the game renderer and other plugin code.");
        sb.AppendLine($"Frames where furniture culling reported active: {snapshot.CullingActiveFrames:N0} / {snapshot.SampleCount:N0}");
        sb.AppendLine($"Culling update average: {snapshot.AverageCullingUpdateMilliseconds:F4} ms · P95: {snapshot.P95CullingUpdateMilliseconds:F4} ms · max: {snapshot.MaximumCullingUpdateMilliseconds:F4} ms");
        sb.AppendLine($"Total measured culling CPU time: {snapshot.TotalCullingUpdateMilliseconds:F2} ms");
        sb.AppendLine();

        sb.AppendLine("=== Instrumentation state ===");
        sb.AppendLine($"Native texture lifecycle capture enabled: {snapshot.NativeTextureCaptureFrames:N0} / {snapshot.SampleCount:N0} sampled frames");
        sb.AppendLine(snapshot.NativeTextureCaptureFrames == 0
            ? "Native texture lifecycle detours were not enabled during the sampled frames."
            : "Native texture lifecycle detours were enabled for part of this capture. Treat this as an instrumented run; hook activity can add overhead.");
        sb.AppendLine("Texture lifecycle event timings are reported separately on the Native Textures tab; enabling those hooks is not required for this frame-pacing capture.");
        sb.AppendLine();

        sb.AppendLine("=== Interpretation notes ===");
        sb.AppendLine("• Compare captures made in the same location, camera direction, character state, and activity.");
        sb.AppendLine("• Keep native texture lifecycle capture disabled for the cleanest baseline. Run a separate instrumented pass when investigating individual texture loads.");
        sb.AppendLine("• Frame intervals can include loading, scheduling delays, alt-tab/minimize pauses, and other waits; they are not a direct measurement of GPU time.");
        sb.AppendLine("• 1%/0.1% lows are calculated from the average interval of the slowest 1%/0.1% of samples. Very short captures make the 0.1% figure unstable.");
        return sb.ToString();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!_isCapturing)
            return;

        long now = Stopwatch.GetTimestamp();
        long previous = _lastUpdateTimestamp;
        _lastUpdateTimestamp = now;
        if (previous == 0)
            return;

        double frameIntervalMilliseconds = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
        if (!double.IsFinite(frameIntervalMilliseconds) || frameIntervalMilliseconds <= 0d)
            return;

        if (_sampleCount >= MaximumSamples)
        {
            StopCapture();
            return;
        }

        _samples[_sampleCount++] = new FrameSample(
            frameIntervalMilliseconds,
            _culling.LastPerformanceUpdateMilliseconds,
            _culling.Active,
            _nativeTextureLifecycle.CaptureEnabled);

        if (_sampleCount >= MaximumSamples)
            StopCapture();
    }

    private PerformanceProfileSnapshot BuildSnapshot()
    {
        int count = _sampleCount;
        if (count == 0)
            return PerformanceProfileSnapshot.Empty;

        var frameTimes = new double[count];
        var cullingTimes = new double[count];
        double frameTotal = 0d;
        double cullingTotal = 0d;
        int activeCullingFrames = 0;
        int nativeCaptureFrames = 0;
        int over33 = 0;
        int over50 = 0;
        int over100 = 0;
        int under16 = 0;
        int between16And33 = 0;
        int between33And50 = 0;
        int between50And100 = 0;
        int over100Bucket = 0;
        double worstFrame = 0d;
        double maximumCulling = 0d;

        for (int i = 0; i < count; i++)
        {
            FrameSample sample = _samples[i];
            double frameMs = sample.FrameIntervalMilliseconds;
            double cullingMs = sample.CullingUpdateMilliseconds;
            frameTimes[i] = frameMs;
            cullingTimes[i] = cullingMs;
            frameTotal += frameMs;
            cullingTotal += cullingMs;
            if (sample.CullingActive) activeCullingFrames++;
            if (sample.NativeTextureCaptureEnabled) nativeCaptureFrames++;
            if (frameMs > 33.333333d) over33++;
            if (frameMs > 50d) over50++;
            if (frameMs > 100d) over100++;
            if (frameMs < 16.666667d) under16++;
            else if (frameMs < 33.333333d) between16And33++;
            else if (frameMs < 50d) between33And50++;
            else if (frameMs < 100d) between50And100++;
            else over100Bucket++;
            if (frameMs > worstFrame) worstFrame = frameMs;
            if (cullingMs > maximumCulling) maximumCulling = cullingMs;
        }

        Array.Sort(frameTimes);
        Array.Sort(cullingTimes);
        double averageFrame = frameTotal / count;
        double averageCulling = cullingTotal / count;
        int slowestOnePercentCount = Math.Max(1, (int)Math.Ceiling(count * 0.01d));
        int slowestOneTenthPercentCount = Math.Max(1, (int)Math.Ceiling(count * 0.001d));
        double slowestOnePercentAverage = AverageTail(frameTimes, slowestOnePercentCount);
        double slowestOneTenthPercentAverage = AverageTail(frameTimes, slowestOneTenthPercentCount);

        return new PerformanceProfileSnapshot(
            count,
            frameTotal / 1000d,
            averageFrame,
            averageFrame > 0d ? 1000d / averageFrame : 0d,
            Percentile(frameTimes, 0.50d),
            Percentile(frameTimes, 0.95d),
            Percentile(frameTimes, 0.99d),
            worstFrame,
            slowestOnePercentAverage > 0d ? 1000d / slowestOnePercentAverage : 0d,
            slowestOneTenthPercentAverage > 0d ? 1000d / slowestOneTenthPercentAverage : 0d,
            over33,
            over50,
            over100,
            under16,
            between16And33,
            between33And50,
            between50And100,
            over100Bucket,
            activeCullingFrames,
            averageCulling,
            Percentile(cullingTimes, 0.95d),
            maximumCulling,
            cullingTotal,
            nativeCaptureFrames);
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
            return 0d;
        int index = (int)Math.Ceiling(sortedValues.Length * percentile) - 1;
        index = Math.Clamp(index, 0, sortedValues.Length - 1);
        return sortedValues[index];
    }

    private static double AverageTail(double[] sortedValues, int count)
    {
        if (sortedValues.Length == 0)
            return 0d;
        count = Math.Clamp(count, 1, sortedValues.Length);
        double total = 0d;
        for (int i = sortedValues.Length - count; i < sortedValues.Length; i++)
            total += sortedValues[i];
        return total / count;
    }

    private static string FormatFps(double fps) => fps > 0d ? $"{fps:F2} FPS" : "n/a";

    public void Dispose()
    {
        if (_disposed)
            return;

        StopCapture();
        _culling.PerformanceTimingEnabled = false;
        _disposed = true;
    }
}

public readonly record struct PerformanceProfileSnapshot(
    int SampleCount,
    double SampledDurationSeconds,
    double AverageFrameMilliseconds,
    double AverageFps,
    double P50FrameMilliseconds,
    double P95FrameMilliseconds,
    double P99FrameMilliseconds,
    double WorstFrameMilliseconds,
    double OnePercentLowFps,
    double OneTenthPercentLowFps,
    int FramesOver33Milliseconds,
    int FramesOver50Milliseconds,
    int FramesOver100Milliseconds,
    int FramesUnder16Milliseconds,
    int Frames16To33Milliseconds,
    int Frames33To50Milliseconds,
    int Frames50To100Milliseconds,
    int FramesAtLeast100Milliseconds,
    int CullingActiveFrames,
    double AverageCullingUpdateMilliseconds,
    double P95CullingUpdateMilliseconds,
    double MaximumCullingUpdateMilliseconds,
    double TotalCullingUpdateMilliseconds,
    int NativeTextureCaptureFrames)
{
    public bool HasSamples => SampleCount > 0;
    public static PerformanceProfileSnapshot Empty => default;
}

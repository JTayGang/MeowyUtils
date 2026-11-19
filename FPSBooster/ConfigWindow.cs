using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FPSBooster;

public sealed class ConfigWindow : Window
{
    private const float SaveDebounceSeconds = 1f;
    private static readonly Vector4 Green = new(.45f, .85f, .45f, 1f);
    private static readonly Vector4 Muted = new(.65f, .65f, .65f, 1f);
    private readonly Plugin _plugin;
    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty;
    private string? _diagnosticsCopyStatus;
    private string? _performanceCopyStatus;
    private string _texturePath = string.Empty;
    private string? _textureInputError;
    private int _resolvedReadMipIndex = -1;
    private bool _gpuSequenceActive;
    private TextureMipPrefetchQueue? _gpuSequencePrefetchQueue;
    private enum GpuUploadSchedulingMode
    {
        OneMipPerFrame = 0,
        ElapsedTimeBudget = 1,
        PredictiveBudget = 2,
        ElapsedBudgetWithLargeMipIsolation = 3,
    }

    // Elapsed-time batching had the best measured throughput on the 4K test asset. Keep
    // predictive scheduling available as a controlled comparison rather than making it default.
    private GpuUploadSchedulingMode _gpuSequenceSchedulingMode = GpuUploadSchedulingMode.ElapsedTimeBudget;
    private GpuUploadSchedulingMode _gpuSequenceActiveSchedulingMode = GpuUploadSchedulingMode.ElapsedTimeBudget;
    private float _gpuSequenceCpuUploadBudgetMilliseconds = 1.0f;
    private float _gpuSequenceActiveCpuUploadBudgetMilliseconds = 1.0f;
    private bool _gpuSequenceVerifyReadback = true;
    private int _gpuSequenceCurrentMip = -1;
    private int _gpuSequenceNextMip = -1;
    private readonly List<string> _gpuSequenceResults = new();
    private readonly List<int> _gpuSequenceFrameBatchCounts = new();
    private readonly List<double> _gpuSequenceFrameUploadSubmissionMilliseconds = new();
    private TextureMipReadReport? _gpuSequencePendingMipRead;
    private const double InitialEstimatedUploadMillisecondsPerMiB = 0.20d;
    private double _gpuSequenceEstimatedUploadMillisecondsPerMiB = InitialEstimatedUploadMillisecondsPerMiB;
    private int _gpuSequenceBudgetDeferralCount;
    private int _gpuSequenceLargeMipDeferralCount;
    private long _gpuSequenceLargeMipIsolationThresholdBytes;
    private int _gpuSequencePrefetchEmptyPollCount;
    private int _gpuSequencePrefetchStarvedFrameCount;
    private int _gpuSequenceBudgetOverrunFrameCount;
    private int _gpuSequenceSingleMipOverrunFrameCount;
    private int _gpuSequenceBatchOverrunFrameCount;
    private double _gpuSequenceMaximumFrameUploadMilliseconds;
    private double _gpuSequenceLastDeferredEstimateMilliseconds;
    private double _gpuSequenceLastDeferredRemainingMilliseconds;
    private string _gpuSequenceStatus = "Not run.";
    private long _gpuSequenceArchiveBytes;
    private long _gpuSequenceMipBytes;
    private double _gpuSequenceReadMilliseconds;
    private double _gpuSequenceUploadMilliseconds;
    private double _gpuSequenceReadbackMilliseconds;
    // Wall-clock metrics include background archive work and the UI-frame cadence between mips.
    // These are distinct from the summed worker/UpdateSubresource timings above.
    private readonly Stopwatch _gpuSequenceWallTimer = new();
    private TextureFileReport? _gpuSequenceReport;
    private string? _gpuSequenceLooseFilePath;
    private long _gpuSequenceLoosePayloadOffset;
    private long _gpuSequenceLooseFileLength;
    private DateTime _gpuSequenceLooseLastWriteUtc;
    private double _gpuSequenceWallMilliseconds;
    private double _gpuSequenceFirstPreviewMilliseconds = -1d;
    private int _gpuSequenceFrameCount;
    private int _gpuSequenceFirstPreviewFrame = -1;

    public ConfigWindow(Plugin plugin) : base("FPSBooster##fpsboosterconfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 180), MaximumSize = new Vector2(620, 700) };
    }

    /// <summary>Stops background sequence work when the plugin is unloaded, even if the window stays open.</summary>
    public void Shutdown()
    {
        StopProgressiveSequence("Stopped because FPSBooster is unloading.", clearResults: true);
    }

    public override void OnClose()
    {
        // The furniture test is momentary. Closing the window must always release it.
        _plugin.Culling.ForceHideAll = false;
        _plugin.Culling.ForceHideAllExceptSpotlights = false;
        StopProgressiveSequence("Stopped because the configuration window closed.", clearResults: true);
        _plugin.TextureStreaming.Clear();
        _plugin.TextureGpuUpload.Reset();
        _plugin.NativeTextureLifecycle.StopCapture();
        _plugin.NativeTextureLifecycle.CancelNativeMipPayloadCapture();
        _plugin.NativeProgressiveTexturePreview.Reset();
        _plugin.NativeTextureHandle.Release();
        _plugin.PenumbraTextureSource.Clear();
        Flush();
    }

    public override void Draw()
    {
        var cfg = _plugin.Config;
        var svc = _plugin.Culling;
        bool changed = false;
        bool hideAllTestActive = false;
        bool spotlightAwareTestActive = false;

        if (ImGui.BeginTabBar("FPSBoosterTabs"))
        {
            if (ImGui.BeginTabItem("Performance"))
            {
                DrawPerformance();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Housing"))
            {
                changed |= Toggle("Enable furniture culling", ref cfg.Enabled,
                    "Hides furniture outside the camera view. Turn this off to show everything again.");
                ImGui.SameLine();
                bool running = svc.Active;
                string stateText = svc.UsingNativeOutdoorCulling
                    ? (svc.NativeOutdoorExtraLimitsActive ? "Game culling + extra limits"
                        : svc.NativeOutdoorExtraLimitsWaitingForCamera ? "Game culling (limits waiting)" : "Game culling")
                    : running ? "Working" : GetSimpleStatus(cfg.Enabled, svc.Status);
                ImGui.TextColored(running ? Green : Muted, stateText);

                var stats = svc.Stats;
                ImGui.Separator();
                ImGui.TextDisabled("Furniture in this area");
                ImGui.SameLine(190f);
                ImGui.Text(stats.FurnitureItems.ToString());

                ImGui.TextDisabled("Hidden right now");
                ImGui.SameLine(190f);
                ImGui.Text(stats.Hidden.ToString());

                if (svc.UsingNativeOutdoorCulling)
                    ImGui.TextDisabled(svc.NativeOutdoorExtraLimitsActive
                        ? "The game handles outdoor visibility; FPSBooster only applies your extra distance/height limits."
                        : svc.NativeOutdoorExtraLimitsWaitingForCamera
                            ? "The game handles outdoor visibility; FPSBooster's extra limits are waiting for camera data."
                            : "The game's native background culling handles outdoor visibility.");

                ImGui.Separator();
                changed |= Toggle("Hide furniture outside the screen", ref cfg.CullOutsideView,
                    "Hides furniture outside the camera view. Outdoors, the game's own culling is used when available.");
                changed |= Slider("Extra room around the screen", ref cfg.FovMarginDeg, 0f, 45f, "%.0f°",
                    "Keeps furniture just past the screen edge visible to avoid pop-in. Unused when the game handles outdoor culling.");
                changed |= DistanceSlider("Always show nearby furniture", ref cfg.AlwaysShowRadius, 0f, 30f,
                    "Furniture within this distance of the camera stays visible. Off disables it.");
                changed |= DistanceSlider("Maximum distance", ref cfg.MaxDistance, 0f, 200f,
                    "Hides furniture farther than this from the camera, even if on screen or spotlight-protected. Off disables it.");
                changed |= DistanceSlider("Maximum height difference", ref cfg.MaxHeightDelta, 0f, 30f,
                    "Hides furniture this far above or below the camera, even if spotlight-protected. Useful for other floors. Off disables it.");
                changed |= Toggle("Show all furniture in GPose", ref cfg.PauseInGpose,
                    "Temporarily stops hiding furniture while you are in GPose.");
                changed |= Toggle("Pause if my character is off screen", ref cfg.RequirePlayerInView,
                    "A safety check. Turn this off when using a free camera.");

                if (ImGui.TreeNode("Temporary furniture test controls"))
                {
                    ImGui.TextDisabled("Hold either button to temporarily override furniture visibility. Releasing restores normal culling.");
                    ImGui.Button("Hold to hide ALL furniture", new Vector2(-1f, 0f));
                    hideAllTestActive = ImGui.IsItemActive();
                    Tip("Hides every loaded furnishing, spotlights included. Release to restore.");

                    ImGui.Button("Hold to hide ALL (keep spotlight protection)", new Vector2(-1f, 0f));
                    spotlightAwareTestActive = ImGui.IsItemActive();
                    Tip("Hides furniture as if all were off screen, but keeps spotlight protection. Release to restore.");

                    if (hideAllTestActive || spotlightAwareTestActive)
                    {
                        string testStatus = hideAllTestActive
                            ? "Test active — hiding ALL furniture"
                            : "Test active — keeping spotlight protection";
                        ImGui.TextColored(new Vector4(1f, .65f, .35f, 1f), testStatus);
                    }

                    ImGui.TreePop();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Native Textures"))
            {
                DrawTextures();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        // Tests stay active only while their button is visible and held; switching tabs or closing ends them.
        svc.ForceHideAll = hideAllTestActive;
        svc.ForceHideAllExceptSpotlights = spotlightAwareTestActive;

        if (changed) _dirty = true;
        if (_dirty && ((DateTime.UtcNow - _lastSave).TotalSeconds >= SaveDebounceSeconds || !IsOpen)) Flush();
    }

    private void DrawPerformance()
    {
        var profiler = _plugin.PerformanceProfiler;
        var snapshot = profiler.GetSnapshot();

        ImGui.TextWrapped("Capture frame pacing and FPSBooster's own housing-culling CPU cost. This profiler samples the time between Dalamud framework updates; it is not a direct CPU/GPU frame-time profiler.");
        ImGui.Separator();

        if (profiler.IsCapturing)
        {
            ImGui.TextColored(Green, $"Recording · {profiler.SampleCount:N0}/{profiler.MaximumSampleCount:N0} samples · {profiler.CaptureElapsedSeconds:F1} s");
            if (ImGui.Button("Stop capture", new Vector2(145f, 0f)))
                profiler.StopCapture();
        }
        else
        {
            if (ImGui.Button(snapshot.HasSamples ? "Start new capture" : "Start capture", new Vector2(160f, 0f)))
                profiler.StartCapture();
            ImGui.SameLine();
            if (ImGui.Button("Clear results", new Vector2(125f, 0f)))
            {
                profiler.ClearResults();
                _performanceCopyStatus = null;
            }
        }

        if (snapshot.HasSamples)
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy performance report", new Vector2(190f, 0f)))
            {
                try
                {
                    var report = new ImU8String(profiler.BuildReport());
                    ImGui.SetClipboardText(report.Span);
                    _performanceCopyStatus = "Copied. Paste the report into your reply.";
                }
                catch (Exception ex)
                {
                    _performanceCopyStatus = $"Clipboard copy failed: {ex.Message}";
                }
            }
        }

        if (!string.IsNullOrEmpty(_performanceCopyStatus))
            ImGui.TextDisabled(_performanceCopyStatus);

        if (!snapshot.HasSamples)
        {
            ImGui.Separator();
            ImGui.TextDisabled("No performance capture yet. Start a capture, reproduce the activity you want to measure, then stop and copy the report.");
        }
        else
        {
            ImGui.Separator();
            ImGui.TextDisabled($"Samples {snapshot.SampleCount:N0} · sampled interval {snapshot.SampledDurationSeconds:F2} s · capture started {profiler.CaptureStartedUtc.ToLocalTime():T}");

            ImGui.TextDisabled("Frame pacing — framework update interval");
            ImGui.Text($"Average: {snapshot.AverageFps:F1} FPS · {snapshot.AverageFrameMilliseconds:F2} ms/frame");
            ImGui.Text($"P50 {snapshot.P50FrameMilliseconds:F2} ms · P95 {snapshot.P95FrameMilliseconds:F2} ms · P99 {snapshot.P99FrameMilliseconds:F2} ms · worst {snapshot.WorstFrameMilliseconds:F2} ms");
            ImGui.Text($"1% low {snapshot.OnePercentLowFps:F1} FPS · 0.1% low {snapshot.OneTenthPercentLowFps:F1} FPS");
            ImGui.Text($"Slow frames: >33.3 ms {snapshot.FramesOver33Milliseconds:N0} · >50 ms {snapshot.FramesOver50Milliseconds:N0} · >100 ms {snapshot.FramesOver100Milliseconds:N0}");
            ImGui.TextDisabled($"Distribution: <16.7 ms {snapshot.FramesUnder16Milliseconds:N0} · 16.7–33.3 ms {snapshot.Frames16To33Milliseconds:N0} · 33.3–50 ms {snapshot.Frames33To50Milliseconds:N0} · 50–100 ms {snapshot.Frames50To100Milliseconds:N0} · ≥100 ms {snapshot.FramesAtLeast100Milliseconds:N0}");

            ImGui.Separator();
            ImGui.TextDisabled("FPSBooster housing-culling CPU time");
            double activePercent = snapshot.SampleCount > 0 ? snapshot.CullingActiveFrames * 100d / snapshot.SampleCount : 0d;
            ImGui.Text($"Culling active: {snapshot.CullingActiveFrames:N0}/{snapshot.SampleCount:N0} frames ({activePercent:F1}%)");
            ImGui.Text($"Update average {snapshot.AverageCullingUpdateMilliseconds:F4} ms · P95 {snapshot.P95CullingUpdateMilliseconds:F4} ms · max {snapshot.MaximumCullingUpdateMilliseconds:F4} ms");
            ImGui.TextDisabled($"Total measured culling work: {snapshot.TotalCullingUpdateMilliseconds:F2} ms");

            if (snapshot.NativeTextureCaptureFrames > 0)
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1f, .65f, .35f, 1f), $"Native texture lifecycle hooks were enabled for {snapshot.NativeTextureCaptureFrames:N0} sampled frames. This capture is instrumented and may include hook overhead.");
            }
        }

        ImGui.Separator();
        ImGui.TextWrapped("Recommended test: capture 30–60 seconds in a repeatable scene with the same camera and character state. Keep Native Textures lifecycle capture disabled for the clean baseline. Copy one report with housing culling off and another with it on to measure the feature's actual cost/benefit.");
        ImGui.TextWrapped("For texture-loading investigations, run a separate pass with Native Textures hooks enabled. Those per-operation timings are useful for diagnosis but should not be mixed with an uninstrumented frame-pacing baseline.");
        ImGui.TextDisabled("Capture continues while this window is closed. Reopen it with /fpsbooster to stop and copy the report.");
        ImGui.TextDisabled("Capture limit: 18,000 framework updates (about 5 minutes at 60 FPS). The capture stops automatically at the limit.");
    }

    private void DrawTextures()
    {
        var inspector = _plugin.TextureStreaming;

        ImGui.Text("Native texture capture");
        ImGui.TextDisabled("Inspect a game-relative .tex path, acquire its native handle, then capture fresh loads.");

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Game resource path (.tex)", ref _texturePath, 512);

        if (ImGui.Button("Inspect path", new Vector2(130f, 0f)))
        {
            StopProgressiveSequence("Stopped because a different texture was inspected.", clearResults: true);
            inspector.Clear();
            _plugin.TextureGpuUpload.Reset();
            _plugin.NativeTextureLifecycle.StopCapture();
            _plugin.NativeTextureLifecycle.CancelNativeMipPayloadCapture();
            _plugin.NativeProgressiveTexturePreview.Reset();
            _plugin.NativeTextureHandle.Release();
            _plugin.PenumbraTextureSource.Clear();

            string inputPath = _texturePath.Trim().Trim('"');
            _texturePath = inputPath;
            _textureInputError = null;

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                _textureInputError = "Enter a game-relative FFXIV .tex resource path.";
            }
            else if (Path.IsPathRooted(inputPath))
            {
                _textureInputError = "Use the game-relative resource path here, not the mod's absolute disk path.";
            }
            else
            {
                try
                {
                    inspector.Inspect(inputPath);
                }
                catch (Exception ex)
                {
                    _textureInputError = ex.Message;
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear", new Vector2(90f, 0f)))
        {
            StopProgressiveSequence("Stopped because the texture inspection was cleared.", clearResults: true);
            inspector.Clear();
            _plugin.TextureGpuUpload.Reset();
            _plugin.NativeTextureLifecycle.StopCapture();
            _plugin.NativeTextureLifecycle.CancelNativeMipPayloadCapture();
            _plugin.NativeProgressiveTexturePreview.Reset();
            _plugin.NativeTextureHandle.Release();
            _plugin.PenumbraTextureSource.Clear();
            _texturePath = string.Empty;
            _textureInputError = null;
        }

        if (!string.IsNullOrEmpty(_textureInputError))
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), _textureInputError);
        else if (!string.IsNullOrEmpty(inspector.Error))
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), inspector.Error);
        else if (inspector.IsLoading)
            ImGui.TextDisabled("Reading vanilla SqPack reference metadata...");
        else if (inspector.Report == null)
            ImGui.TextDisabled("Workflow: 1) Inspect path  2) Acquire handle  3) Start capture  4) Trigger a fresh texture load.");

        var report = inspector.Report;
        if (report == null)
            return;

        ImGui.Separator();
        ImGui.TextDisabled("Vanilla SqPack reference");
        ImGui.TextWrapped(report.Path);
        ImGui.TextDisabled($"{report.Width} × {report.Height} · {report.Format} · {report.MipCount} mips · {FormatBytes(report.TotalMipBytes)} mip payload");

        DrawNativeTextureHandleProbe(report);
    }

    private static void DrawMipReadResult(TextureMipReadReport mipRead, bool isLooseFile)
    {
        ImGui.TextColored(Green, mipRead.Message);
        if (isLooseFile)
        {
            ImGui.TextDisabled($"Mip {mipRead.MipIndex} · {FormatBytes(mipRead.DataBytesRequested)} raw source bytes requested · {FormatBytes(mipRead.DecompressedBytes)} output · {mipRead.ElapsedMilliseconds:F2} ms read (no decompression)");
            ImGui.TextDisabled($"SHA-256: {mipRead.Sha256}");
            ImGui.TextDisabled(mipRead.BlockTypeSummary);
        }
        else
        {
            ImGui.TextDisabled($"Mip {mipRead.MipIndex} · {FormatBytes(mipRead.DeclaredGroupStoredBytes)} stored group · {FormatBytes(mipRead.DataBytesRequested)} block data + {FormatBytes(mipRead.BlockTableBytesRequested)} stride table = {FormatBytes(mipRead.GroupBytesRequested)} requested · {FormatBytes(mipRead.DecompressedBytes)} output · {mipRead.BlockCount} blocks · {mipRead.ElapsedMilliseconds:F2} ms read/decompress");
            ImGui.TextDisabled($"SHA-256: {mipRead.Sha256}");
            if (!string.IsNullOrEmpty(mipRead.BlockTypeSummary))
                ImGui.TextDisabled($"SqPack block types: {mipRead.BlockTypeSummary}");
        }
    }

    private void DrawNativeTextureHandleProbe(TextureFileReport report)
    {
        var probe = _plugin.NativeTextureHandle;

        ImGui.Separator();
        ImGui.Text("Native resource");
        ImGui.TextDisabled("Acquire uses FFXIV's normal asynchronous resource request. It may reuse a cached handle or begin a load.");

        if (!probe.IsAcquired)
        {
            if (ImGui.Button("Acquire native handle", new Vector2(190f, 0f)))
            {
                try { probe.Acquire(report.Path); }
                catch (Exception ex) { probe.SetError(ex.Message); }
            }
        }
        else
        {
            if (ImGui.Button("Release native handle", new Vector2(190f, 0f)))
            {
                _plugin.NativeTextureLifecycle.StopCapture();
                _plugin.NativeTextureLifecycle.CancelNativeMipPayloadCapture();
                _plugin.NativeProgressiveTexturePreview.Reset();
                probe.Release();
            }
        }

        if (!string.IsNullOrEmpty(probe.Error))
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), probe.Error);
        else
            ImGui.TextDisabled(probe.Status);

        var snapshot = probe.Refresh();
        if (snapshot != null)
        {
            if (snapshot.HasTextureObject)
            {
                string gpuState = snapshot.D3D11TextureAddress != 0 ? "GPU texture ready" : "GPU texture pending";
                ImGui.TextColored(Green,
                    $"{snapshot.ActualWidth} × {snapshot.ActualHeight} · {snapshot.TextureFormat} · {snapshot.TextureMipLevelRaw} mips · {gpuState}");
            }
            else
            {
                ImGui.TextDisabled("Waiting for the persistent native Texture object...");
            }

            if (ImGui.TreeNode("Native handle details"))
            {
                ImGui.TextDisabled($"Handle 0x{(nuint)snapshot.HandleAddress:X} · resource ID {snapshot.ResourceId} · size 0x{snapshot.RuntimeHandleSize:X} · refcount {snapshot.ReferenceCount}");
                ImGui.TextDisabled($"File sizes {snapshot.FileSize:N0} / {snapshot.FileSize2:N0} / {snapshot.FileSize3:N0} · last I/O {snapshot.LastIoResult}");
                ImGui.TextDisabled($"Header {snapshot.HeaderWidth} × {snapshot.HeaderHeight} · {snapshot.HeaderFormat} · {snapshot.HeaderMipCountAndFlag} mips/flags · {snapshot.HeaderType}");
                ImGui.TextDisabled($"ChangeLod raw byte +0x104: 0x{snapshot.LodFlagByte:X2} (read-only; semantics not established)");

                if (snapshot.HasTextureObject)
                {
                    ImGui.Separator();
                    ImGui.TextDisabled($"Texture object 0x{(nuint)snapshot.TextureObjectAddress:X} · actual {snapshot.ActualWidth} × {snapshot.ActualHeight} · allocated {snapshot.AllocatedWidth} × {snapshot.AllocatedHeight}");
                    ImGui.TextDisabled($"Depth {snapshot.Depth} · array {snapshot.ArraySize} · flags {snapshot.TextureFlags}");
                    ImGui.TextDisabled($"D3D11 texture 0x{(nuint)snapshot.D3D11TextureAddress:X} · SRV 0x{(nuint)snapshot.D3D11ShaderResourceViewAddress:X}");
                }

                ImGui.TreePop();
            }
        }

        DrawNativeTextureLifecycleProbe(probe, snapshot, report);
    }

    private void DrawNativeTextureLifecycleProbe(
        NativeTextureHandleProbe handleProbe,
        NativeTextureHandleSnapshot? handleSnapshot,
        TextureFileReport? inspectedReport)
    {
        var lifecycle = _plugin.NativeTextureLifecycle;
        lifecycle.ObserveUiThread();
        lifecycle.DrainEvents();

        ImGui.Separator();
        ImGui.Text("Lifecycle capture");

        if (ImGui.Button("Copy diagnostics", new Vector2(150f, 0f)))
        {
            var clipboardText = new ImU8String(BuildDiagnosticsClipboardText(handleProbe, handleSnapshot, inspectedReport, lifecycle, _plugin.NativeProgressiveTexturePreview));
            try
            {
                ImGui.SetClipboardText(clipboardText.Span);
                _diagnosticsCopyStatus = "Copied. Paste the report into your reply.";
            }
            catch (Exception ex)
            {
                _diagnosticsCopyStatus = $"Clipboard copy failed: {ex.Message}";
            }
            finally
            {
                clipboardText.Recycle();
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear events"))
            lifecycle.ClearEvents();
        if (!string.IsNullOrEmpty(_diagnosticsCopyStatus))
            ImGui.TextDisabled(_diagnosticsCopyStatus);
        else
            ImGui.TextDisabled("Copies the handle snapshot, hook status, and all retained lifecycle events.");

        if (!handleProbe.IsAcquired)
        {
            ImGui.TextDisabled("Acquire the native handle before starting capture.");
            return;
        }

        if (!lifecycle.IsInstalled || !lifecycle.CaptureEnabled)
        {
            string buttonText = lifecycle.IsInstalled ? "Resume capture" : "Start capture";
            if (ImGui.Button(buttonText, new Vector2(150f, 0f)))
            {
                try
                {
                    // NativeHandle is a pointer; keep the unsafe boundary limited to this call.
                    unsafe { lifecycle.StartCapture(handleProbe.NativeHandle); }
                }
                catch (Exception ex) { lifecycle.SetError(ex.Message); }
            }
        }
        else if (ImGui.Button("Stop capture", new Vector2(150f, 0f)))
        {
            lifecycle.StopCapture();
        }

        if (!string.IsNullOrEmpty(lifecycle.Error))
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), lifecycle.Error);
        else
            ImGui.TextDisabled(lifecycle.Status);

        string readHook = lifecycle.FileDescriptorReadHookInstalled
            ? "FileDescriptor.Read: installed"
            : lifecycle.FileDescriptorReadStatus.StartsWith("Unavailable", StringComparison.OrdinalIgnoreCase)
                ? "FileDescriptor.Read: unavailable (optional hook)"
                : "FileDescriptor.Read: not installed";
        ImGui.TextDisabled($"Events {lifecycle.CapturedEventCount:N0} · queued {lifecycle.PendingEventCount:N0} · dropped {lifecycle.DroppedEventCount:N0} · errors {lifecycle.CaptureErrorCount:N0}");
        ImGui.TextDisabled(readHook);
        ImGui.TextDisabled(lifecycle.TextureInitializeContentsHookInstalled
            ? "Texture.InitializeContents: installed"
            : lifecycle.TextureInitializeContentsStatus.StartsWith("Unavailable", StringComparison.OrdinalIgnoreCase)
                ? "Texture.InitializeContents: unavailable (optional hook)"
                : "Texture.InitializeContents: not installed");
        ImGui.TextDisabled(lifecycle.D3D11CreateTexture2DHookInstalled
            ? "D3D11 CreateTexture2D: installed (initialization scope only)"
            : lifecycle.D3D11CreateTexture2DStatus.StartsWith("Unavailable", StringComparison.OrdinalIgnoreCase)
                ? "D3D11 CreateTexture2D: unavailable (optional hook)"
                : "D3D11 CreateTexture2D: not installed");
        ImGui.TextDisabled(lifecycle.D3D11CreateShaderResourceViewHookInstalled
            ? "D3D11 CreateShaderResourceView: installed (matching texture only)"
            : lifecycle.D3D11CreateShaderResourceViewStatus.StartsWith("Unavailable", StringComparison.OrdinalIgnoreCase)
                ? "D3D11 CreateShaderResourceView: unavailable (optional hook)"
                : "D3D11 CreateShaderResourceView: not installed");

        if (lifecycle.IsInstalled && ImGui.TreeNode("Hook details"))
        {
            ImGui.TextDisabled($"VTable 0x{(nuint)lifecycle.VtableAddress:X}");
            ImGui.TextDisabled($"Read 0x{(nuint)lifecycle.ReadAddress:X} · Load 0x{(nuint)lifecycle.LoadAddress:X} · Reread 0x{(nuint)lifecycle.RereadAddress:X}");
            ImGui.TextDisabled($"FileJob 0x{(nuint)lifecycle.FileJobAddress:X} · FinishLoad 0x{(nuint)lifecycle.FinishLoadAddress:X}");
            ImGui.TextDisabled($"FileDescriptor.Read 0x{(nuint)lifecycle.FileDescriptorReadAddress:X} · {lifecycle.FileDescriptorReadStatus}");
            ImGui.TextDisabled($"Texture.InitializeContents 0x{(nuint)lifecycle.TextureInitializeContentsAddress:X} · {lifecycle.TextureInitializeContentsStatus}");
            ImGui.TextDisabled($"D3D11 CreateTexture2D 0x{(nuint)lifecycle.D3D11CreateTexture2DAddress:X} · {lifecycle.D3D11CreateTexture2DStatus}");
            ImGui.TextDisabled($"D3D11 CreateShaderResourceView 0x{(nuint)lifecycle.D3D11CreateShaderResourceViewAddress:X} · {lifecycle.D3D11CreateShaderResourceViewStatus}");
            ImGui.TextDisabled($"UI draw thread {lifecycle.LastObservedUiThreadId} · Read/Reread shared target: {lifecycle.ReadAndRereadShareTarget}");
            ImGui.TreePop();
        }

        DrawNativeProgressiveTexturePreview(lifecycle, handleSnapshot);

        if (lifecycle.RecentEvents.Count == 0)
        {
            ImGui.TextDisabled("No events yet. Start capture, then change gear or enter an area that loads new textures.");
            return;
        }

        if (ImGui.TreeNode($"Recent events ({lifecycle.RecentEvents.Count})"))
        {
            int shown = Math.Min(12, lifecycle.RecentEvents.Count);
            for (int i = 0; i < shown; i++)
            {
                var item = lifecycle.RecentEvents[i];
                string fileName = string.IsNullOrEmpty(item.ResourceFileName)
                    ? "(unknown resource)"
                    : Path.GetFileName(item.ResourceFileName.Replace('\\', '/')) ?? item.ResourceFileName;
                string label = $"#{item.Sequence} · {item.Operation} · {item.ElapsedMilliseconds:F3} ms · {fileName}##LifecycleEvent{item.Sequence}";
                if (!ImGui.TreeNode(label))
                    continue;

                ImGui.TextWrapped(item.ResourceFileName);
                ImGui.TextDisabled($"Handle 0x{(nuint)item.HandleAddress:X} · resource {item.ResourceId} · thread {item.ThreadId} · result {item.Result}");
                ImGui.TextDisabled($"File mode {item.FileModeName} → {item.ExitFileModeName} · offset 0x{item.FileOffset:X} · requested {item.RequestedLength:N0} → {item.ExitRequestedLength:N0}");

                if (item.Operation == "FileJob")
                {
                    bool sameAsUiThread = lifecycle.LastObservedUiThreadId != 0 && item.ThreadId == lifecycle.LastObservedUiThreadId;
                    ImGui.TextDisabled($"Priority {item.Priority} · synchronous {item.InputFlag != 0} · same thread as UI draw: {sameAsUiThread}");
                }
                else if (item.Operation == "FinishLoad")
                {
                    ImGui.TextDisabled($"Open-file result {item.InputFlag} · secondary argument {item.SecondaryInput}");
                }

                if (item.FileDescriptorAddress != 0)
                    ImGui.TextDisabled($"FileDescriptor 0x{(nuint)item.FileDescriptorAddress:X}");

                ImGui.TextDisabled($"ReadState {item.StateBefore.ReadState} → {item.StateAfter.ReadState} · LoadState {item.StateBefore.LoadState} → {item.StateAfter.LoadState}");
                ImGui.TextDisabled($"Blob 0x{(nuint)item.StateBefore.BlobAddress:X} → 0x{(nuint)item.StateAfter.BlobAddress:X} · GetData 0x{(nuint)item.StateBefore.DataAddress:X}/{item.StateBefore.DataLength:N0} → 0x{(nuint)item.StateAfter.DataAddress:X}/{item.StateAfter.DataLength:N0}");

                if (item.Operation == "FileDescriptor.Read")
                {
                    ImGui.TextDisabled($"Output 0x{(nuint)item.IoOutputBufferAddress:X} · length {item.IoLength:N0} · offset 0x{item.IoStart:X} · reset {item.IoResetPosition != 0}");
                    if (item.TextureHeaderLayout is { } headerLayout)
                    {
                        ImGui.TextDisabled($"Header output offset +0x{(nuint)item.OutputOffsetFromHandle:X} · field offset +0x{(nuint)headerLayout.HeaderOffsetFromHandle:X} · {headerLayout.HeaderSize} bytes · matches {item.OutputMatchesTextureHeader}");
                        ImGui.TextWrapped($"LOD offsets: {headerLayout.LodOffsets}");
                        ImGui.TextWrapped($"Surface offsets: {headerLayout.OffsetsToSurfaces}");
                    }
                }

                if (item.Operation == "Load")
                {
                    ImGui.TextDisabled($"Contents 0x{(nuint)item.LoadContentsAddress:X} · matches descriptor: {item.LoadContentsMatchesDescriptor}");
                    ImGui.TextDisabled($"TextureWhileLoading 0x{(nuint)item.TextureWhileLoadingAddress:X} · DataWhileLoading 0x{(nuint)item.DataWhileLoadingAddress:X}");
                }

                if (item.Operation is "Load" or "FinishLoad" or "FileDescriptor.Read")
                {
                    if (item.Width != 0 || item.Height != 0)
                        ImGui.TextDisabled($"Header {item.Width} × {item.Height} × {item.Depth} · {item.Format} · {item.MipCountAndFlag} mip/flag · Texture 0x{(nuint)item.TextureAddress:X}");
                }

                if (item.TextureInitialization is { } initialization)
                {
                    ImGui.TextDisabled($"InitializeContents object 0x{(nuint)initialization.TextureObjectAddress:X} · input 0x{(nuint)initialization.ContentsAddress:X}");
                    ImGui.TextDisabled($"Payload buffer 0x{(nuint)initialization.PayloadBufferAddress:X} · {initialization.PayloadLength:N0} bytes at 0x{initialization.PayloadOffset:X} · input matches: {initialization.ContentsMatchesPayloadBuffer} · delta {(initialization.PayloadBufferAddress == 0 ? "n/a" : FormatSignedOffset(initialization.ContentsOffsetFromPayloadBuffer))}");
                    ImGui.TextDisabled($"Texture matches active handle: {initialization.TextureMatchesActiveHandle}");
                    ImGui.TextDisabled($"D3D11 texture {FormatPointer(initialization.Before.D3D11TextureAddress)} → {FormatPointer(initialization.After.D3D11TextureAddress)} · SRV {FormatPointer(initialization.Before.D3D11ShaderResourceViewAddress)} → {FormatPointer(initialization.After.D3D11ShaderResourceViewAddress)}");
                    ImGui.TextWrapped($"Native association audit: {initialization.ResourceRelationship.Status}");
                    ImGui.TextDisabled($"SRV backing resource: {FormatPointer(initialization.ResourceRelationship.ViewGetResourceAddress)} · same COM texture: {initialization.ResourceRelationship.ViewResourceIdentityCheckSucceeded && initialization.ResourceRelationship.ViewResourceIdentityMatchesTexture}");
                    ImGui.TextDisabled($"Texture/SRV share device: {initialization.ResourceRelationship.TextureViewDeviceIdentityCheckSucceeded && initialization.ResourceRelationship.TextureAndViewShareDevice}");
                    ImGui.TextDisabled($"Nested D3D11 CreateTexture2D calls: {initialization.D3D11TextureCreateCalls.Length}");
                }

                ImGui.TreePop();
            }

            if (lifecycle.RecentEvents.Count > shown)
                ImGui.TextDisabled($"Showing newest {shown} of {lifecycle.RecentEvents.Count} events.");

            ImGui.TreePop();
        }
    }

    private void DrawNativeProgressiveTexturePreview(
        NativeTextureLifecycleProbe lifecycle,
        NativeTextureHandleSnapshot? selectedHandle)
    {
        var preview = _plugin.NativeProgressiveTexturePreview;
        bool createdThisFrame = false;

        if (lifecycle.TryTakeNativeMipPayloadCapture(out NativeTextureMipPayloadCapture? capture) && capture != null)
        {
            try
            {
                preview.Create(capture);
                createdThisFrame = true;
            }
            catch (Exception ex)
            {
                preview.Reset();
                preview.SetError(ex.Message);
            }
        }

        ImGui.Separator();
        ImGui.Text("Progressive shadow preview");
        ImGui.TextDisabled("One-shot diagnostic: copies the selected native resource's BC7 payload, then builds a separate DEFAULT texture. Other BC7 resources are ignored. This temporarily adds a CPU copy and a second GPU texture; the live FFXIV texture is never replaced or modified.");

        if (!preview.IsCreated)
        {
            if (lifecycle.NativeMipPayloadCaptureArmed)
            {
                ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), lifecycle.NativeMipPayloadCaptureStatus);
                if (ImGui.Button("Cancel native mip capture", new Vector2(190f, 0f)))
                    lifecycle.CancelNativeMipPayloadCapture();
            }
            else
            {
                bool canArm = lifecycle.CaptureEnabled && lifecycle.D3D11CreateTexture2DHookInstalled
                    && selectedHandle != null && selectedHandle.ResourceId != 0
                    && !lifecycle.HasPendingNativeMipPayloadCapture;
                if (!canArm)
                    ImGui.BeginDisabled();
                if (ImGui.Button("Arm selected resource capture", new Vector2(230f, 0f)))
                {
                    try { lifecycle.ArmNativeMipPayloadCapture(selectedHandle!.ResourceId); }
                    catch (Exception ex) { lifecycle.SetError(ex.Message); }
                }
                if (!canArm)
                    ImGui.EndDisabled();

                if (selectedHandle != null)
                ImGui.TextDisabled($"Capture target: selected resource ID {selectedHandle.ResourceId}. Other BC7 textures will be ignored.");
                else
                    ImGui.TextDisabled("Refresh the selected native handle before arming a progressive capture.");
                ImGui.TextDisabled(lifecycle.NativeMipPayloadCaptureStatus);
                if (!lifecycle.CaptureEnabled || !lifecycle.D3D11CreateTexture2DHookInstalled)
                    ImGui.TextDisabled("Start lifecycle capture first. This copies up to 64 MiB only after you explicitly arm it.");
            }

            if (!string.IsNullOrEmpty(preview.Error))
                ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), preview.Error);
            return;
        }

        // Uploads and SRV changes happen here on the Dalamud UI draw thread, never inside a
        // native resource callback. The coarsest mip was already uploaded when the capture arrived.
        if (!createdThisFrame && !preview.IsComplete)
            preview.AdvanceFrame();

        ImGui.TextColored(preview.IsComplete ? Green : Muted,
            preview.IsComplete ? "Shadow refinement complete" : "Coarse-to-fine refinement running");
        ImGui.TextWrapped($"Source: {preview.ResourceFileName}");
        ImGui.TextDisabled($"Resource ID {preview.ResourceId}");
        ImGui.TextDisabled($"{preview.Width} × {preview.Height} BC7 · {preview.UploadedMipCount}/{preview.MipCount} mips · visible Mip {preview.FirstVisibleMip}–{preview.MipCount - 1}");
        ImGui.TextDisabled($"Payload copied {FormatBytes(preview.TotalMipPayloadBytes)} · uploaded {FormatBytes(preview.UploadedBytes)} · remaining {FormatBytes(Math.Max(0L, preview.TotalMipPayloadBytes - preview.UploadedBytes))}");
        ImGui.TextDisabled($"Payload copy {preview.CaptureCopyMilliseconds:F3} ms · capture → coarse SRV ready {FormatOptionalMilliseconds(preview.CaptureToFirstPreviewMilliseconds)} · capture → full-detail SRV ready {FormatOptionalMilliseconds(preview.CaptureToFullResolutionMilliseconds)}");
        ImGui.TextDisabled($"Initial preview setup {preview.InitialPreviewSetupMilliseconds:F3} ms (coarsest mip {preview.InitialMipUploadMilliseconds:F3} ms · SRV {preview.InitialViewCreationMilliseconds:F3} ms)");
        ImGui.TextDisabled($"Last frame: {preview.LastFrameMipCount} mip(s), {FormatBytes(preview.LastFrameBytes)}, {preview.LastFrameUploadMilliseconds:F3} ms submission · max {preview.MaximumFrameUploadMilliseconds:F3} ms");
        ImGui.TextDisabled($"UI frames {preview.TotalUiFrameCount} total (initial + {preview.RefinementFrameCount} refinement) · budget deferrals {preview.BudgetDeferrals} · large-mip isolation deferrals {preview.LargeMipIsolationDeferrals}");
        ImGui.TextWrapped(preview.Status);

        float displayWidth = Math.Clamp(ImGui.GetWindowWidth() - 48f, 180f, 440f);
        float aspect = preview.Height > 0 ? (float)preview.Width / preview.Height : 1f;
        float displayHeight = Math.Clamp(displayWidth / Math.Max(0.01f, aspect), 96f, 280f);
        ImGui.Image(preview.PreviewTextureId, new Vector2(displayWidth, displayHeight));

        if (ImGui.Button("Discard shadow preview", new Vector2(190f, 0f)))
        {
            preview.Reset();
            lifecycle.CancelNativeMipPayloadCapture();
        }
    }

    private static string FormatOptionalMilliseconds(double milliseconds) =>
        milliseconds < 0d ? "pending" : $"{milliseconds:F2} ms";

    private static string BuildDiagnosticsClipboardText(
        NativeTextureHandleProbe handleProbe,
        NativeTextureHandleSnapshot? snapshot,
        TextureFileReport? inspectedReport,
        NativeTextureLifecycleProbe lifecycle,
        NativeProgressiveTexturePreview preview)
    {
        var sb = new StringBuilder(32 * 1024);
        string version = typeof(ConfigWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
        int uiThreadId = lifecycle.LastObservedUiThreadId;

        sb.AppendLine("FPSBooster native texture diagnostics");
        sb.AppendLine($"Generated UTC: {DateTime.UtcNow:O}");
        sb.AppendLine($"Plugin version: {version}");
        sb.AppendLine();

        sb.AppendLine("=== Selected resource ===");
        sb.AppendLine($"Game resource path: {(string.IsNullOrWhiteSpace(handleProbe.Path) ? "(none)" : handleProbe.Path)}");
        sb.AppendLine($"Handle acquired: {handleProbe.IsAcquired}");
        sb.AppendLine($"Handle status: {handleProbe.Status}");
        sb.AppendLine($"Handle error: {handleProbe.Error ?? "(none)"}");
        if (inspectedReport != null)
        {
            sb.AppendLine("Original SqPack reference metadata (logical game path; not the redirected disk replacement):");
            sb.AppendLine($"  Path: {inspectedReport.Path}");
            sb.AppendLine($"  Dimensions: {inspectedReport.Width} x {inspectedReport.Height}");
            sb.AppendLine($"  Format: {inspectedReport.Format}");
            sb.AppendLine($"  Mips: {inspectedReport.MipCount}");
            sb.AppendLine($"  Total mip payload: {inspectedReport.TotalMipBytes:N0} bytes");
        }
        else
        {
            sb.AppendLine("SqPack reference metadata: unavailable");
        }
        sb.AppendLine();

        sb.AppendLine("=== Current native handle snapshot ===");
        if (snapshot == null)
        {
            sb.AppendLine("Snapshot unavailable (no handle or native texture object not yet inspectable).");
        }
        else
        {
            sb.AppendLine($"Handle: {FormatPointer(snapshot.HandleAddress)} · size 0x{snapshot.RuntimeHandleSize:X} · refcount {snapshot.ReferenceCount}");
            sb.AppendLine($"Resource ID: {snapshot.ResourceId}");
            sb.AppendLine($"File sizes: {snapshot.FileSize:N0} / {snapshot.FileSize2:N0} / {snapshot.FileSize3:N0}");
            sb.AppendLine($"Last I/O result: {snapshot.LastIoResult} (0x{snapshot.LastIoResult:X2})");
            sb.AppendLine($"TEX header: {snapshot.HeaderWidth} x {snapshot.HeaderHeight} x {snapshot.HeaderDepth} · {snapshot.HeaderFormat} · mip/flag byte {snapshot.HeaderMipCountAndFlag} (0x{snapshot.HeaderMipCountAndFlag:X2}) · {snapshot.HeaderType} · array size {snapshot.HeaderArraySize}");
            sb.AppendLine($"ChangeLod raw byte (+0x104): 0x{snapshot.LodFlagByte:X2}; bit 0={snapshot.ChangeLodBit} (semantics not established; read-only)");
            sb.AppendLine($"Persistent Texture object: {FormatPointer(snapshot.TextureObjectAddress)} · available {snapshot.HasTextureObject}");
            if (snapshot.HasTextureObject)
            {
                sb.AppendLine($"  Actual dimensions: {snapshot.ActualWidth} x {snapshot.ActualHeight}");
                sb.AppendLine($"  Allocated dimensions: {snapshot.AllocatedWidth} x {snapshot.AllocatedHeight}");
                sb.AppendLine($"  Depth: {snapshot.Depth} · array size {snapshot.ArraySize} · raw mip level {snapshot.TextureMipLevelRaw}");
                sb.AppendLine($"  Texture format: {snapshot.TextureFormat}");
                sb.AppendLine($"  Texture flags: {snapshot.TextureFlags}");
                sb.AppendLine($"  D3D11 texture: {FormatPointer(snapshot.D3D11TextureAddress)}");
                sb.AppendLine($"  D3D11 SRV: {FormatPointer(snapshot.D3D11ShaderResourceViewAddress)}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("=== Native lifecycle capture ===");
        sb.AppendLine($"Hooks installed: {lifecycle.IsInstalled}");
        sb.AppendLine($"Capture enabled: {lifecycle.CaptureEnabled}");
        sb.AppendLine($"Status: {lifecycle.Status}");
        sb.AppendLine($"Error: {lifecycle.Error ?? "(none)"}");
        sb.AppendLine($"Events captured: {lifecycle.CapturedEventCount}");
        sb.AppendLine($"Events queued: {lifecycle.PendingEventCount}");
        sb.AppendLine($"Events dropped: {lifecycle.DroppedEventCount}");
        sb.AppendLine($"Capture errors: {lifecycle.CaptureErrorCount}");
        sb.AppendLine($"UI draw thread: {(uiThreadId == 0 ? "unknown" : uiThreadId.ToString())}");
        sb.AppendLine($"Read/Reread share target: {lifecycle.ReadAndRereadShareTarget}");
        sb.AppendLine($"FileDescriptor.Read hook installed: {lifecycle.FileDescriptorReadHookInstalled}");
        sb.AppendLine($"FileDescriptor.Read status: {lifecycle.FileDescriptorReadStatus}");
        sb.AppendLine($"Texture.InitializeContents hook installed: {lifecycle.TextureInitializeContentsHookInstalled}");
        sb.AppendLine($"Texture.InitializeContents status: {lifecycle.TextureInitializeContentsStatus}");
        sb.AppendLine($"D3D11 CreateTexture2D hook installed: {lifecycle.D3D11CreateTexture2DHookInstalled}");
        sb.AppendLine($"D3D11 CreateTexture2D status: {lifecycle.D3D11CreateTexture2DStatus}");
        sb.AppendLine($"D3D11 CreateShaderResourceView hook installed: {lifecycle.D3D11CreateShaderResourceViewHookInstalled}");
        sb.AppendLine($"D3D11 CreateShaderResourceView status: {lifecycle.D3D11CreateShaderResourceViewStatus}");
        sb.AppendLine($"Native progressive mip capture armed: {lifecycle.NativeMipPayloadCaptureArmed}");
        sb.AppendLine($"Native progressive mip capture pending: {lifecycle.HasPendingNativeMipPayloadCapture}");
        sb.AppendLine($"Native progressive mip capture target resource ID: {lifecycle.NativeMipPayloadCaptureTargetResourceId?.ToString() ?? "(none)"}");
        sb.AppendLine($"Native progressive mip capture status: {lifecycle.NativeMipPayloadCaptureStatus}");
        sb.AppendLine();
        sb.AppendLine("=== Progressive shadow preview ===");
        sb.AppendLine($"Created: {preview.IsCreated}");
        sb.AppendLine($"Complete: {preview.IsComplete}");
        sb.AppendLine($"Status: {preview.Status}");
        sb.AppendLine($"Error: {preview.Error ?? "(none)"}");
        sb.AppendLine($"Captured resource ID: {preview.ResourceId}");
        sb.AppendLine($"Resource filename: {(string.IsNullOrWhiteSpace(preview.ResourceFileName) ? "(none)" : preview.ResourceFileName)}");
        sb.AppendLine($"Dimensions: {(preview.Width == 0 ? "n/a" : $"{preview.Width}x{preview.Height}")}");
        sb.AppendLine($"Mips uploaded/total: {preview.UploadedMipCount}/{preview.MipCount}");
        sb.AppendLine($"First visible mip: {preview.FirstVisibleMip}");
        sb.AppendLine($"Payload bytes copied: {preview.TotalMipPayloadBytes}");
        sb.AppendLine($"Payload bytes uploaded: {preview.UploadedBytes}");
        sb.AppendLine($"Payload copy: {preview.CaptureCopyMilliseconds:F3} ms");
        sb.AppendLine($"Capture-to-coarse-SRV-ready: {FormatOptionalMilliseconds(preview.CaptureToFirstPreviewMilliseconds)}");
        sb.AppendLine($"Capture-to-full-detail-SRV-ready: {FormatOptionalMilliseconds(preview.CaptureToFullResolutionMilliseconds)}");
        sb.AppendLine($"Initial preview setup (allocation + initial mip + SRV): {preview.InitialPreviewSetupMilliseconds:F3} ms");
        sb.AppendLine($"Initial mip upload: {preview.InitialMipUploadMilliseconds:F3} ms");
        sb.AppendLine($"Initial SRV creation: {preview.InitialViewCreationMilliseconds:F3} ms");
        sb.AppendLine($"Total UI frames including initial preview: {preview.TotalUiFrameCount}");
        sb.AppendLine($"Additional refinement frames: {preview.RefinementFrameCount}");
        sb.AppendLine($"Last frame: {preview.LastFrameMipCount} mips, {preview.LastFrameBytes} bytes, {preview.LastFrameUploadMilliseconds:F3} ms");
        sb.AppendLine($"Maximum frame upload submission: {preview.MaximumFrameUploadMilliseconds:F3} ms");
        sb.AppendLine($"Budget deferrals: {preview.BudgetDeferrals}");
        sb.AppendLine($"Largest-mip isolation deferrals: {preview.LargeMipIsolationDeferrals}");
        sb.AppendLine($"Last deferred estimate / remaining budget: {preview.LastDeferredEstimateMilliseconds:F3} / {preview.LastDeferredRemainingMilliseconds:F3} ms");
        sb.AppendLine("Hook addresses:");
        sb.AppendLine($"  VTable: {FormatPointer(lifecycle.VtableAddress)}");
        sb.AppendLine($"  Read: {FormatPointer(lifecycle.ReadAddress)}");
        sb.AppendLine($"  Load: {FormatPointer(lifecycle.LoadAddress)}");
        sb.AppendLine($"  Reread: {FormatPointer(lifecycle.RereadAddress)}");
        sb.AppendLine($"  FileJob: {FormatPointer(lifecycle.FileJobAddress)}");
        sb.AppendLine($"  FinishLoad: {FormatPointer(lifecycle.FinishLoadAddress)}");
        sb.AppendLine($"  FileDescriptor.Read: {FormatPointer(lifecycle.FileDescriptorReadAddress)}");
        sb.AppendLine($"  Texture.InitializeContents: {FormatPointer(lifecycle.TextureInitializeContentsAddress)}");
        sb.AppendLine($"  D3D11 CreateTexture2D: {FormatPointer(lifecycle.D3D11CreateTexture2DAddress)}");
        sb.AppendLine($"  D3D11 CreateShaderResourceView: {FormatPointer(lifecycle.D3D11CreateShaderResourceViewAddress)}");
        sb.AppendLine();

        var events = lifecycle.RecentEvents;
        sb.AppendLine($"=== Recent lifecycle events ({events.Count}; all retained events included, newest first) ===");
        if (events.Count == 0)
        {
            sb.AppendLine("(no retained events)");
        }
        else
        {
            for (int i = 0; i < events.Count; i++)
            {
                var item = events[i];
                sb.AppendLine();
                sb.AppendLine($"--- Event #{item.Sequence}: {item.Operation} ---");
                sb.AppendLine($"Timestamp UTC: {item.TimestampUtc:O}");
                sb.AppendLine($"Duration: {item.ElapsedMilliseconds:F3} ms");
                sb.AppendLine($"Resource filename: {item.ResourceFileName}");
                sb.AppendLine($"Handle: {FormatPointer(item.HandleAddress)} · resource ID {item.ResourceId}");
                sb.AppendLine($"Thread: {item.ThreadId} · same as UI draw thread: {(uiThreadId == 0 ? "unknown" : (item.ThreadId == uiThreadId).ToString())}");
                sb.AppendLine($"Result: {item.Result} (0x{item.Result:X2}) · input flag: {item.InputFlag} (0x{item.InputFlag:X2}) · secondary input: {item.SecondaryInput}");
                sb.AppendLine($"File mode: {item.FileModeName} [{item.FileModeValue}] -> {item.ExitFileModeName} [{(item.ExitFileModeValue == byte.MaxValue ? "n/a" : item.ExitFileModeValue.ToString())}]");
                sb.AppendLine($"File offset: 0x{item.FileOffset:X} -> 0x{item.ExitFileOffset:X}");
                sb.AppendLine($"Requested length: {item.RequestedLength:N0} -> {item.ExitRequestedLength:N0}");
                sb.AppendLine($"FileDescriptor: {FormatPointer(item.FileDescriptorAddress)}");
                sb.AppendLine($"FileBuffer: {FormatPointer(item.FileBufferAddress)} -> {FormatPointer(item.ExitFileBufferAddress)}");

                if (item.Operation == "FileJob")
                    sb.AppendLine($"FileJob: priority {item.Priority} · synchronous {item.InputFlag != 0}");
                if (item.Operation == "FinishLoad")
                    sb.AppendLine($"FinishLoad arguments: open-file result {item.InputFlag} · secondary argument {item.SecondaryInput}");

                AppendStateSnapshot(sb, "State before", item.StateBefore);
                AppendStateSnapshot(sb, "State after", item.StateAfter);

                if (item.Operation == "FileDescriptor.Read")
                {
                    sb.AppendLine($"Read output buffer: {FormatPointer(item.IoOutputBufferAddress)}");
                    sb.AppendLine($"Read length: {item.IoLength:N0} bytes · file offset 0x{item.IoStart:X} · reset position {item.IoResetPosition != 0}");
                    if (item.TextureHeaderLayout is { } headerLayout)
                    {
                        sb.AppendLine($"Output matches TextureResourceHandle.Header: {item.OutputMatchesTextureHeader} · observed output offset +0x{(nuint)item.OutputOffsetFromHandle:X} · field offset +0x{(nuint)headerLayout.HeaderOffsetFromHandle:X} · size {headerLayout.HeaderSize}");
                        sb.AppendLine($"TEX LOD offsets: {headerLayout.LodOffsets}");
                        sb.AppendLine($"TEX offsets-to-surfaces: {headerLayout.OffsetsToSurfaces}");
                        sb.AppendLine($"Raw 80-byte TEX header: {headerLayout.RawHeaderHex}");
                    }
                }

                if (item.Operation == "Load")
                {
                    sb.AppendLine($"Load contents: {FormatPointer(item.LoadContentsAddress)} · matches descriptor {item.LoadContentsMatchesDescriptor}");
                    sb.AppendLine($"Load descriptor mode: {(item.LoadDescriptorFileModeValue == byte.MaxValue ? "n/a" : item.LoadDescriptorFileModeValue.ToString())}");
                    sb.AppendLine($"Load descriptor FileBuffer: {FormatPointer(item.LoadDescriptorFileBufferAddress)}");
                    sb.AppendLine($"TextureWhileLoading: {FormatPointer(item.TextureWhileLoadingAddress)}");
                    sb.AppendLine($"DataWhileLoading: {FormatPointer(item.DataWhileLoadingAddress)}");
                }

                if (item.Operation is "Load" or "FinishLoad" or "FileDescriptor.Read")
                {
                    if (item.Width != 0 || item.Height != 0)
                    {
                        sb.AppendLine($"Texture header: {item.Width} x {item.Height} x {item.Depth} · {item.Format} · mip/flag {item.MipCountAndFlag}");
                        sb.AppendLine($"Texture pointer: {FormatPointer(item.TextureAddress)}");
                    }
                }

                if (item.TextureInitialization is { } initialization)
                {
                    sb.AppendLine($"Texture.InitializeContents object: {FormatPointer(initialization.TextureObjectAddress)}");
                    sb.AppendLine($"InitializeContents contents: {FormatPointer(initialization.ContentsAddress)}");
                    sb.AppendLine($"Candidate payload buffer: {FormatPointer(initialization.PayloadBufferAddress)} · length {initialization.PayloadLength:N0} · file offset 0x{initialization.PayloadOffset:X}");
                    sb.AppendLine($"Contents matches captured payload buffer: {initialization.ContentsMatchesPayloadBuffer}");
                    sb.AppendLine($"Contents pointer delta from payload buffer: {(initialization.PayloadBufferAddress == 0 ? "n/a" : FormatSignedOffset(initialization.ContentsOffsetFromPayloadBuffer))}");
                    sb.AppendLine($"Texture object matches active TextureResourceHandle: {initialization.TextureMatchesActiveHandle}");
                    if (item.TextureHeaderLayout is { } headerBefore)
                    {
                        sb.AppendLine($"Header at InitializeContents entry: LOD [{headerBefore.LodOffsets}] · surfaces [{headerBefore.OffsetsToSurfaces}]");
                        sb.AppendLine($"Raw header at InitializeContents entry: {headerBefore.RawHeaderHex}");
                    }
                    if (item.TextureHeaderLayoutAfter is { } headerAfter)
                    {
                        sb.AppendLine($"Header after InitializeContents: LOD [{headerAfter.LodOffsets}] · surfaces [{headerAfter.OffsetsToSurfaces}]");
                        sb.AppendLine($"Raw header after InitializeContents: {headerAfter.RawHeaderHex}");
                    }
                    AppendTextureObjectSnapshot(sb, "Texture before InitializeContents", initialization.Before);
                    AppendTextureObjectSnapshot(sb, "Texture after InitializeContents", initialization.After);
                    var relationship = initialization.ResourceRelationship;
                    sb.AppendLine($"Native D3D11 association audit inspected: {relationship.Inspected}");
                    sb.AppendLine($"  Audit status: {relationship.Status}");
                    sb.AppendLine($"  Persistent texture matches CreateTexture2D output: {relationship.TexturePointerMatchesCreateResult}");
                    sb.AppendLine($"  Persistent SRV matches CreateShaderResourceView output: {relationship.ViewPointerMatchesCreateResult}");
                    sb.AppendLine($"  CreateShaderResourceView resource argument matches Texture.D3D11Texture2D pointer: {relationship.ViewCreateResourcePointerMatchesTexture}");
                    sb.AppendLine($"  SRV GetResource succeeded: {relationship.ViewGetResourceSucceeded} · returned resource {FormatPointer(relationship.ViewGetResourceAddress)}");
                    sb.AppendLine($"  SRV resource COM identity check: {relationship.ViewResourceIdentityCheckSucceeded} · same texture object: {relationship.ViewResourceIdentityMatchesTexture}");
                    sb.AppendLine($"  Texture GetDevice: {relationship.TextureGetDeviceSucceeded} · device {FormatPointer(relationship.TextureDeviceAddress)}");
                    sb.AppendLine($"  SRV GetDevice: {relationship.ViewGetDeviceSucceeded} · device {FormatPointer(relationship.ViewDeviceAddress)}");
                    sb.AppendLine($"  Texture/SRV device identity check: {relationship.TextureViewDeviceIdentityCheckSucceeded} · same device: {relationship.TextureAndViewShareDevice}");
                    sb.AppendLine($"  Texture/CreateTexture2D device identity check: {relationship.CreateTextureDeviceIdentityCheckSucceeded} · same device: {relationship.TextureSharesCreateTextureDevice}");
                    sb.AppendLine($"  SRV/CreateShaderResourceView device identity check: {relationship.CreateViewDeviceIdentityCheckSucceeded} · same device: {relationship.ViewSharesCreateViewDevice}");
                    sb.AppendLine($"Nested D3D11 CreateTexture2D calls: {initialization.D3D11TextureCreateCalls.Length}");
                    for (int createIndex = 0; createIndex < initialization.D3D11TextureCreateCalls.Length; createIndex++)
                    {
                        var create = initialization.D3D11TextureCreateCalls[createIndex];
                        sb.AppendLine($"  CreateTexture2D #{createIndex + 1}: HRESULT 0x{create.HResult:X8} · device {FormatPointer(create.DeviceAddress)} · created texture {FormatPointer(create.CreatedTextureAddress)} · matches Texture object after init {create.CreatedTextureAddress != 0 && create.CreatedTextureAddress == initialization.After.D3D11TextureAddress}");
                        sb.AppendLine($"    Desc {create.Width}x{create.Height} · mips {create.MipLevels} · array {create.ArraySize} · format {DescribeDxgiFormatForReport(create.Format)} · usage {DescribeD3D11UsageForReport(create.Usage)} ({create.Usage}) · bind 0x{create.BindFlags:X} · CPU 0x{create.CPUAccessFlags:X} · misc 0x{create.MiscFlags:X} · samples {create.SampleCount}/{create.SampleQuality}");
                        sb.AppendLine($"    InitialData array {FormatPointer(create.InitialDataArrayAddress)} · null {create.InitialDataWasNull} · payload {FormatPointer(create.PayloadBufferAddress)} / {create.PayloadLength:N0} bytes · subresource metadata captured {create.SubresourceMetadataCaptured}");
                        foreach (var subresource in create.Subresources)
                        {
                            string payloadOffsetText = create.PayloadBufferAddress == 0 ? "n/a" : FormatSignedOffset(subresource.OffsetFromPayloadBuffer);
                            string fileOffsetText = subresource.WithinPayloadBuffer && subresource.OffsetFromPayloadBuffer >= 0
                                ? $" · candidate file offset 0x{(ulong)initialization.PayloadOffset + (ulong)subresource.OffsetFromPayloadBuffer:X}"
                                : string.Empty;
                            string expectedBc7Text = subresource.ExpectedBc7SliceBytes == 0
                                ? "BC7 size n/a"
                                : $"BC7 expected row/slice {subresource.ExpectedBc7RowPitch:N0}/{subresource.ExpectedBc7SliceBytes:N0}";
                            sb.AppendLine($"    Subresource {subresource.Index} (array {subresource.ArraySlice}, mip {subresource.MipIndex}, {subresource.Width}x{subresource.Height}): SysMem {FormatPointer(subresource.SystemMemoryAddress)} · payload delta {payloadOffsetText}{fileOffsetText} · within payload {subresource.WithinPayloadBuffer} · row/slice pitch {subresource.SystemMemoryPitch:N0}/{subresource.SystemMemorySlicePitch:N0} · {expectedBc7Text}");
                        }
                    }

                    sb.AppendLine($"Nested D3D11 CreateShaderResourceView calls: {initialization.D3D11ShaderResourceViewCalls.Length}");
                    for (int viewIndex = 0; viewIndex < initialization.D3D11ShaderResourceViewCalls.Length; viewIndex++)
                    {
                        var view = initialization.D3D11ShaderResourceViewCalls[viewIndex];
                        sb.AppendLine($"  CreateShaderResourceView #{viewIndex + 1}: HRESULT 0x{view.HResult:X8} · device {FormatPointer(view.DeviceAddress)} · resource {FormatPointer(view.ResourceAddress)} · created SRV {FormatPointer(view.CreatedViewAddress)}");
                        sb.AppendLine($"    Input desc null: {view.InputDescriptionWasNull} · actual desc captured: {view.ActualDescriptionCaptured}");
                        if (!view.InputDescriptionWasNull)
                            AppendShaderResourceViewDescription(sb, "Input descriptor", view.InputDescription);
                        if (view.ActualDescriptionCaptured)
                            AppendShaderResourceViewDescription(sb, "Actual descriptor", view.ActualDescription);
                    }
                }

                sb.AppendLine($"Resource file sizes: {item.FileSize:N0} / {item.FileSize2:N0} / {item.FileSize3:N0}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== Notes ===");
        sb.AppendLine("The selected game path and original SqPack metadata describe the logical/base asset; native Resource filename values describe the file path observed during the actual load and may identify a Penumbra replacement.");
        sb.AppendLine("The manually acquired native handle snapshot is a separate probe reference; its handle address need not match the handles observed in lifecycle events.");
        sb.AppendLine("Pointer values are numeric observations only, except for documented header/texture descriptor queries; payload buffer memory is never dereferenced or modified by this probe.");
        sb.AppendLine("D3D11 texture descriptions and association checks use read-only COM queries. Temporary references returned by QueryInterface, SRV.GetResource, and DeviceChild.GetDevice are released immediately; the game's original texture/SRV references are not changed.");
        sb.AppendLine("The association audit stores only copied numeric addresses and boolean results; temporary COM references have already been released when diagnostics are copied. It verifies COM identity between the persistent Texture resource, its SRV backing resource, and their D3D11 devices. It does not determine the game's ownership/refcount policy or establish that replacing either pointer would be safe.");
        sb.AppendLine("The candidate payload-buffer address is held thread-locally only during its enclosing FileJob call to correlate a nested Texture.InitializeContents call.");
        sb.AppendLine("ChangeLod is reported as a raw byte only; its semantics have not been established.");
        sb.AppendLine("Event timings are individual nested native-call durations and must not be summed as independent wall-clock costs.");
        return sb.ToString();
    }

    private static void AppendShaderResourceViewDescription(StringBuilder sb, string label, NativeD3D11ShaderResourceViewDescriptionSnapshot description)
    {
        string dimension = DescribeShaderResourceViewDimension(description.ViewDimension);
        sb.AppendLine($"    {label}: format {DescribeDxgiFormatForReport(description.Format)} · dimension {dimension} ({description.ViewDimension})");
        if (description.ViewDimension is 4 or 9) // TEXTURE2D / TEXTURECUBE
            sb.AppendLine($"      MostDetailedMip {description.MipMostDetailedOrFirstElement} · MipLevels {description.MipLevelsOrNumElements}");
        else if (description.ViewDimension is 5 or 10) // TEXTURE2DARRAY / TEXTURECUBEARRAY
            sb.AppendLine($"      MostDetailedMip {description.MipMostDetailedOrFirstElement} · MipLevels {description.MipLevelsOrNumElements} · FirstArraySlice {description.FirstArraySliceOrFlags} · ArraySize {description.ArraySizeOrNumElements}");
        else
            sb.AppendLine($"      Union values {description.MipMostDetailedOrFirstElement}, {description.MipLevelsOrNumElements}, {description.FirstArraySliceOrFlags}, {description.ArraySizeOrNumElements}");
    }

    private static string DescribeShaderResourceViewDimension(uint dimension) => dimension switch
    {
        0 => "UNKNOWN",
        1 => "BUFFER",
        2 => "TEXTURE1D",
        3 => "TEXTURE1DARRAY",
        4 => "TEXTURE2D",
        5 => "TEXTURE2DARRAY",
        6 => "TEXTURE2DMS",
        7 => "TEXTURE2DMSARRAY",
        8 => "TEXTURE3D",
        9 => "TEXTURECUBE",
        10 => "TEXTURECUBEARRAY",
        11 => "BUFFEREX",
        _ => "UNKNOWN",
    };

    private static string DescribeDxgiFormatForReport(uint format) => format switch
    {
        98 => "BC7_UNORM (DXGI_FORMAT 98)",
        99 => "BC7_UNORM_SRGB (DXGI_FORMAT 99)",
        _ => $"DXGI_FORMAT {format}",
    };

    private static string DescribeD3D11UsageForReport(uint usage) => usage switch
    {
        0 => "Default",
        1 => "Immutable",
        2 => "Dynamic",
        3 => "Staging",
        _ => "Unknown",
    };

    private static void AppendTextureObjectSnapshot(StringBuilder sb, string label, NativeTextureObjectSnapshot texture)
    {
        sb.AppendLine($"{label}: object {FormatPointer(texture.TextureAddress)} · {texture.ActualWidth} x {texture.ActualHeight} · allocated {texture.AllocatedWidth} x {texture.AllocatedHeight} · depth {texture.Depth} · mips {texture.MipLevel} · array {texture.ArraySize}");
        sb.AppendLine($"  Format {texture.Format} · flags {texture.Flags} · D3D11 texture {FormatPointer(texture.D3D11TextureAddress)} · SRV {FormatPointer(texture.D3D11ShaderResourceViewAddress)}");
        sb.AppendLine($"  D3D11 texture description: {texture.D3D11Description}");
    }

    private static void AppendStateSnapshot(StringBuilder sb, string label, NativeTextureStateSnapshot state)
    {
        sb.AppendLine($"{label}: Blob {FormatPointer(state.BlobAddress)} · GetData {FormatPointer(state.DataAddress)} / {state.DataLength:N0} bytes · file sizes {state.FileSize:N0}/{state.FileSize2:N0}/{state.FileSize3:N0}");
        sb.AppendLine($"  FileType 0x{state.FileType:X8} · last I/O {state.LastIoResult} · ReadState {state.ReadState} · LoadState {state.LoadState} · OtherState {state.OtherState} · ChangeLod 0x{state.ChangeLodRaw:X2} · Texture {FormatPointer(state.TextureAddress)}");
    }

    private static string FormatPointer(nint address) => $"0x{(nuint)address:X}";

    private static string FormatSignedOffset(nint offset) => offset < 0
        ? $"-0x{(nuint)(-offset):X}"
        : $"+0x{(nuint)offset:X}";

    private void DrawPenumbraTextureSourceProbe(TextureFileReport report)
    {
        var probe = _plugin.PenumbraTextureSource;
        bool manualFilePath = probe.Result?.IsManualFilePath == true;
        ImGui.TextDisabled(manualFilePath ? "Selected local .tex file" : "Penumbra-resolved replacement source (local player collection)");
        ImGui.TextWrapped(manualFilePath
            ? "This is the exact absolute filesystem path supplied in the texture input field. FPSBooster reads its file metadata and small TEX/container header directly; it does not call Penumbra IPC or infer a game-relative path from the mod directory."
            : "Resolve asks Penumbra's public ResolvePlayerPath IPC which file currently supplies this game path for the local player's active collection. It does not change mod settings. If the returned path is a normal disk file, FPSBooster reads only its file metadata, bounded Type-4 mip descriptors and shared stride table (when present), and the small TexHeader; no mip payload is loaded or decompressed. This resolver is player-collection scoped, not a universal resolver for every NPC or environment resource. A blank result is not treated as proof that no replacement exists.");

        if (_gpuSequenceActive)
            ImGui.BeginDisabled();
        if (manualFilePath)
        {
            if (ImGui.Button("Reinspect selected file", new Vector2(190f, 0f)))
            {
                StopProgressiveSequence("Stopped because the selected local file is being refreshed.", clearResults: true);
                _plugin.TextureGpuUpload.Reset();
                _plugin.TextureStreaming.ClearMipReadResult();
                probe.InspectFilePath(probe.Result!.ResolvedPath);
            }
        }
        else if (ImGui.Button("Resolve effective source via Penumbra", new Vector2(250f, 0f)))
        {
            StopProgressiveSequence("Stopped because the effective Penumbra source is being refreshed.", clearResults: true);
            _plugin.TextureGpuUpload.Reset();
            _plugin.TextureStreaming.ClearMipReadResult();
            probe.Resolve(report.Path);
        }
        ImGui.SameLine();
        if (ImGui.Button(manualFilePath ? "Clear selected file" : "Clear resolved source", new Vector2(150f, 0f)))
        {
            StopProgressiveSequence("Stopped because the resolved source was cleared.", clearResults: true);
            _plugin.TextureGpuUpload.Reset();
            _plugin.TextureStreaming.ClearMipReadResult();
            probe.Clear();
        }
        if (_gpuSequenceActive)
            ImGui.EndDisabled();

        if (!string.IsNullOrEmpty(probe.Error))
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), probe.Error);
        else
            ImGui.TextDisabled(probe.Status);

        var result = probe.Result;
        if (result == null)
            return;

        ImGui.Columns(2, "PenumbraResolvedSourceSummary", false);
        ImGui.SetColumnWidth(0, 205f);
        if (!result.IsManualFilePath)
        {
            ImGui.Text("Game path"); ImGui.NextColumn(); ImGui.TextWrapped(result.GamePath); ImGui.NextColumn();
        }
        else
        {
            ImGui.Text("Input mode"); ImGui.NextColumn(); ImGui.Text("Absolute local file path"); ImGui.NextColumn();
        }
        ImGui.Text(result.IsManualFilePath ? "Selected path" : "Resolved path"); ImGui.NextColumn();
        if (result.IsResolved)
            ImGui.TextWrapped(result.ResolvedPath);
        else
            ImGui.TextDisabled("No path returned");
        ImGui.NextColumn();
        if (!result.IsManualFilePath)
        {
            ImGui.Text("IPC resolve time"); ImGui.NextColumn(); ImGui.Text($"{result.ResolveMilliseconds:F2} ms"); ImGui.NextColumn();
        }

        if (!string.IsNullOrEmpty(result.FileInspectionError))
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), result.FileInspectionError);

        if (result.IsResolved)
        {
            ImGui.Text("File exists on disk"); ImGui.NextColumn(); ImGui.Text(result.FileExists ? "Yes" : "No / not directly accessible"); ImGui.NextColumn();
            if (result.FileExists)
            {
                ImGui.Text("File length"); ImGui.NextColumn(); ImGui.Text(FormatBytes(result.FileLength)); ImGui.NextColumn();
                ImGui.Text("Last write (UTC)"); ImGui.NextColumn(); ImGui.Text(result.FileLastWriteUtc.ToString("u")); ImGui.NextColumn();
                ImGui.Text("Container"); ImGui.NextColumn(); ImGui.TextWrapped(result.ContainerDescription); ImGui.NextColumn();
                if (result.IsType4Container)
                {
                    ImGui.Text("Declared raw size"); ImGui.NextColumn(); ImGui.Text(FormatBytes(result.DeclaredRawFileSize)); ImGui.NextColumn();
                    ImGui.Text("Mip descriptors"); ImGui.NextColumn(); ImGui.Text(result.ContainerMipDescriptorCount.ToString()); ImGui.NextColumn();
                    ImGui.Text("Block stride table"); ImGui.NextColumn();
                    ImGui.Text($"{FormatBytes(result.BlockStrideTableBytes)} · {(result.BlockStrideTableRangeValid ? "in header" : "invalid range")} · {(result.BlockStrideTableValuesValid ? "sums match" : "invalid values")}"); ImGui.NextColumn();
                    if (!string.IsNullOrEmpty(result.BlockStrideTableValidationError))
                    {
                        ImGui.Text("Stride validation"); ImGui.NextColumn();
                        ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), result.BlockStrideTableValidationError); ImGui.NextColumn();
                    }
                }
                ImGui.Text("TexHeader offset"); ImGui.NextColumn(); ImGui.Text(result.HasValidTextureHeader ? $"0x{result.TextureHeaderOffset:X}" : "Not validated"); ImGui.NextColumn();
            }
        }
        ImGui.Columns(1);

        if (result.TextureReport is { } resolvedTexture)
        {
            ImGui.Separator();
            ImGui.TextDisabled(result.IsManualFilePath ? "Selected file texture metadata" : "Resolved source texture metadata");
            ImGui.Columns(2, "PenumbraTextureMetadata", false);
            ImGui.SetColumnWidth(0, 205f);
            ImGui.Text("Resolution"); ImGui.NextColumn(); ImGui.Text($"{resolvedTexture.Width} × {resolvedTexture.Height}"); ImGui.NextColumn();
            ImGui.Text("Mip levels"); ImGui.NextColumn(); ImGui.Text(resolvedTexture.MipCount.ToString()); ImGui.NextColumn();
            ImGui.Text("Format / type"); ImGui.NextColumn(); ImGui.Text($"{resolvedTexture.Format} / {resolvedTexture.Type}"); ImGui.NextColumn();
            ImGui.Text("Calculated mip payload"); ImGui.NextColumn(); ImGui.Text(FormatBytes(resolvedTexture.TotalMipBytes)); ImGui.NextColumn();
            ImGui.Columns(1);

            bool dimsMatchVanilla = resolvedTexture.Width == report.Width && resolvedTexture.Height == report.Height;
            if (result.IsManualFilePath)
                ImGui.TextDisabled("Dimensions and mip metadata above describe the selected file itself.");
            else
                ImGui.TextDisabled(dimsMatchVanilla
                    ? "Resolved source dimensions match the vanilla SqPack header."
                    : $"Resolved source dimensions differ from vanilla by {(double)resolvedTexture.Width / Math.Max(1, report.Width):F2}× width and {(double)resolvedTexture.Height / Math.Max(1, report.Height):F2}× height.");
            if (result.DeclaredRawSizeMatchesHeader.HasValue)
                ImGui.TextDisabled(result.DeclaredRawSizeMatchesHeader.Value
                    ? "Type-4 declared uncompressed size matches the 80-byte TexHeader plus calculated mip payload."
                    : "Type-4 declared uncompressed size does not match the calculated header + mip payload; do not assume its internal layout is standard.");
            if (!result.IsType4Container)
            {
                ImGui.TextDisabled($"Mip source ranges: {result.LooseMipValidCount} readable · {result.LooseMipAbsentCount} absent · {result.LooseMipInvalidCount} invalid. OffsetToSurface = 0 is treated as an absent mip, per Lumina's TexHeader definition.");
                if (!string.IsNullOrEmpty(result.LooseMipLayoutValidationError))
                    ImGui.TextColored(result.LooseMipInvalidCount > 0 ? new Vector4(1f, .75f, .35f, 1f) : Muted, result.LooseMipLayoutValidationError);

                ImGui.Separator();
                ImGui.TextDisabled(result.IsManualFilePath ? "Selective local loose-file mip read" : "Selective Penumbra loose-file mip read");
                ImGui.TextWrapped("Each present mip is read from its explicit absolute OffsetToSurface entry in the 80-byte TexHeader. Gaps, padding, trailing bytes, and absent mip entries are allowed. Only the selected mip's exact range is read; a zero offset means that mip has no payload in this source.");
                if (!string.IsNullOrEmpty(result.LooseMipOverlapWarning))
                    ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), result.LooseMipOverlapWarning);

                if (ImGui.TreeNode($"Per-mip source offsets ({result.LooseMipRanges.Count})"))
                {
                    ImGui.Columns(5, "PenumbraMipOffsets", false);
                    ImGui.Text("Mip"); ImGui.NextColumn();
                    ImGui.Text("Offset"); ImGui.NextColumn();
                    ImGui.Text("Payload"); ImGui.NextColumn();
                    ImGui.Text("Range"); ImGui.NextColumn();
                    ImGui.Text("Details"); ImGui.NextColumn();
                    ImGui.Separator();
                    foreach (var mip in resolvedTexture.Mips)
                    {
                        var range = result.LooseMipRanges.Find(x => x.MipIndex == mip.Index);
                        long start = mip.SourceFileOffset;
                        long end = range?.EndOffset ?? 0;
                        ImGui.Text(mip.Index.ToString()); ImGui.NextColumn();
                        ImGui.Text(start > 0 ? $"0x{start:X}" : "0 (none)"); ImGui.NextColumn();
                        ImGui.Text(FormatBytes(mip.Bytes)); ImGui.NextColumn();
                        string rangeLabel = range?.Status ?? "Unknown";
                        if (range?.IsReadable == true)
                            ImGui.TextColored(Green, $"OK · end 0x{end:X}");
                        else
                            ImGui.TextColored(range?.Status == "Absent" ? Muted : new Vector4(1f, .55f, .35f, 1f), rangeLabel);
                        ImGui.NextColumn();
                        ImGui.TextDisabled(range?.Detail ?? "No range metadata."); ImGui.NextColumn();
                    }
                    ImGui.Columns(1);
                    ImGui.TreePop();
                }

                if (_resolvedReadMipIndex < 0 || _resolvedReadMipIndex >= resolvedTexture.MipCount)
                    _resolvedReadMipIndex = resolvedTexture.MipCount - 1;
                ImGui.SetNextItemWidth(110f);
                ImGui.InputInt("Resolved mip index", ref _resolvedReadMipIndex);
                _resolvedReadMipIndex = Math.Clamp(_resolvedReadMipIndex, 0, resolvedTexture.MipCount - 1);
                var selectedRange = result.LooseMipRanges.Find(x => x.MipIndex == _resolvedReadMipIndex);
                ImGui.SameLine();
                bool resolvedIs2D = (((Lumina.Data.Files.TexFile.Attribute)resolvedTexture.TypeFlags & Lumina.Data.Files.TexFile.Attribute.TextureTypeMask)
                    == Lumina.Data.Files.TexFile.Attribute.TextureType2D);
                bool canReadResolvedMip = resolvedIs2D && selectedRange?.IsReadable == true
                    && !_gpuSequenceActive && !_plugin.TextureStreaming.IsReadingMip;
                if (!canReadResolvedMip) ImGui.BeginDisabled();
                if (ImGui.Button(result.IsManualFilePath ? "Read selected mip only" : "Read resolved mip only", new Vector2(190f, 0f)))
                {
                    try
                    {
                        _plugin.TextureStreaming.ReadLooseMip(
                            result.ResolvedPath, resolvedTexture, _resolvedReadMipIndex,
                            result.MipPayloadStartOffset, result.FileLength, result.FileLastWriteUtc);
                    }
                    catch (Exception ex)
                    {
                        _plugin.TextureGpuUpload.SetError(ex.Message);
                    }
                }
                if (!canReadResolvedMip) ImGui.EndDisabled();
                if (!resolvedIs2D)
                    ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), "Manual selective GPU testing currently supports ordinary 2D textures only.");
                else if (selectedRange?.IsReadable != true)
                    ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), selectedRange?.Detail ?? "No source range was recorded for this mip.");

                var streaming = _plugin.TextureStreaming;
                if (ReferenceEquals(streaming.MipReadSourceReport, resolvedTexture))
                {
                    if (streaming.IsReadingMip)
                        ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), result.IsManualFilePath
                            ? $"Reading selected loose-file Mip {_resolvedReadMipIndex}..."
                            : $"Reading Penumbra loose-file Mip {_resolvedReadMipIndex}...");
                    if (!string.IsNullOrEmpty(streaming.MipReadError))
                        ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), streaming.MipReadError);
                    if (streaming.MipRead is { } looseMip)
                        DrawMipReadResult(looseMip, isLooseFile: true);
                }

                if (result.HasValidLooseMipRanges)
                    ImGui.TextDisabled(result.IsManualFilePath
                        ? "Every declared mip has a nonzero, in-bounds, non-overlapping range. The full progressive sequence can use this local file."
                        : "Every declared mip has a nonzero, in-bounds, non-overlapping range. The full progressive sequence can use this replacement.");
                else
                    ImGui.TextDisabled(result.IsManualFilePath
                        ? "The selected file has a partial or invalid mip table. Individually valid mips can still be read and uploaded manually; full-chain upload remains disabled."
                        : "The replacement has a partial or invalid mip table. Individually valid mips can still be read and uploaded manually; the full-chain sequence remains disabled until every mip is available.");
            }

            if (result.Groups.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextDisabled("Penumbra source Type-4 mip descriptors (metadata only)");
                ImGui.Columns(7, "PenumbraMipGroups", false);
                ImGui.SetColumnWidth(0, 45f);
                ImGui.SetColumnWidth(1, 75f);
                ImGui.SetColumnWidth(2, 75f);
                ImGui.SetColumnWidth(3, 75f);
                ImGui.SetColumnWidth(4, 65f);
                ImGui.SetColumnWidth(5, 55f);
                ImGui.Text("Group"); ImGui.NextColumn();
                ImGui.Text("Mip match"); ImGui.NextColumn();
                ImGui.Text("Stored"); ImGui.NextColumn();
                ImGui.Text("Raw"); ImGui.NextColumn();
                ImGui.Text("Block off."); ImGui.NextColumn();
                ImGui.Text("Blocks"); ImGui.NextColumn();
                ImGui.Text("Range"); ImGui.NextColumn();
                foreach (var group in result.Groups)
                {
                    ImGui.Text(group.Index.ToString()); ImGui.NextColumn();
                    ImGui.Text(group.MatchedMipIndex.HasValue ? $"Mip {group.MatchedMipIndex.Value}" : "—"); ImGui.NextColumn();
                    ImGui.Text(FormatBytes(group.CompressedSize)); ImGui.NextColumn();
                    ImGui.Text(FormatBytes(group.DecompressedSize)); ImGui.NextColumn();
                    ImGui.Text($"0x{group.BlockOffset:X}"); ImGui.NextColumn();
                    ImGui.Text(group.BlockCount.ToString()); ImGui.NextColumn();
                    if (group.IsDataRangeValid) ImGui.Text("OK");
                    else ImGui.TextColored(new Vector4(1f, .35f, .3f, 1f), "Invalid");
                    ImGui.NextColumn();
                }
                ImGui.Columns(1);
                ImGui.TextWrapped(result.MipMappingNote ?? "Type-4 mip mapping has not been validated.");
                if (result.HasStrictMipGroupMapping)
                    ImGui.TextColored(Green, "Type-4 group sizes and ranges match the texture mip chain. Selective source-file mip reads can be tested next.");
            }
        }
    }

    private static void DrawSqPackNativeComparison(TextureFileReport report, NativeTextureHandleSnapshot snapshot)
    {
        uint nativeWidth = snapshot.ActualWidth > 0 ? snapshot.ActualWidth : snapshot.HeaderWidth;
        uint nativeHeight = snapshot.ActualHeight > 0 ? snapshot.ActualHeight : snapshot.HeaderHeight;
        int nativeMipCount = snapshot.TextureMipLevelRaw > 0
            ? snapshot.TextureMipLevelRaw
            : snapshot.HeaderMipCountAndFlag;
        string nativeFormat = !string.IsNullOrWhiteSpace(snapshot.TextureFormat)
            ? snapshot.TextureFormat
            : snapshot.HeaderFormat;

        bool isBc7 = nativeFormat.Contains("BC7", StringComparison.OrdinalIgnoreCase);
        bool is2D = snapshot.TextureFlags.Contains("TextureType2D", StringComparison.OrdinalIgnoreCase)
            || snapshot.HeaderType.Contains("TextureType2D", StringComparison.OrdinalIgnoreCase);
        long? nativeBc7Bytes = isBc7 && is2D
            ? EstimateBc7MipPayload(nativeWidth, nativeHeight, nativeMipCount, snapshot.ArraySize)
            : null;

        bool dimensionsDiffer = nativeWidth != report.Width || nativeHeight != report.Height;
        bool mipCountDiffers = nativeMipCount > 0 && nativeMipCount != report.MipCount;
        bool formatDiffers = !FormatsEquivalent(report.Format, nativeFormat);
        bool differs = dimensionsDiffer || mipCountDiffers || formatDiffers;

        ImGui.Separator();
        ImGui.TextDisabled("Vanilla SqPack vs effective native texture");
        ImGui.Columns(3, "SqPackNativeTextureComparison", false);
        ImGui.SetColumnWidth(0, 130f);
        ImGui.SetColumnWidth(1, 175f);
        ImGui.Text("Property"); ImGui.NextColumn();
        ImGui.Text("Vanilla SqPack"); ImGui.NextColumn();
        ImGui.Text("Effective native"); ImGui.NextColumn();

        ImGui.Text("Resolution"); ImGui.NextColumn();
        ImGui.Text($"{report.Width} × {report.Height}"); ImGui.NextColumn();
        ImGui.Text($"{nativeWidth} × {nativeHeight}"); ImGui.NextColumn();

        ImGui.Text("Mip levels"); ImGui.NextColumn();
        ImGui.Text(report.MipCount.ToString()); ImGui.NextColumn();
        ImGui.Text(nativeMipCount > 0 ? nativeMipCount.ToString() : "Unavailable"); ImGui.NextColumn();

        ImGui.Text("Format"); ImGui.NextColumn();
        ImGui.Text(report.Format); ImGui.NextColumn();
        ImGui.Text(nativeFormat); ImGui.NextColumn();

        ImGui.Text("Mip payload"); ImGui.NextColumn();
        ImGui.Text(FormatBytes(report.TotalMipBytes)); ImGui.NextColumn();
        ImGui.Text(nativeBc7Bytes.HasValue ? $"~{FormatBytes(nativeBc7Bytes.Value)}" : "Estimate unavailable"); ImGui.NextColumn();
        ImGui.Columns(1);

        if (dimensionsDiffer && report.Width > 0 && report.Height > 0)
        {
            double widthScale = (double)nativeWidth / report.Width;
            double heightScale = (double)nativeHeight / report.Height;
            double texelScale = widthScale * heightScale;
            ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f),
                $"Effective native dimensions are {widthScale:F2}× wider and {heightScale:F2}× taller ({texelScale:F2}× the texel count).");
        }

        if (nativeBc7Bytes.HasValue && report.TotalMipBytes > 0)
        {
            double payloadScale = (double)nativeBc7Bytes.Value / report.TotalMipBytes;
            ImGui.TextDisabled($"Tight BC7 block-payload estimate: {payloadScale:F2}× the vanilla SqPack mip payload. This is a format-size estimate, not measured GPU allocation or residency.");
        }

        if (differs)
        {
            ImGui.TextWrapped("The archive inspector reads the vanilla SqPack entry. ResourceHandle.GetAsync reports the effective texture resolved by the running game after resource-redirection hooks, including Penumbra in this setup. These values describe two different sources; the mismatch is expected for a higher-resolution replacement, not evidence of a SqPack parser error.");
        }
        else
        {
            ImGui.TextWrapped("The native texture metadata currently matches the vanilla SqPack dimensions, mip count, and format. This does not by itself prove that the underlying file bytes are identical.");
        }
    }

    private static bool FormatsEquivalent(string vanillaFormat, string nativeFormat)
    {
        if (string.Equals(vanillaFormat, nativeFormat, StringComparison.OrdinalIgnoreCase))
            return true;

        // Lumina and FFXIVClientStructs use slightly different labels for the same BC formats.
        return vanillaFormat.Contains("BC7", StringComparison.OrdinalIgnoreCase)
            && nativeFormat.Contains("BC7", StringComparison.OrdinalIgnoreCase);
    }

    private static long? EstimateBc7MipPayload(uint width, uint height, int mipCount, byte arraySize)
    {
        if (width == 0 || height == 0 || mipCount <= 0 || mipCount > 16)
            return null;

        long slices = Math.Max(1, (int)arraySize);
        long total = 0;
        long mipWidth = width;
        long mipHeight = height;

        for (int mip = 0; mip < mipCount; mip++)
        {
            long blocksWide = Math.Max(1L, (mipWidth + 3L) / 4L);
            long blocksHigh = Math.Max(1L, (mipHeight + 3L) / 4L);
            total = checked(total + blocksWide * blocksHigh * 16L * slices);
            mipWidth = Math.Max(1L, mipWidth / 2L);
            mipHeight = Math.Max(1L, mipHeight / 2L);
        }

        return total;
    }

    private void DrawGpuUploadProbe(TextureFileReport report, TextureMipReadReport? mipRead, string? looseSourcePath, long loosePayloadOffset, long looseFileLength, DateTime looseLastWriteUtc, bool looseMipChainComplete)
    {
        var probe = _plugin.TextureGpuUpload;
        bool fromLooseSource = !string.IsNullOrEmpty(looseSourcePath);
        bool manualFileSource = _plugin.PenumbraTextureSource.Result?.IsManualFilePath == true;
        string sourceName = manualFileSource
            ? "user-selected local .tex file"
            : fromLooseSource ? "Penumbra-resolved loose replacement file" : "vanilla SqPack archive";
        ImGui.TextDisabled("Plugin-owned GPU upload experiment");
        ImGui.TextWrapped($"This creates a separate BC7 D3D11 texture owned by FPSBooster. The selected source is the {sourceName}. It does not access or modify the game's existing texture resources.");

        if (!_gpuSequenceActive)
        {
            if (!probe.IsCreated)
            {
                if (ImGui.Button("Create empty GPU test texture", new Vector2(220f, 0f)))
                {
                    try { probe.Create(report); }
                    catch (Exception ex) { probe.SetError(ex.Message); }
                }
            }
            else
            {
                if (ImGui.Button("Reset GPU test texture", new Vector2(180f, 0f)))
                    probe.Reset();
                ImGui.SameLine();

                bool hasMipData = mipRead != null && mipRead.Data.Length > 0
                    && ReferenceEquals(_plugin.TextureStreaming.MipReadSourceReport, report)
                    && !_plugin.TextureStreaming.IsReadingMip;
                if (!hasMipData)
                    ImGui.BeginDisabled();
                if (ImGui.Button("Upload last read mip", new Vector2(170f, 0f)) && mipRead != null)
                {
                    try { probe.Upload(report, mipRead); }
                    catch (Exception ex) { probe.SetError(ex.Message); }
                }
                if (!hasMipData)
                    ImGui.EndDisabled();
            }

            ImGui.Separator();
            ImGui.TextDisabled("Upload scheduling mode (all modes use the same two-mip prefetch queue)");
            int schedulingModeValue = (int)_gpuSequenceSchedulingMode;
            ImGui.RadioButton("One mip / frame", ref schedulingModeValue, (int)GpuUploadSchedulingMode.OneMipPerFrame);
            ImGui.SameLine();
            ImGui.RadioButton("Elapsed-time budget", ref schedulingModeValue, (int)GpuUploadSchedulingMode.ElapsedTimeBudget);
            ImGui.SameLine();
            ImGui.RadioButton("Predictive budget", ref schedulingModeValue, (int)GpuUploadSchedulingMode.PredictiveBudget);
            ImGui.RadioButton("Elapsed + isolate largest mip", ref schedulingModeValue, (int)GpuUploadSchedulingMode.ElapsedBudgetWithLargeMipIsolation);
            _gpuSequenceSchedulingMode = (GpuUploadSchedulingMode)Math.Clamp(schedulingModeValue, 0, 3);

            if (_gpuSequenceSchedulingMode != GpuUploadSchedulingMode.OneMipPerFrame)
            {
                ImGui.SetNextItemWidth(250f);
                ImGui.SliderFloat("CPU upload budget per UI frame", ref _gpuSequenceCpuUploadBudgetMilliseconds, 0.25f, 4.0f, "%.2f ms");
                if (_gpuSequenceSchedulingMode == GpuUploadSchedulingMode.ElapsedTimeBudget)
                    ImGui.TextDisabled("Elapsed-time mode submits at least one ready mip, then continues while the UI-thread upload phase remains under budget. It favors throughput and may overshoot by the final indivisible mip.");
                else if (_gpuSequenceSchedulingMode == GpuUploadSchedulingMode.PredictiveBudget)
                    ImGui.TextDisabled("Predictive mode estimates the next mip's CPU submission cost and defers it when the estimate exceeds the remaining budget. It can avoid multi-mip overruns, but conservative estimates may add UI frames. A single large mip can exceed the budget by itself.");
                else
                    ImGui.TextDisabled("Hybrid mode uses elapsed-time batching, but starts the largest mip on a fresh UI frame when it would otherwise follow other uploads. The threshold is max(2 MiB, 50% of total mip payload). This isolates the dominant upload without deferring every large mip predictively.");
            }
            else
            {
                ImGui.TextDisabled("Baseline mode: one mip is submitted per UI frame. The same two-mip background prefetch queue is used so scheduling modes can be compared without changing read-ahead depth.");
            }
            if (_gpuSequenceVerifyReadback)
                ImGui.TextDisabled("GPU readback verification remains limited to one mip per UI frame because staging Map(Read) is synchronous and can stall the UI.");

            bool sourceLayoutValidated = fromLooseSource
                ? looseMipChainComplete && loosePayloadOffset == 80 && looseFileLength > loosePayloadOffset
                : report.SqPack?.HasStrictMipGroupMapping == true;
            bool isSupportedGpuTexture = report.Format.Equals("BC7", StringComparison.OrdinalIgnoreCase)
                && (((Lumina.Data.Files.TexFile.Attribute)report.TypeFlags & Lumina.Data.Files.TexFile.Attribute.TextureTypeMask)
                    == Lumina.Data.Files.TexFile.Attribute.TextureType2D);
            bool canStartSequence = sourceLayoutValidated && isSupportedGpuTexture
                && !_plugin.TextureStreaming.IsLoading && !_plugin.TextureStreaming.IsReadingMip;
            if (!canStartSequence)
                ImGui.BeginDisabled();
            if (ImGui.Button("Run coarse-to-fine upload only", new Vector2(250f, 0f)))
                StartProgressiveSequence(report, false, looseSourcePath, loosePayloadOffset, looseFileLength, looseLastWriteUtc);
            if (ImGui.Button("Run coarse-to-fine upload + verify", new Vector2(260f, 0f)))
                StartProgressiveSequence(report, true, looseSourcePath, loosePayloadOffset, looseFileLength, looseLastWriteUtc);
            if (!canStartSequence)
                ImGui.EndDisabled();
            ImGui.TextDisabled(fromLooseSource
                ? manualFileSource
                    ? "The background reader seeks each raw mip range in the selected local loose TEX file; no SqPack decompression is involved. Upload-only mode can submit multiple prefetched mips in one UI frame within the CPU submission budget."
                    : "The background reader seeks each raw mip range in the Penumbra-resolved loose file; no SqPack decompression is involved. Upload-only mode can submit multiple prefetched mips in one UI frame within the CPU submission budget."
                : "The background reader selectively reads and decompresses SqPack mip groups. Upload-only mode can submit multiple prefetched mips in one UI frame within the CPU submission budget; verify mode performs one synchronous staging readback per frame.");
            if (fromLooseSource && !looseMipChainComplete)
                ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), manualFileSource
                    ? "The selected file has absent, invalid, or overlapping mip ranges. Manual reads/uploads are allowed for valid mips, but the complete coarse-to-fine sequence is disabled."
                    : "The resolved replacement has absent, invalid, or overlapping mip ranges. Manual reads/uploads are allowed for valid mips, but the complete coarse-to-fine sequence is disabled.");
        }
        else
        {
            if (ImGui.Button("Stop progressive test", new Vector2(190f, 0f)))
                StopProgressiveSequence("Stopped by user. Any completed mip uploads remain in the test texture.", clearResults: false);
            ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), _gpuSequenceStatus);
        }

        if (!probe.IsCreated)
        {
            ImGui.TextWrapped(probe.Status);
            if (!string.IsNullOrEmpty(probe.Error))
                ImGui.TextColored(new Vector4(1f, .35f, .3f, 1f), probe.Error);
        }
        else
        {
            ImGui.TextWrapped(probe.Status);
            if (!string.IsNullOrEmpty(probe.Error))
                ImGui.TextColored(new Vector4(1f, .35f, .3f, 1f), probe.Error);

            var upload = probe.LastUpload;
            if (upload != null)
            {
                ImGui.Text($"Last upload: Mip {upload.MipIndex} · {FormatBytes(upload.Bytes)} · {upload.SubmissionMilliseconds:F3} ms CPU submission");
                ImGui.Text($"Uploaded mip levels: {probe.UploadedMipCount}/{report.MipCount}");

                if (!_gpuSequenceActive && ImGui.Button("Read back & verify last upload", new Vector2(220f, 0f)))
                {
                    try { probe.VerifyLastUpload(); }
                    catch (Exception ex) { probe.SetError("GPU readback verification failed: " + ex.Message); }
                }

                var readback = probe.LastReadback;
                if (readback != null)
                {
                    ImGui.TextColored(readback.Matches ? Green : new Vector4(1f, .35f, .3f, 1f), readback.Message);
                    ImGui.TextDisabled($"Mip {readback.MipIndex} · {FormatBytes(readback.ComparedBytes)} compared · mapped row pitch {FormatBytes(readback.MappedRowPitch)} · {readback.ElapsedMilliseconds:F3} ms copy/readback/compare");
                }
            }

            if (probe.IsPreviewReady)
            {
                ImGui.Separator();
                ImGui.TextDisabled($"GPU-sampled progressive preview · initialized Mip {probe.PreviewFirstMip} through Mip {probe.PreviewFirstMip + probe.PreviewMipCount - 1} ({probe.PreviewMipCount}/{report.MipCount} visible levels)");
                float previewScale = Math.Min(420f / report.Width, 240f / report.Height);
                previewScale = Math.Max(0.01f, previewScale);
                ImGui.Image(probe.PreviewTextureId, new Vector2(report.Width * previewScale, report.Height * previewScale));
                ImGui.TextDisabled("The preview becomes available after the first mip upload and refines as finer mips arrive. Its SRV exposes only a contiguous range of initialized levels. This is still a separate plugin-owned texture, not an FFXIV texture.");
                if (!string.IsNullOrEmpty(probe.PreviewError))
                    ImGui.TextColored(new Vector4(1f, .65f, .35f, 1f), "The preview could not expand to the newest mip range: " + probe.PreviewError);
            }
            else if (!string.IsNullOrEmpty(probe.PreviewError))
            {
                ImGui.TextColored(new Vector4(1f, .65f, .35f, 1f), "GPU preview view could not be created: " + probe.PreviewError);
            }
        }

        if (_gpuSequenceResults.Count > 0 || _gpuSequenceStatus != "Not run.")
        {
            ImGui.Separator();
            ImGui.TextDisabled("Coarse-to-fine sequence results");
            ImGui.TextWrapped(_gpuSequenceStatus);
            foreach (string result in _gpuSequenceResults)
                ImGui.TextColored(result.Contains("MISMATCH", StringComparison.OrdinalIgnoreCase)
                    ? new Vector4(1f, .35f, .3f, 1f)
                    : Green, result);
            if (!_gpuSequenceActive && _gpuSequenceResults.Count > 0)
            {
                string readbackSummary = _gpuSequenceVerifyReadback
                    ? $"{_gpuSequenceReadbackMilliseconds:F2} ms synchronous readback/compare"
                    : "staging readback skipped";
                string sourceReadLabel = string.IsNullOrEmpty(_gpuSequenceLooseFilePath) ? "read/decompress" : "direct source read";
                string schedulerSummary = _gpuSequenceVerifyReadback
                    ? "verification pacing: one mip per UI frame"
                    : _gpuSequenceActiveSchedulingMode switch
                    {
                        GpuUploadSchedulingMode.ElapsedTimeBudget => $"elapsed-time budget {_gpuSequenceActiveCpuUploadBudgetMilliseconds:F2} ms",
                        GpuUploadSchedulingMode.PredictiveBudget => $"predictive budget {_gpuSequenceActiveCpuUploadBudgetMilliseconds:F2} ms; learned estimate {_gpuSequenceEstimatedUploadMillisecondsPerMiB:F3} ms/MiB",
                        GpuUploadSchedulingMode.ElapsedBudgetWithLargeMipIsolation => $"elapsed budget {_gpuSequenceActiveCpuUploadBudgetMilliseconds:F2} ms + large-mip isolation at {FormatBytes(_gpuSequenceLargeMipIsolationThresholdBytes)}",
                        _ => "one mip per UI frame baseline",
                    };
                ImGui.TextDisabled($"Work totals · {FormatBytes(_gpuSequenceArchiveBytes)} source bytes requested · {FormatBytes(_gpuSequenceMipBytes)} mip payloads · {_gpuSequenceReadMilliseconds:F2} ms {sourceReadLabel} · {_gpuSequenceUploadMilliseconds:F3} ms CPU upload submission · {readbackSummary} · {schedulerSummary}");
                if (_gpuSequenceFirstPreviewMilliseconds >= 0d)
                    ImGui.TextDisabled($"Time to first preview SRV ready: {_gpuSequenceFirstPreviewMilliseconds:F2} ms ({_gpuSequenceFirstPreviewFrame} UI frames)");
                ImGui.TextDisabled($"End-to-end wall time: {_gpuSequenceWallMilliseconds:F2} ms ({_gpuSequenceFrameCount} UI frames). Includes test-texture allocation, background-read waits, and frame cadence; upload timings remain CPU submission only.");
                if (_gpuSequenceFrameBatchCounts.Count > 0)
                {
                    var frameSummaries = new List<string>(_gpuSequenceFrameBatchCounts.Count);
                    for (int i = 0; i < _gpuSequenceFrameBatchCounts.Count; i++)
                    {
                        double submittedMs = i < _gpuSequenceFrameUploadSubmissionMilliseconds.Count
                            ? _gpuSequenceFrameUploadSubmissionMilliseconds[i]
                            : 0d;
                        frameSummaries.Add($"{_gpuSequenceFrameBatchCounts[i]} ({submittedMs:F3} ms)");
                    }
                    ImGui.TextDisabled("Mip uploads per UI frame (count / summed CPU submission): " + string.Join(", ", frameSummaries));
                }
                if (_gpuSequenceActiveSchedulingMode != GpuUploadSchedulingMode.OneMipPerFrame && !_gpuSequenceVerifyReadback)
                    ImGui.TextDisabled($"Budget deferrals: {_gpuSequenceBudgetDeferralCount} · large-mip isolation deferrals: {_gpuSequenceLargeMipDeferralCount} · over-budget frames: {_gpuSequenceBudgetOverrunFrameCount} (single-mip: {_gpuSequenceSingleMipOverrunFrameCount}, multi-mip: {_gpuSequenceBatchOverrunFrameCount}) · max frame upload submission: {_gpuSequenceMaximumFrameUploadMilliseconds:F3} ms");
                if (!_gpuSequenceVerifyReadback)
                    ImGui.TextDisabled($"Prefetch empty polls: {_gpuSequencePrefetchEmptyPollCount} · upload-starved UI frames: {_gpuSequencePrefetchStarvedFrameCount} · bounded queued mips: {TextureMipPrefetchQueue.DefaultCapacity}");
            }
        }

        ImGui.TextWrapped($"Important: the test resource reserves the complete mip chain up front. Upload-only mode is the cleaner timing run. {(fromLooseSource ? "For a loose TEX source, each raw mip range is read directly without SqPack decompression." : "For vanilla assets, each SqPack group is read and decompressed independently.")} A single background reader uses a bounded pipeline: one pending mip held across a frame boundary, up to two queued mips, and one mip currently being read. Predictive frame-budget mode estimates the next mip CPU UpdateSubresource submission cost from payload size and recent uploads of at least 1 MiB; it defers a mip when its estimated cost would exceed the remaining budget. Individual mip uploads are indivisible, so a single large mip can exceed the budget when submitted alone. The budget measures CPU submission, not GPU execution/completion. The elapsed + large-mip-isolation mode uses the same elapsed-time batching but defers any mip at or above max(2 MiB, 50% of total mip payload) to a fresh frame when another mip has already been submitted that frame. The preview SRV is refreshed once after each upload batch, rather than once per mip. Verify mode remains one mip per frame because staging Map(Read) is synchronous and can stall the UI. Once a mip is uploaded, FPSBooster expands the preview SRV to include only the contiguous initialized range. This remains a plugin-owned test resource and does not demonstrate reduced VRAM residency or game texture streaming.");

        AdvanceProgressiveSequence(report, looseSourcePath, loosePayloadOffset, looseFileLength, looseLastWriteUtc);
    }

    private void StartProgressiveSequence(TextureFileReport report, bool verifyReadback, string? looseSourcePath, long loosePayloadOffset, long looseFileLength, DateTime looseLastWriteUtc)
    {
        var inspector = _plugin.TextureStreaming;
        var probe = _plugin.TextureGpuUpload;
        if (inspector.IsLoading || inspector.IsReadingMip)
        {
            probe.SetError("Wait for the current texture metadata or mip read to finish before starting the progressive test.");
            return;
        }

        try
        {
            if (report.MipCount <= 0)
                throw new InvalidOperationException("The inspected texture does not contain any mip levels.");
            bool fromLooseSource = !string.IsNullOrEmpty(looseSourcePath);
            if (fromLooseSource)
            {
                if (loosePayloadOffset != 80 || looseFileLength <= loosePayloadOffset)
                    throw new InvalidOperationException("The loose TEX source metadata is no longer valid. Reinspect the source file before reading mips.");
                ValidateCompleteLooseMipChain(report, looseFileLength);
            }
            else if (report.SqPack?.HasStrictMipGroupMapping != true)
            {
                throw new InvalidOperationException("The SqPack groups could not be mapped one-to-one to the mip chain. The progressive GPU sequence is disabled for this texture.");
            }

            StopProgressiveSequence("Preparing a fresh coarse-to-fine test.", clearResults: true);
            _gpuSequenceReport = report;
            _gpuSequenceLooseFilePath = looseSourcePath;
            _gpuSequenceLoosePayloadOffset = loosePayloadOffset;
            _gpuSequenceLooseFileLength = looseFileLength;
            _gpuSequenceLooseLastWriteUtc = looseLastWriteUtc;
            _gpuSequenceWallTimer.Restart();
            probe.Create(report);
            _gpuSequenceActiveSchedulingMode = _gpuSequenceSchedulingMode;
            _gpuSequenceActiveCpuUploadBudgetMilliseconds = _gpuSequenceCpuUploadBudgetMilliseconds;
            _gpuSequenceLargeMipIsolationThresholdBytes = Math.Max(2L * 1024L * 1024L, report.TotalMipBytes / 2L);
            _gpuSequenceLargeMipDeferralCount = 0;
            _gpuSequenceVerifyReadback = verifyReadback;
            _gpuSequencePendingMipRead = null;
            _gpuSequenceEstimatedUploadMillisecondsPerMiB = InitialEstimatedUploadMillisecondsPerMiB;
            _gpuSequenceCurrentMip = -1;
            _gpuSequenceNextMip = report.MipCount - 1;
            _gpuSequencePrefetchQueue = new TextureMipPrefetchQueue(
                report,
                looseSourcePath,
                loosePayloadOffset,
                looseFileLength,
                looseLastWriteUtc,
                TextureMipPrefetchQueue.DefaultCapacity);
            _gpuSequenceActive = true;

            string mode = verifyReadback ? "upload + readback verification" : "upload-only (no staging readback)";
            string schedule = verifyReadback
                ? "verification pacing (one mip per UI frame)"
                : _gpuSequenceActiveSchedulingMode switch
                {
                    GpuUploadSchedulingMode.ElapsedTimeBudget => $"elapsed-time budget ({_gpuSequenceActiveCpuUploadBudgetMilliseconds:F2} ms CPU UpdateSubresource budget)",
                    GpuUploadSchedulingMode.PredictiveBudget => $"predictive budget ({_gpuSequenceActiveCpuUploadBudgetMilliseconds:F2} ms CPU UpdateSubresource budget; starting estimate {InitialEstimatedUploadMillisecondsPerMiB:F2} ms/MiB)",
                    GpuUploadSchedulingMode.ElapsedBudgetWithLargeMipIsolation => $"elapsed budget ({_gpuSequenceActiveCpuUploadBudgetMilliseconds:F2} ms) with largest-mip isolation threshold {FormatBytes(_gpuSequenceLargeMipIsolationThresholdBytes)}",
                    _ => "one mip per UI frame baseline",
                };
            bool manualFileSource = _plugin.PenumbraTextureSource.Result?.IsManualFilePath == true;
            string sourceLabel = fromLooseSource
                ? (manualFileSource ? "selected local loose TEX file" : "Penumbra loose replacement")
                : "vanilla SqPack";
            _gpuSequenceStatus = $"Starting coarse-to-fine {mode} from {sourceLabel}: {report.MipCount} mip levels, Mip {_gpuSequenceNextMip} first; {schedule}. Background prefetch capacity: {TextureMipPrefetchQueue.DefaultCapacity}.";
        }
        catch (Exception ex)
        {
            StopProgressiveSequence("Could not start the progressive test.", clearResults: false);
            probe.SetError(ex.Message);
        }
    }

    private static void ValidateCompleteLooseMipChain(TextureFileReport report, long fileLength)
    {
        if (report.Mips.Count != report.MipCount || report.MipCount <= 0)
            throw new InvalidOperationException("The loose TEX mip metadata is incomplete.");

        var ranges = new List<(long Start, long End, int Mip)>();
        foreach (var mip in report.Mips)
        {
            long start = mip.SourceFileOffset;
            long end;
            try { end = checked(start + mip.Bytes); }
            catch (OverflowException) { throw new InvalidOperationException($"Mip {mip.Index} has an overflowing source range."); }

            if (start < 80 || mip.Bytes <= 0 || end > fileLength)
                throw new InvalidOperationException($"Mip {mip.Index} does not have a valid in-bounds source range.");
            ranges.Add((start, end, mip.Index));
        }

        ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < ranges.Count; i++)
        {
            if (ranges[i].Start < ranges[i - 1].End)
                throw new InvalidOperationException($"Mip {ranges[i - 1].Mip} and Mip {ranges[i].Mip} ranges overlap; the full-chain upload is disabled until that layout is understood.");
        }
    }

    /// <summary>
    /// Drains ready mip payloads on the UI thread. Source reads/decompression run on one bounded
    /// background producer; D3D11 uploads remain on the UI thread and can be budgeted per frame.
    /// </summary>
    private void AdvanceProgressiveSequence(TextureFileReport report, string? looseSourcePath, long loosePayloadOffset, long looseFileLength, DateTime looseLastWriteUtc)
    {
        if (!_gpuSequenceActive)
            return;

        _gpuSequenceFrameCount++;
        var probe = _plugin.TextureGpuUpload;
        if (!ReferenceEquals(_gpuSequenceReport, report)
            || !string.Equals(_gpuSequenceLooseFilePath, looseSourcePath, StringComparison.OrdinalIgnoreCase)
            || _gpuSequenceLoosePayloadOffset != loosePayloadOffset
            || _gpuSequenceLooseFileLength != looseFileLength
            || _gpuSequenceLooseLastWriteUtc != looseLastWriteUtc)
        {
            StopProgressiveSequence("Stopped because the selected texture source changed.", clearResults: false);
            return;
        }

        var prefetch = _gpuSequencePrefetchQueue;
        if (prefetch == null)
        {
            StopProgressiveSequence("Stopped because the background mip prefetch queue is unavailable.", clearResults: false);
            probe.SetError(_gpuSequenceStatus);
            return;
        }

        bool previewWasReadyAtFrameStart = probe.IsPreviewReady;
        int uploadedThisFrame = 0;
        int newestUploadedMipThisFrame = -1;
        double uploadSubmissionMillisecondsThisFrame = 0d;
        var frameTimer = Stopwatch.StartNew();
        bool prefetchWasEmpty = false;
        int maximumUploadsThisFrame = _gpuSequenceVerifyReadback
            || _gpuSequenceActiveSchedulingMode == GpuUploadSchedulingMode.OneMipPerFrame
                ? 1
                : int.MaxValue;

        while (_gpuSequenceActive && uploadedThisFrame < maximumUploadsThisFrame)
        {
            // Preserve a full UI frame for the first coarse preview to be drawn before allowing
            // the next frame to batch additional uploads and visibly refine it.
            if (uploadedThisFrame > 0 && !previewWasReadyAtFrameStart)
                break;

            if (uploadedThisFrame > 0 && !_gpuSequenceVerifyReadback)
            {
                if ((_gpuSequenceActiveSchedulingMode == GpuUploadSchedulingMode.ElapsedTimeBudget
                        || _gpuSequenceActiveSchedulingMode == GpuUploadSchedulingMode.ElapsedBudgetWithLargeMipIsolation)
                    && frameTimer.Elapsed.TotalMilliseconds >= _gpuSequenceActiveCpuUploadBudgetMilliseconds)
                {
                    break;
                }
            }

            if (_gpuSequencePendingMipRead == null)
            {
                if (!prefetch.TryDequeue(out var queuedMipRead) || queuedMipRead == null)
                {
                    prefetchWasEmpty = !prefetch.ProducerCompleted;
                    break;
                }

                _gpuSequencePendingMipRead = queuedMipRead;
            }

            TextureMipReadReport? pendingMipRead = _gpuSequencePendingMipRead;
            if (pendingMipRead == null)
                break;
            TextureMipReadReport mipRead = pendingMipRead;
            if (mipRead.MipIndex != _gpuSequenceNextMip)
            {
                StopProgressiveSequence(
                    $"Stopped because the prefetch queue returned Mip {mipRead.MipIndex}, but Mip {_gpuSequenceNextMip} was expected.",
                    clearResults: false);
                probe.SetError(_gpuSequenceStatus);
                return;
            }

            if (uploadedThisFrame > 0
                && _gpuSequenceActiveSchedulingMode == GpuUploadSchedulingMode.ElapsedBudgetWithLargeMipIsolation
                && mipRead.DecompressedBytes >= _gpuSequenceLargeMipIsolationThresholdBytes
                && !_gpuSequenceVerifyReadback)
            {
                _gpuSequenceLargeMipDeferralCount++;
                _gpuSequenceBudgetDeferralCount++;
                _gpuSequenceResults.Add($"Scheduler isolated large Mip {mipRead.MipIndex} ({FormatBytes(mipRead.DecompressedBytes)}): starts on a fresh UI frame; threshold {_gpuSequenceLargeMipIsolationThresholdBytes / (1024d * 1024d):F2} MiB.");
                break;
            }

            if (uploadedThisFrame > 0 && _gpuSequenceActiveSchedulingMode == GpuUploadSchedulingMode.PredictiveBudget && !_gpuSequenceVerifyReadback)
            {
                double estimatedNextUploadMilliseconds = EstimateMipUploadSubmissionMilliseconds(mipRead.DecompressedBytes);
                double remainingBudgetMilliseconds = Math.Max(0d,
                    _gpuSequenceActiveCpuUploadBudgetMilliseconds - uploadSubmissionMillisecondsThisFrame);
                if (estimatedNextUploadMilliseconds > remainingBudgetMilliseconds)
                {
                    _gpuSequenceBudgetDeferralCount++;
                    _gpuSequenceLastDeferredEstimateMilliseconds = estimatedNextUploadMilliseconds;
                    _gpuSequenceLastDeferredRemainingMilliseconds = remainingBudgetMilliseconds;
                    _gpuSequenceResults.Add($"Scheduler deferred Mip {mipRead.MipIndex}: estimated {estimatedNextUploadMilliseconds:F3} ms, with {remainingBudgetMilliseconds:F3} ms remaining; held for the next UI frame.");
                    break;
                }
            }

            _gpuSequencePendingMipRead = null;
            _gpuSequenceCurrentMip = mipRead.MipIndex;
            try
            {
                // Defer SRV recreation while batching. The first coarse mip still gets its own
                // view immediately so it can be drawn for a full UI frame before later uploads.
                var upload = probe.Upload(report, mipRead, refreshPreview: false);
                uploadSubmissionMillisecondsThisFrame += upload.SubmissionMilliseconds;
                if (_gpuSequenceActiveSchedulingMode != GpuUploadSchedulingMode.OneMipPerFrame && !_gpuSequenceVerifyReadback)
                    ObserveMipUploadSubmission(mipRead.DecompressedBytes, upload.SubmissionMilliseconds);
                newestUploadedMipThisFrame = mipRead.MipIndex;
                if (!probe.IsPreviewReady)
                    probe.RefreshProgressivePreview(mipRead.MipIndex);
                if (_gpuSequenceFirstPreviewMilliseconds < 0d && probe.IsPreviewReady)
                {
                    _gpuSequenceFirstPreviewMilliseconds = _gpuSequenceWallTimer.Elapsed.TotalMilliseconds;
                    _gpuSequenceFirstPreviewFrame = _gpuSequenceFrameCount;
                }

                TextureGpuReadbackReport? readback = _gpuSequenceVerifyReadback ? probe.VerifyLastUpload() : null;
                _gpuSequenceArchiveBytes = checked(_gpuSequenceArchiveBytes + mipRead.GroupBytesRequested);
                _gpuSequenceMipBytes = checked(_gpuSequenceMipBytes + mipRead.DecompressedBytes);
                _gpuSequenceReadMilliseconds += mipRead.ElapsedMilliseconds;
                _gpuSequenceUploadMilliseconds += upload.SubmissionMilliseconds;
                if (readback != null)
                    _gpuSequenceReadbackMilliseconds += readback.ElapsedMilliseconds;

                if (readback is { Matches: false })
                {
                    _gpuSequenceResults.Add($"Mip {_gpuSequenceCurrentMip}: GPU readback MISMATCH at 0x{readback.MismatchOffset:X}.");
                    StopProgressiveSequence($"Stopped: Mip {_gpuSequenceCurrentMip} did not match its source payload.", clearResults: false);
                    probe.SetError(_gpuSequenceStatus);
                    return;
                }

                string result = readback == null
                    ? "uploaded (readback skipped)"
                    : $"readback {readback.ElapsedMilliseconds:F2} ms — byte-for-byte match";
                _gpuSequenceResults.Add($"Mip {_gpuSequenceCurrentMip} ({FormatBytes(mipRead.DecompressedBytes)}): upload {upload.SubmissionMilliseconds:F3} ms; {result}.");
                _gpuSequenceNextMip = _gpuSequenceCurrentMip - 1;
                uploadedThisFrame++;

                if (_gpuSequenceNextMip < 0)
                {
                    probe.RefreshProgressivePreview(newestUploadedMipThisFrame);
                    RecordGpuSequenceFrame(uploadedThisFrame, uploadSubmissionMillisecondsThisFrame);
                    _gpuSequenceActive = false;
                    _gpuSequenceWallMilliseconds = _gpuSequenceWallTimer.Elapsed.TotalMilliseconds;
                    _gpuSequenceWallTimer.Stop();
                    _gpuSequencePrefetchQueue?.Dispose();
                    _gpuSequencePrefetchQueue = null;

                    bool completePayload = _gpuSequenceMipBytes == report.TotalMipBytes && probe.UploadedMipCount == report.MipCount;
                    string completion = _gpuSequenceVerifyReadback
                        ? "uploaded coarse-to-fine and verified byte-for-byte on the GPU"
                        : "uploaded coarse-to-fine (no staging readback was performed)";
                    _gpuSequenceStatus = completePayload
                        ? $"Complete: all {report.MipCount} mip levels {completion}."
                        : $"Sequence ended, but the uploaded payload ({FormatBytes(_gpuSequenceMipBytes)}) or distinct mip count did not match the expected texture total ({FormatBytes(report.TotalMipBytes)}; {report.MipCount} mips).";
                    if (!completePayload)
                        probe.SetError(_gpuSequenceStatus);
                    return;
                }
            }
            catch (Exception ex)
            {
                StopProgressiveSequence($"Failed while uploading/verifying Mip {_gpuSequenceCurrentMip}: {ex.Message}", clearResults: false);
                probe.SetError(_gpuSequenceStatus);
                return;
            }
        }

        if (!_gpuSequenceActive)
            return;

        if (newestUploadedMipThisFrame >= 0)
            probe.RefreshProgressivePreview(newestUploadedMipThisFrame);

        if (_gpuSequencePendingMipRead == null && prefetch.ProducerCompleted && prefetch.IsDrained)
        {
            RecordGpuSequenceFrame(uploadedThisFrame, uploadSubmissionMillisecondsThisFrame);
            if (prefetch.Error != null)
            {
                StopProgressiveSequence($"Background mip read failed while preparing Mip {_gpuSequenceNextMip}: {prefetch.Error.Message}", clearResults: false);
                probe.SetError(_gpuSequenceStatus);
            }
            else if (_gpuSequenceNextMip >= 0)
            {
                StopProgressiveSequence($"Background mip reader completed early; Mip {_gpuSequenceNextMip} and finer levels were not supplied.", clearResults: false);
                probe.SetError(_gpuSequenceStatus);
            }
            return;
        }

        if (prefetchWasEmpty)
        {
            _gpuSequencePrefetchEmptyPollCount++;
            if (uploadedThisFrame == 0)
                _gpuSequencePrefetchStarvedFrameCount++;
        }

        RecordGpuSequenceFrame(uploadedThisFrame, uploadSubmissionMillisecondsThisFrame);
        if (uploadedThisFrame > 0)
        {
            if (_gpuSequencePendingMipRead != null)
            {
                _gpuSequenceStatus = _gpuSequenceActiveSchedulingMode == GpuUploadSchedulingMode.PredictiveBudget
                    ? $"Uploaded {uploadedThisFrame} mip level(s) this UI frame. Mip {_gpuSequencePendingMipRead.MipIndex} was deferred: estimated {_gpuSequenceLastDeferredEstimateMilliseconds:F3} ms vs {_gpuSequenceLastDeferredRemainingMilliseconds:F3} ms remaining budget."
                    : $"Uploaded {uploadedThisFrame} mip level(s) this UI frame. Mip {_gpuSequencePendingMipRead.MipIndex} is queued for the next UI frame.";
            }
            else
            {
                _gpuSequenceStatus = $"Uploaded {uploadedThisFrame} mip level(s) this UI frame. Next: Mip {_gpuSequenceNextMip}; queued refinement will continue on the next frame.";
            }
        }
        else if (_gpuSequencePendingMipRead != null)
        {
            _gpuSequenceStatus = $"Mip {_gpuSequencePendingMipRead.MipIndex} is ready and will be submitted at the start of the next UI frame.";
        }
        else
        {
            _gpuSequenceStatus = $"Prefetching Mip {_gpuSequenceNextMip} and following levels on a background worker...";
        }
    }

    private double EstimateMipUploadSubmissionMilliseconds(long payloadBytes)
    {
        const double fixedSubmissionOverheadMilliseconds = 0.008d;
        double payloadMiB = payloadBytes / (1024d * 1024d);
        return fixedSubmissionOverheadMilliseconds + (payloadMiB * _gpuSequenceEstimatedUploadMillisecondsPerMiB);
    }

    private void ObserveMipUploadSubmission(long payloadBytes, double submissionMilliseconds)
    {
        // Small BC7 mips are dominated by timer/driver-call noise. Calibrate only on >=1 MiB
        // uploads, which are the levels that can materially consume a frame's upload budget.
        const long calibrationMinimumBytes = 1024L * 1024L;
        const double fixedSubmissionOverheadMilliseconds = 0.008d;
        if (payloadBytes < calibrationMinimumBytes || submissionMilliseconds <= fixedSubmissionOverheadMilliseconds)
            return;

        double payloadMiB = payloadBytes / (1024d * 1024d);
        double measuredRate = (submissionMilliseconds - fixedSubmissionOverheadMilliseconds) / payloadMiB;
        if (double.IsNaN(measuredRate) || double.IsInfinity(measuredRate))
            return;

        // Clamp outliers and adapt gradually. This is a scheduling estimate, not a guarantee
        // of driver/GPU execution time; one indivisible upload can still exceed the frame budget.
        measuredRate = Math.Clamp(measuredRate, 0.04d, 2.0d);
        _gpuSequenceEstimatedUploadMillisecondsPerMiB = Math.Clamp(
            (_gpuSequenceEstimatedUploadMillisecondsPerMiB * 0.70d) + (measuredRate * 0.30d),
            0.04d,
            2.0d);
    }

    private void RecordGpuSequenceFrame(int uploadedMipCount, double uploadSubmissionMilliseconds)
    {
        _gpuSequenceFrameBatchCounts.Add(uploadedMipCount);
        _gpuSequenceFrameUploadSubmissionMilliseconds.Add(uploadSubmissionMilliseconds);
        _gpuSequenceMaximumFrameUploadMilliseconds = Math.Max(
            _gpuSequenceMaximumFrameUploadMilliseconds,
            uploadSubmissionMilliseconds);

        // A single mip is indivisible. Count a frame as over budget only when its summed
        // UpdateSubresource submission time exceeds the configured budget by a small tolerance.
        if (_gpuSequenceActiveSchedulingMode != GpuUploadSchedulingMode.OneMipPerFrame && !_gpuSequenceVerifyReadback
            && uploadSubmissionMilliseconds > _gpuSequenceActiveCpuUploadBudgetMilliseconds + 0.05d)
        {
            _gpuSequenceBudgetOverrunFrameCount++;
            if (uploadedMipCount <= 1)
                _gpuSequenceSingleMipOverrunFrameCount++;
            else
                _gpuSequenceBatchOverrunFrameCount++;
        }
    }

    private void StopProgressiveSequence(string status, bool clearResults)
    {
        _gpuSequenceActive = false;
        _gpuSequencePendingMipRead = null;
        _gpuSequencePrefetchQueue?.Dispose();
        _gpuSequencePrefetchQueue = null;
        _gpuSequenceCurrentMip = -1;
        _gpuSequenceNextMip = -1;
        _gpuSequenceReport = null;
        _gpuSequenceLooseFilePath = null;
        _gpuSequenceLoosePayloadOffset = 0;
        _gpuSequenceLooseFileLength = 0;
        _gpuSequenceLooseLastWriteUtc = default;
        if (_gpuSequenceWallTimer.IsRunning)
        {
            _gpuSequenceWallMilliseconds = _gpuSequenceWallTimer.Elapsed.TotalMilliseconds;
            _gpuSequenceWallTimer.Stop();
        }
        _gpuSequenceStatus = status;
        if (!clearResults)
            return;

        _gpuSequenceResults.Clear();
        _gpuSequenceFrameBatchCounts.Clear();
        _gpuSequenceFrameUploadSubmissionMilliseconds.Clear();
        _gpuSequenceEstimatedUploadMillisecondsPerMiB = InitialEstimatedUploadMillisecondsPerMiB;
        _gpuSequenceBudgetDeferralCount = 0;
        _gpuSequenceLargeMipDeferralCount = 0;
        _gpuSequenceLargeMipIsolationThresholdBytes = 0;
        _gpuSequencePrefetchEmptyPollCount = 0;
        _gpuSequencePrefetchStarvedFrameCount = 0;
        _gpuSequenceBudgetOverrunFrameCount = 0;
        _gpuSequenceSingleMipOverrunFrameCount = 0;
        _gpuSequenceBatchOverrunFrameCount = 0;
        _gpuSequenceMaximumFrameUploadMilliseconds = 0d;
        _gpuSequenceLastDeferredEstimateMilliseconds = 0d;
        _gpuSequenceLastDeferredRemainingMilliseconds = 0d;
        _gpuSequenceArchiveBytes = 0;
        _gpuSequenceMipBytes = 0;
        _gpuSequenceReadMilliseconds = 0;
        _gpuSequenceUploadMilliseconds = 0;
        _gpuSequenceReadbackMilliseconds = 0;
        _gpuSequenceWallMilliseconds = 0;
        _gpuSequenceFirstPreviewMilliseconds = -1d;
        _gpuSequenceFirstPreviewFrame = -1;
        _gpuSequenceFrameCount = 0;
        _gpuSequenceWallTimer.Reset();
    }

    private static string FormatBytes(long bytes)
    {
        const double KiB = 1024d;
        const double MiB = KiB * 1024d;
        return bytes >= MiB ? $"{bytes / MiB:F2} MiB" : bytes >= KiB ? $"{bytes / KiB:F1} KiB" : $"{bytes} B";
    }

    private static string GetSimpleStatus(bool enabled, string status)
    {
        if (!enabled || status == "Off") return "Off";
        if (status.Contains("housing area", StringComparison.OrdinalIgnoreCase)) return "Visit a housing area";
        if (status.Contains("GPose", StringComparison.OrdinalIgnoreCase)) return "Paused in GPose";
        if (status.Contains("cutscene", StringComparison.OrdinalIgnoreCase)) return "Paused in cutscene";
        if (status.Contains("player not in camera view", StringComparison.OrdinalIgnoreCase)) return "Paused: character off screen";
        if (status.Contains("Loading", StringComparison.OrdinalIgnoreCase)) return "Loading";
        if (status.Contains("Camera unavailable", StringComparison.OrdinalIgnoreCase)) return "Waiting for camera";
        if (status == "Starting") return "Starting";
        return "Paused";
    }

    private void Flush()
    {
        if (!_dirty) return;
        _plugin.Config.Sanitize();
        _plugin.Config.Save(_plugin.PluginInterface);
        _lastSave = DateTime.UtcNow;
        _dirty = false;
    }

    private static void Tip(string? tip)
    {
        if (tip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    private static bool Toggle(string label, ref bool value, string? tip = null)
    {
        bool changed = ImGui.Checkbox(label, ref value);
        Tip(tip);
        return changed;
    }

    private static bool DistanceSlider(string label, ref float value, float min, float max, string? tip = null)
    {
        // Show a clear Off label at zero instead of displaying "0 yalms".
        string format = value < 0.5f ? "Off" : "%.0f yalms";
        return Slider(label, ref value, min, max, format, tip);
    }

    private static bool Slider(string label, ref float value, float min, float max, string format, string? tip = null)
    {
        ImGui.SetNextItemWidth(210f);
        bool changed = ImGui.SliderFloat(label, ref value, min, max, format);
        Tip(tip);
        return changed;
    }
}

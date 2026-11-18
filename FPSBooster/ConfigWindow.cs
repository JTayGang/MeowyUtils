using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
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
    private string _texturePath = string.Empty;
    private bool _manualFilePathInput;
    private int _readMipIndex = -1;
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
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 180), MaximumSize = new Vector2(700, 820) };
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
        _plugin.NativeTextureHandle.Release();
        _plugin.PenumbraTextureSource.Clear();
        _manualFilePathInput = false;
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

                ImGui.Separator();
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

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Textures"))
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

    private void DrawTextures()
    {
        var inspector = _plugin.TextureStreaming;
        ImGui.TextWrapped("Enter either a game-relative .tex path (for vanilla SqPack inspection and optional Penumbra resolution) or an absolute filesystem path to a loose .tex file (inspected directly, without Penumbra IPC).");
        ImGui.TextWrapped("Vanilla mip reads decompress one selected SqPack group. Loose .tex files use their own TexHeader OffsetToSurface entries for direct raw mip reads. The optional GPU experiment always uses a separate plugin-owned test texture.");
        ImGui.Separator();

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("Game path or absolute .tex path", ref _texturePath, 512);
        if (ImGui.Button("Inspect .tex", new Vector2(130f, 0f)))
        {
            StopProgressiveSequence("Stopped because a different texture was inspected.", clearResults: true);
            inspector.Clear();
            _plugin.TextureGpuUpload.Reset();
            _plugin.NativeTextureHandle.Release();
            _plugin.PenumbraTextureSource.Clear();

            string inputPath = _texturePath.Trim().Trim('"');
            _texturePath = inputPath;
            _manualFilePathInput = Path.IsPathRooted(inputPath);
            try
            {
                if (_manualFilePathInput)
                    _plugin.PenumbraTextureSource.InspectFilePath(inputPath);
                else
                    inspector.Inspect(inputPath);
            }
            catch (Exception ex)
            {
                ImGui.TextColored(new Vector4(1f, .35f, .3f, 1f), ex.Message);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear", new Vector2(90f, 0f)))
        {
            StopProgressiveSequence("Stopped because the texture inspection was cleared.", clearResults: true);
            inspector.Clear();
            _plugin.TextureGpuUpload.Reset();
            _plugin.NativeTextureHandle.Release();
            _plugin.PenumbraTextureSource.Clear();
            _texturePath = string.Empty;
            _manualFilePathInput = false;
        }

        var selectedSource = _plugin.PenumbraTextureSource.Result;
        bool manualFilePath = _manualFilePathInput || selectedSource?.IsManualFilePath == true;
        if (manualFilePath)
        {
            ImGui.TextDisabled(_plugin.PenumbraTextureSource.Status);
            if (!string.IsNullOrEmpty(_plugin.PenumbraTextureSource.Error))
                ImGui.TextColored(new Vector4(1f, .35f, .3f, 1f), _plugin.PenumbraTextureSource.Error);
        }
        else
        {
            ImGui.TextDisabled(inspector.Status);
            if (inspector.IsLoading)
                ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), "Reading archive metadata only...");
            if (!string.IsNullOrEmpty(inspector.Error))
                ImGui.TextWrapped(inspector.Error);
        }

        var report = inspector.Report ?? (manualFilePath ? selectedSource?.TextureReport : null);
        if (report == null)
        {
            if (manualFilePath)
            {
                if (selectedSource != null && !string.IsNullOrEmpty(selectedSource.FileInspectionError))
                    ImGui.TextWrapped(selectedSource.FileInspectionError);
                else if (string.IsNullOrEmpty(_plugin.PenumbraTextureSource.Error))
                    ImGui.TextDisabled("No valid texture header is available for this local file.");
            }
            return;
        }

        if (!manualFilePath)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Selective SqPack mip read — archive only");
        }
        else
        {
            ImGui.Separator();
            ImGui.TextDisabled("Selected local TEX file — direct mip reads");
            ImGui.TextWrapped("This report came from the absolute path entered above. No game-path lookup or Penumbra IPC was performed. Mip reads use the loose file's explicit OffsetToSurface entries.");
        }
        if (!manualFilePath && report.MipCount > 0)
        {
            if (_readMipIndex < 0 || _readMipIndex >= report.MipCount)
                _readMipIndex = report.MipCount - 1;

            ImGui.SetNextItemWidth(110f);
            ImGui.InputInt("Mip index", ref _readMipIndex);
            _readMipIndex = Math.Clamp(_readMipIndex, 0, report.MipCount - 1);
            ImGui.SameLine();
            if (_gpuSequenceActive)
                ImGui.BeginDisabled();
            if (ImGui.Button("Read mip only", new Vector2(140f, 0f)))
                inspector.ReadMip(_readMipIndex);
            if (_gpuSequenceActive)
                ImGui.EndDisabled();
        }
        else if (report.MipCount <= 0)
        {
            ImGui.TextDisabled("This texture has no mip levels available for reading.");
        }

        if (!manualFilePath)
        {
            if (inspector.IsReadingMip && ReferenceEquals(inspector.MipReadSourceReport, report))
                ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), $"Reading SqPack Group {_readMipIndex} only...");

            var currentMipRead = inspector.MipRead;
            if (ReferenceEquals(inspector.MipReadSourceReport, report))
            {
                string? mipReadError = inspector.MipReadError;
                if (!string.IsNullOrEmpty(mipReadError))
                    ImGui.TextColored(new Vector4(1f, .65f, .35f, 1f), mipReadError);

                if (currentMipRead != null)
                    DrawMipReadResult(currentMipRead, isLooseFile: false);
            }
        }

        ImGui.Separator();
        if (!manualFilePath)
        {
            DrawNativeTextureHandleProbe(report);
            ImGui.Separator();
        }
        else
        {
            ImGui.TextDisabled("The native FFXIV ResourceHandle probe is only available for game-relative paths; an arbitrary local file path is not a game resource key.");
        }
        DrawPenumbraTextureSourceProbe(report);

        // When a validated loose-file source is available (user-selected or Penumbra-resolved),
        // make that asset the GPU experiment target. Otherwise retain the game-relative SqPack report.
        var resolvedSource = _plugin.PenumbraTextureSource.Result;
        TextureFileReport gpuReport = report;
        string? looseSourcePath = null;
        long loosePayloadOffset = 0;
        long looseFileLength = 0;
        DateTime looseLastWriteUtc = default;
        bool looseMipChainComplete = false;
        if (resolvedSource is { TextureReport: not null }
            && (resolvedSource.HasAnyValidLooseMipRanges
                || (resolvedSource.IsManualFilePath && !resolvedSource.IsType4Container)))
        {
            // Even a partial replacement is the correct target for individual manual mip
            // reads/uploads. The automated full-chain sequence remains separately gated.
            gpuReport = resolvedSource.TextureReport;
            looseSourcePath = resolvedSource.ResolvedPath;
            loosePayloadOffset = resolvedSource.MipPayloadStartOffset;
            looseFileLength = resolvedSource.FileLength;
            looseLastWriteUtc = resolvedSource.FileLastWriteUtc;
            looseMipChainComplete = resolvedSource.HasValidLooseMipRanges;
        }

        TextureMipReadReport? gpuMipRead = ReferenceEquals(inspector.MipReadSourceReport, gpuReport)
            ? inspector.MipRead
            : null;
        ImGui.Separator();
        DrawGpuUploadProbe(gpuReport, gpuMipRead, looseSourcePath, loosePayloadOffset, looseFileLength, looseLastWriteUtc, looseMipChainComplete);
        ImGui.Separator();
        ImGui.TextDisabled(report.Path);
        ImGui.Columns(2, "TextureSummary", false);
        ImGui.SetColumnWidth(0, 190f);
        ImGui.Text("Base resolution"); ImGui.NextColumn(); ImGui.Text($"{report.Width} × {report.Height}"); ImGui.NextColumn();
        ImGui.Text("Mip levels"); ImGui.NextColumn(); ImGui.Text(report.MipCount.ToString()); ImGui.NextColumn();
        ImGui.Text("Format"); ImGui.NextColumn(); ImGui.Text(report.Format); ImGui.NextColumn();
        ImGui.Text("Texture type"); ImGui.NextColumn(); ImGui.Text(report.Type); ImGui.NextColumn();
        ImGui.Text("Total mip data"); ImGui.NextColumn(); ImGui.Text(FormatBytes(report.TotalMipBytes)); ImGui.NextColumn();
        ImGui.Text(manualFilePath ? "Header bytes inspected" : "Archive metadata requested"); ImGui.NextColumn(); ImGui.Text($"{FormatBytes(report.MetadataBytesRequested)} · {report.MetadataReadMilliseconds:F2} ms"); ImGui.NextColumn();
        ImGui.Columns(1);

        ImGui.Separator();
        ImGui.TextDisabled("LOD start points");
        ImGui.Columns(3, "TextureLods", false);
        foreach (var lod in report.Lods)
        {
            ImGui.Text(lod.Name);
            ImGui.NextColumn();
            ImGui.Text(lod.IsValid ? $"Mip {lod.StartMip}" : $"Unavailable ({lod.StartMip})");
            ImGui.NextColumn();
            ImGui.Text(lod.IsValid ? $"{lod.Width} × {lod.Height}" : "—");
            ImGui.NextColumn();
        }
        ImGui.Columns(1);

        ImGui.Separator();
        ImGui.TextDisabled("Smallest-first upload plan (estimated payload)");
        ImGui.TextWrapped($"The coarsest mip alone is {FormatBytes(report.CoarsestMipBytes)} ({report.CoarsestMipPercent:F1}% of the mip payload). The remaining, larger mips would be scheduled afterward.");
        ImGui.Columns(4, "TextureMipPlan", false);
        ImGui.SetColumnWidth(0, 55f);
        ImGui.SetColumnWidth(1, 55f);
        ImGui.SetColumnWidth(2, 130f);
        ImGui.Text("Step"); ImGui.NextColumn();
        ImGui.Text("Mip"); ImGui.NextColumn();
        ImGui.Text("Resolution"); ImGui.NextColumn();
        ImGui.Text("Payload"); ImGui.NextColumn();
        ImGui.Separator();

        long cumulativeBytes = 0;
        for (int step = 0; step < report.Mips.Count; step++)
        {
            var mip = report.Mips[report.Mips.Count - 1 - step];
            cumulativeBytes += mip.Bytes;
            ImGui.Text((step + 1).ToString()); ImGui.NextColumn();
            ImGui.Text(mip.Index.ToString()); ImGui.NextColumn();
            ImGui.Text($"{mip.Width} × {mip.Height}"); ImGui.NextColumn();
            ImGui.Text($"{FormatBytes(mip.Bytes)}  ({FormatBytes(cumulativeBytes)} total)"); ImGui.NextColumn();
        }
        ImGui.Columns(1);

        var sqPack = report.SqPack;
        if (sqPack != null)
        {
            ImGui.Separator();
            ImGui.TextDisabled("SqPack archive layout");
            if (!string.IsNullOrEmpty(sqPack.Note))
                ImGui.TextWrapped(sqPack.Note);

            if (!string.IsNullOrEmpty(sqPack.DatFileName))
            {
                ImGui.Columns(2, "TextureSqPackSummary", false);
                ImGui.SetColumnWidth(0, 190f);
                ImGui.Text("Archive file"); ImGui.NextColumn(); ImGui.Text(sqPack.DatFileName); ImGui.NextColumn();
                ImGui.Text("Entry offset"); ImGui.NextColumn(); ImGui.Text($"0x{sqPack.FileOffset:X}"); ImGui.NextColumn();
                ImGui.Text("Entry header size"); ImGui.NextColumn(); ImGui.Text(FormatBytes(sqPack.FileHeaderSize)); ImGui.NextColumn();
                ImGui.Text("Raw file size"); ImGui.NextColumn(); ImGui.Text(FormatBytes(sqPack.RawFileSize)); ImGui.NextColumn();
                ImGui.Text("Group payload totals"); ImGui.NextColumn(); ImGui.Text($"{FormatBytes(sqPack.TotalCompressedBytes)} stored / {FormatBytes(sqPack.TotalDecompressedBytes)} expanded"); ImGui.NextColumn();
                ImGui.Columns(1);
            }

            if (sqPack.Groups.Count > 0)
            {
                ImGui.TextDisabled("LOD-group records (SqPack storage order)");
                ImGui.Columns(8, "TextureSqPackGroups", false);
                ImGui.SetColumnWidth(0, 46f);
                ImGui.SetColumnWidth(1, 100f);
                ImGui.SetColumnWidth(2, 76f);
                ImGui.SetColumnWidth(3, 76f);
                ImGui.SetColumnWidth(4, 76f);
                ImGui.SetColumnWidth(5, 78f);
                ImGui.SetColumnWidth(6, 48f);
                ImGui.SetColumnWidth(7, 48f);
                ImGui.Text("Group"); ImGui.NextColumn();
                ImGui.Text("Mip match"); ImGui.NextColumn();
                ImGui.Text("Comp. offset"); ImGui.NextColumn();
                ImGui.Text("Comp. size"); ImGui.NextColumn();
                ImGui.Text("Raw size"); ImGui.NextColumn();
                ImGui.Text("Block offset"); ImGui.NextColumn();
                ImGui.Text("Blocks"); ImGui.NextColumn();
                ImGui.Text("Range"); ImGui.NextColumn();
                ImGui.Separator();

                foreach (var group in sqPack.Groups)
                {
                    ImGui.Text(group.Index.ToString()); ImGui.NextColumn();
                    ImGui.Text(group.MatchedMipIndex.HasValue
                        ? $"Mip {group.MatchedMipIndex.Value} ({group.MatchedMipWidth}×{group.MatchedMipHeight})"
                        : "Unmatched"); ImGui.NextColumn();
                    ImGui.Text($"0x{group.CompressedOffset:X}");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Absolute .dat offset: 0x{group.AbsoluteDataOffset:X}");
                    ImGui.NextColumn();
                    ImGui.Text(FormatBytes(group.CompressedSize)); ImGui.NextColumn();
                    ImGui.Text(FormatBytes(group.DecompressedSize)); ImGui.NextColumn();
                    ImGui.Text($"0x{group.BlockOffset:X}"); ImGui.NextColumn();
                    ImGui.Text(group.BlockCount.ToString()); ImGui.NextColumn();
                    if (group.IsDataRangeValid)
                        ImGui.Text("OK");
                    else
                        ImGui.TextColored(new Vector4(1f, .35f, .3f, 1f), "Invalid");
                    ImGui.NextColumn();
                }

                ImGui.Columns(1);
                if (!string.IsNullOrEmpty(sqPack.MipMappingNote))
                {
                    if (sqPack.HasStrictMipGroupMapping)
                        ImGui.TextWrapped(sqPack.MipMappingNote);
                    else
                        ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), sqPack.MipMappingNote);
                }
            }
        }

        ImGui.Separator();
        ImGui.TextWrapped("Important: Inspect now reads only the SqPack entry header, LOD-group descriptors, and the .tex header. Read mip separately reads the selected group's stride-table entries and blocks, then validates the decompressed byte count and reports a SHA-256 hash. It does not load other mips, call Lumina's full GetFileAsync<TexFile> path, modify game-owned texture resources, or report game texture residency. The optional GPU experiment creates a separate plugin-owned BC7 test texture. Byte-for-byte Lumina comparison was already confirmed for Mips 0, 5, and 9 in the previous verification build.");
        ImGui.TextWrapped("Byte counters represent logical bytes requested by this reader, not guaranteed physical disk I/O; the operating system's filesystem cache and read-ahead can change actual device traffic.");
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
        ImGui.TextDisabled("Native FFXIV texture-resource handle (manual observation)");
        ImGui.TextWrapped("Acquire calls the game's normal asynchronous ResourceHandle.GetAsync API for this .tex path. It may reuse a cached native handle or initiate the game's normal load. FPSBooster holds the returned reference until Release. This is not a passive lookup. The SqPack inspector reads the vanilla archive entry; this native handle reports the effective resource after active redirection hooks (including Penumbra). The probe only reads persistent handle/header/Texture fields; it never writes ChangeLod or accesses the temporary TextureWhileLoading/DataWhileLoading pointers.");

        if (!probe.IsAcquired)
        {
            if (ImGui.Button("Acquire native async texture handle", new Vector2(250f, 0f)))
            {
                try { probe.Acquire(report.Path); }
                catch (Exception ex) { probe.SetError(ex.Message); }
            }
        }
        else
        {
            if (ImGui.Button("Release native handle reference", new Vector2(220f, 0f)))
                probe.Release();
        }

        if (!string.IsNullOrEmpty(probe.Error))
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), probe.Error);
        else
            ImGui.TextDisabled(probe.Status);

        var snapshot = probe.Refresh();
        if (snapshot == null)
            return;

        ImGui.Columns(2, "NativeTextureHandleSummary", false);
        ImGui.SetColumnWidth(0, 205f);
        ImGui.Text("Handle address"); ImGui.NextColumn(); ImGui.Text($"0x{(nuint)snapshot.HandleAddress:X}"); ImGui.NextColumn();
        ImGui.Text("Runtime handle size"); ImGui.NextColumn(); ImGui.Text($"0x{snapshot.RuntimeHandleSize:X} ({snapshot.RuntimeHandleSize} B)"); ImGui.NextColumn();
        ImGui.Text("Native refcount (raw)"); ImGui.NextColumn(); ImGui.Text(snapshot.ReferenceCount.ToString()); ImGui.NextColumn();
        ImGui.Text("File sizes (raw)"); ImGui.NextColumn(); ImGui.Text($"{snapshot.FileSize:N0} / {snapshot.FileSize2:N0} / {snapshot.FileSize3:N0}"); ImGui.NextColumn();
        ImGui.Text("Last I/O result (raw)"); ImGui.NextColumn(); ImGui.Text($"{snapshot.LastIoResult} (0x{snapshot.LastIoResult:X2})"); ImGui.NextColumn();
        ImGui.Text("ChangeLod field byte +0x104"); ImGui.NextColumn(); ImGui.Text($"0x{snapshot.LodFlagByte:X2} · bit 0 = {snapshot.ChangeLodBit}"); ImGui.NextColumn();
        ImGui.Text("Native .tex header"); ImGui.NextColumn(); ImGui.Text($"{snapshot.HeaderWidth} × {snapshot.HeaderHeight} · {snapshot.HeaderFormat}"); ImGui.NextColumn();
        ImGui.Text("Header type / depth"); ImGui.NextColumn(); ImGui.Text($"{snapshot.HeaderType} · depth {snapshot.HeaderDepth}"); ImGui.NextColumn();
        ImGui.Text("MipCountAndFlag (raw)"); ImGui.NextColumn(); ImGui.Text($"{snapshot.HeaderMipCountAndFlag} (0x{snapshot.HeaderMipCountAndFlag:X2}) · array {snapshot.HeaderArraySize}"); ImGui.NextColumn();
        ImGui.Columns(1);

        if (!snapshot.HasTextureObject)
        {
            ImGui.TextColored(new Vector4(1f, .75f, .35f, 1f), "The persistent Texture pointer is not available yet. The native async request may still be in progress.");
            return;
        }

        ImGui.Separator();
        ImGui.TextDisabled("Persistent native Texture object");
        ImGui.Columns(2, "NativeTextureObjectSummary", false);
        ImGui.SetColumnWidth(0, 205f);
        ImGui.Text("Texture object"); ImGui.NextColumn(); ImGui.Text($"0x{(nuint)snapshot.TextureObjectAddress:X}"); ImGui.NextColumn();
        ImGui.Text("Actual dimensions"); ImGui.NextColumn(); ImGui.Text($"{snapshot.ActualWidth} × {snapshot.ActualHeight}"); ImGui.NextColumn();
        ImGui.Text("Allocated dimensions"); ImGui.NextColumn(); ImGui.Text($"{snapshot.AllocatedWidth} × {snapshot.AllocatedHeight}"); ImGui.NextColumn();
        ImGui.Text("Depth / MipLevel raw"); ImGui.NextColumn(); ImGui.Text($"{snapshot.Depth} / {snapshot.TextureMipLevelRaw}"); ImGui.NextColumn();
        ImGui.Text("Texture format / flags"); ImGui.NextColumn(); ImGui.Text($"{snapshot.TextureFormat} / {snapshot.TextureFlags}"); ImGui.NextColumn();
        ImGui.Text("Array size"); ImGui.NextColumn(); ImGui.Text(snapshot.ArraySize.ToString()); ImGui.NextColumn();
        ImGui.Text("D3D11 texture pointer"); ImGui.NextColumn(); ImGui.Text(snapshot.D3D11TextureAddress == 0 ? "null" : $"0x{(nuint)snapshot.D3D11TextureAddress:X}"); ImGui.NextColumn();
        ImGui.Text("D3D11 SRV pointer"); ImGui.NextColumn(); ImGui.Text(snapshot.D3D11ShaderResourceViewAddress == 0 ? "null" : $"0x{(nuint)snapshot.D3D11ShaderResourceViewAddress:X}"); ImGui.NextColumn();
        ImGui.Columns(1);

        DrawSqPackNativeComparison(report, snapshot);

        ImGui.TextWrapped("These D3D11 addresses belong to the native game resource. FPSBooster only displays their values; it does not wrap, AddRef, release, replace, map, or update them. ChangeLod is shown as a raw bit observation only; its semantics remain unknown and it is never modified.");
    }


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
            if (manualFilePath)
                _manualFilePathInput = false;
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

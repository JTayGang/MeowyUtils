using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FPSBooster;

/// <summary>
/// Parser and constrained rewriter for the nested AVFX chunk format. Rewrites only the
/// Camera-binder experiment's Bind, BdCn, and TmLn.BnNo fields; source game files are untouched.
/// </summary>
public static class AvfxBinderInspector
{
    private const int HeaderSize = 8;
    private const int MaximumChunks = 200_000;

    private static readonly HashSet<string> KnownTags = new(StringComparer.Ordinal)
    {
        "AVFX", "Ver", "BdCn", "Bind", "BnVr", "Data",
        "bStG", "bVSc", "bVSb", "bVSd", "bVSi", "bTSc", "bTSd", "bTSi",
        "bFTO", "bDSE", "bATS", "bIFY", "bBET", "Life", "RoTp",
        "PrpS", "Prp1", "Prp2", "PrpG", "Dst", "DstR", "Keys",
        "bCmS", "DwLy", "ScCn", "TlCn", "EmCn", "PrCn", "EfCn", "TxCn", "MdCn",
        "Schd", "TmLn", "Emit", "Ptcl", "Efct", "Tex", "Modl", "Item", "Clip",
        "LpSt", "LpEd", "BnNo", "TICn", "CpCn",
        "BPT", "BPTP", "BPID", "GenD", "CoUF", "bRng", "RnPT", "RnPX", "RnPY", "RnPZ", "RnRd", "BCT", "Pos",
        "KeyC", "BvPr", "BvPo", "RanT"
    };

    public sealed class BinderInfo
    {
        public int Index { get; init; }
        public int? TypeValue { get; init; }
        public string TypeName { get; init; } = "Unknown";
        public int? RotationValue { get; init; }
        public string RotationName { get; init; } = "Unknown";
        public bool? FollowingTargetOrientation { get; init; }
        public bool? AdjustToScreen { get; init; }
        public bool? VfxScaleEnabled { get; init; }
        public bool? StartToGlobalDirection { get; init; }
        public int? Life { get; init; }
        public int? CameraDistanceCurveBytes { get; init; }
        public int? CameraDistanceRandomCurveBytes { get; init; }
        public string CameraDistanceCurveSummary { get; init; } = "not present";
        public string CameraDistanceRandomCurveSummary { get; init; } = "not present";
        public string DataChildren { get; init; } = "(no Data block)";
        public string PropertySummary { get; init; } = "";
    }

    public sealed class TimelineInfo
    {
        public int Index { get; init; }
        public int? BinderIndex { get; init; }
    }

    public sealed class Result
    {
        public bool Success { get; init; }
        public string Error { get; init; } = "";
        public int FileBytes { get; init; }
        public int RootPayloadBytes { get; init; }
        public int? DeclaredBinderCount { get; init; }
        public int BinderCount { get; init; }
        public int CameraBinderCount { get; init; }
        public bool BinderCountMatchesDeclaration { get; init; }
        public bool? CameraSpace { get; init; }
        public int? DrawLayerValue { get; init; }
        public string DrawLayerName { get; init; } = "Not present";
        public IReadOnlyList<BinderInfo> Binders { get; init; } = Array.Empty<BinderInfo>();
        public IReadOnlyList<TimelineInfo> Timelines { get; init; } = Array.Empty<TimelineInfo>();
        public bool TimelineBinderReferencesMatch { get; init; }
        public string Report { get; init; } = "";
    }

    private readonly record struct Chunk(string Tag, string RawTag, int PayloadOffset, int Size)
    {
        public int PayloadEnd => PayloadOffset + Size;
    }

    public static Result Inspect(byte[]? data)
    {
        if (data == null || data.Length < HeaderSize)
            return Failure("File is empty or too small to contain an AVFX header.", data?.Length ?? 0);

        try
        {
            if (!TryReadChunk(data, 0, data.Length, out var root, out string rootError))
                return Failure("Could not read AVFX root block: " + rootError, data.Length);
            if (root.Tag != "AVFX")
                return Failure($"Unexpected root tag '{root.RawTag}' (normalized '{root.Tag}'); expected AVFX/XFVA.", data.Length);

            if (!TryReadChunks(data, root.PayloadOffset, root.Size, out var mainChunks, out string parseError))
                return Failure("AVFX root parsed, but its child blocks are invalid: " + parseError, data.Length);

            int? declaredBinderCount = ReadInt(data, mainChunks, "BdCn");
            bool? cameraSpace = ReadBool(data, mainChunks, "bCmS");
            int? drawLayer = ReadInt(data, mainChunks, "DwLy");
            var binders = new List<BinderInfo>();

            foreach (var binderChunk in mainChunks.Where(x => x.Tag == "Bind"))
            {
                if (!TryReadChunks(data, binderChunk.PayloadOffset, binderChunk.Size, out var binderChunks, out _))
                    continue;

                int? typeValue = ReadInt(data, binderChunks, "BnVr");
                int? rotationValue = ReadInt(data, binderChunks, "RoTp");
                var dataChunk = binderChunks.FirstOrDefault(x => x.Tag == "Data");
                string dataChildren = "(no Data block)";
                int? distanceBytes = null;
                int? distanceRandomBytes = null;
                string distanceSummary = "not present";
                string distanceRandomSummary = "not present";
                if (dataChunk.Size >= 0 && dataChunk.PayloadOffset > 0
                    && TryReadChunks(data, dataChunk.PayloadOffset, dataChunk.Size, out var dataChunks, out _))
                {
                    dataChildren = dataChunks.Count == 0 ? "(empty Data block)" : string.Join(", ", dataChunks.Select(x => x.Tag));
                    var distance = dataChunks.FirstOrDefault(x => x.Tag == "Dst");
                    var distanceRandom = dataChunks.FirstOrDefault(x => x.Tag == "DstR");
                    if (distance.Size > 0)
                    {
                        distanceBytes = distance.Size;
                        distanceSummary = DescribeCurve(data, distance);
                    }
                    if (distanceRandom.Size > 0)
                    {
                        distanceRandomBytes = distanceRandom.Size;
                        distanceRandomSummary = DescribeCurve(data, distanceRandom);
                    }
                }

                var propertyParts = new List<string>();
                foreach (string propertyTag in new[] { "PrpS", "Prp1", "Prp2", "PrpG" })
                {
                    var propertyChunk = binderChunks.FirstOrDefault(x => x.Tag == propertyTag);
                    if (propertyChunk.Size <= 0 || propertyChunk.PayloadOffset <= 0) continue;
                    if (TryReadChunks(data, propertyChunk.PayloadOffset, propertyChunk.Size, out var propertyChunks, out _))
                    {
                        int? bindPointId = ReadInt(data, propertyChunks, "BPID");
                        int? bindPointType = ReadInt(data, propertyChunks, "BPT");
                        int? targetPointType = ReadInt(data, propertyChunks, "BPTP");
                        if (bindPointId.HasValue || bindPointType.HasValue || targetPointType.HasValue)
                            propertyParts.Add($"{propertyTag}: BPID={bindPointId?.ToString(CultureInfo.InvariantCulture) ?? "?"}, BPT={bindPointType?.ToString(CultureInfo.InvariantCulture) ?? "?"}, BPTP={targetPointType?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
                    }
                }

                int index = binders.Count;
                binders.Add(new BinderInfo
                {
                    Index = index,
                    TypeValue = typeValue,
                    TypeName = GetBinderTypeName(typeValue),
                    RotationValue = rotationValue,
                    RotationName = GetBinderRotationName(rotationValue),
                    FollowingTargetOrientation = ReadBool(data, binderChunks, "bFTO"),
                    AdjustToScreen = ReadBool(data, binderChunks, "bATS"),
                    VfxScaleEnabled = ReadBool(data, binderChunks, "bVSc"),
                    StartToGlobalDirection = ReadBool(data, binderChunks, "bStG"),
                    Life = ReadInt(data, binderChunks, "Life"),
                    CameraDistanceCurveBytes = distanceBytes,
                    CameraDistanceRandomCurveBytes = distanceRandomBytes,
                    CameraDistanceCurveSummary = distanceSummary,
                    CameraDistanceRandomCurveSummary = distanceRandomSummary,
                    DataChildren = dataChildren,
                    PropertySummary = propertyParts.Count == 0 ? "No decoded point properties" : string.Join("; ", propertyParts),
                });
            }

            var timelines = new List<TimelineInfo>();
            foreach (var timelineChunk in mainChunks.Where(x => x.Tag == "TmLn"))
            {
                if (!TryReadChunks(data, timelineChunk.PayloadOffset, timelineChunk.Size, out var timelineChunks, out string timelineError))
                    return Failure($"Timeline #{timelines.Count} could not be parsed: {timelineError}", data.Length);

                timelines.Add(new TimelineInfo
                {
                    Index = timelines.Count,
                    BinderIndex = ReadInt(data, timelineChunks, "BnNo"),
                });
            }

            int cameraBinderCount = binders.Count(x => x.TypeValue == 3);
            bool countMatches = declaredBinderCount.HasValue && declaredBinderCount.Value == binders.Count;
            bool timelineReferencesMatch = timelines.Count > 0
                && timelines.All(x => x.BinderIndex.HasValue && x.BinderIndex.Value >= 0 && x.BinderIndex.Value < binders.Count);
            string report = BuildReport(data.Length, root.Size, declaredBinderCount, binders, timelines, cameraSpace, drawLayer, countMatches);
            return new Result
            {
                Success = true,
                FileBytes = data.Length,
                RootPayloadBytes = root.Size,
                DeclaredBinderCount = declaredBinderCount,
                BinderCount = binders.Count,
                CameraBinderCount = cameraBinderCount,
                BinderCountMatchesDeclaration = countMatches,
                CameraSpace = cameraSpace,
                DrawLayerValue = drawLayer,
                DrawLayerName = GetDrawLayerName(drawLayer),
                Binders = binders,
                Timelines = timelines,
                TimelineBinderReferencesMatch = timelineReferencesMatch,
                Report = report,
            };
        }
        catch (Exception ex)
        {
            return Failure("AVFX inspection failed safely: " + ex.Message, data.Length);
        }
    }

    /// <summary>
    /// Appends a byte-for-byte Camera Bind block from a known-good template to a zero-binder
    /// AVFX, places it in the canonical Bind section, repairs BdCn, and assigns it to every
    /// timeline through TmLn.BnNo. Unknown template binder payload fields remain untouched.
    /// </summary>
    public static bool TryAppendCameraBinderFromTemplate(
        byte[]? targetSource,
        byte[]? templateSource,
        int templateBinderIndex,
        out byte[] rewritten,
        out int previousBinderCount,
        out string error)
    {
        rewritten = Array.Empty<byte>();
        previousBinderCount = 0;
        error = string.Empty;
        try
        {
            var targetInspection = Inspect(targetSource);
            if (!targetInspection.Success)
            {
                error = "Target AVFX inspection failed: " + targetInspection.Error;
                return false;
            }
            if (targetInspection.BinderCount != 0)
            {
                error = $"Automatic Camera-binder injection supports only zero-binder AVFX files; the target contains {targetInspection.BinderCount} existing binder(s). Existing actor/world binders are not rewritten.";
                return false;
            }

            var templateInspection = Inspect(templateSource);
            if (!templateInspection.Success)
            {
                error = "Template AVFX inspection failed: " + templateInspection.Error;
                return false;
            }
            if (templateBinderIndex < 0 || templateBinderIndex >= templateInspection.Binders.Count
                || templateInspection.Binders[templateBinderIndex].TypeValue != 3)
            {
                error = $"Template binder #{templateBinderIndex} is not a valid Camera (BnVr=3) binder.";
                return false;
            }
            if (targetInspection.Timelines.Count == 0)
            {
                error = "Target AVFX has no TmLn timeline blocks. A Camera binder cannot be assigned through TmLn.BnNo.";
                return false;
            }

            if (!TryReadChunk(targetSource!, 0, targetSource!.Length, out var targetRoot, out error)
                || !TryReadChunks(targetSource, targetRoot.PayloadOffset, targetRoot.Size, out var targetRootChunks, out error))
            {
                error = "Target AVFX chunk structure is invalid: " + error;
                return false;
            }
            if (!TryReadChunk(templateSource!, 0, templateSource!.Length, out var templateRoot, out error)
                || !TryReadChunks(templateSource, templateRoot.PayloadOffset, templateRoot.Size, out var templateRootChunks, out error))
            {
                error = "Template AVFX chunk structure is invalid: " + error;
                return false;
            }

            var countChunks = targetRootChunks.Where(x => x.Tag == "BdCn").ToArray();
            if (countChunks.Length > 1 || (countChunks.Length == 1 && countChunks[0].Size != sizeof(int)))
            {
                error = "Target AVFX contains an ambiguous or malformed BdCn binder-count field.";
                return false;
            }

            var targetBinders = targetRootChunks.Where(x => x.Tag == "Bind").ToArray();
            var templateBinders = templateRootChunks.Where(x => x.Tag == "Bind").ToArray();
            if (targetInspection.BinderCount != targetBinders.Length || templateInspection.BinderCount != templateBinders.Length)
            {
                error = "At least one Bind block could not be parsed safely. Refusing to rewrite an ambiguous binder table.";
                return false;
            }

            previousBinderCount = targetBinders.Length;
            if (previousBinderCount != 0)
            {
                error = "The target acquired an existing binder during inspection. Refusing automatic Camera-binder injection.";
                return false;
            }
            int newBinderIndex = previousBinderCount;
            var templateBinder = templateBinders[templateBinderIndex];
            int rawLength = HeaderSize + templateBinder.Size;
            int paddedLength = rawLength + ((4 - (templateBinder.Size & 3)) & 3);
            if (targetRoot.Size > int.MaxValue - paddedLength || targetSource.Length > int.MaxValue - paddedLength)
            {
                error = "Adding the Camera binder would exceed the supported AVFX size range.";
                return false;
            }

            int insertionOffset = DetermineBinderInsertionOffset(targetRoot, targetRootChunks, targetBinders);
            if (insertionOffset < targetRoot.PayloadOffset || insertionOffset > targetRoot.PayloadEnd)
            {
                error = "Could not determine a safe canonical insertion point for the Camera binder.";
                return false;
            }

            var binderBytes = new byte[paddedLength];
            Buffer.BlockCopy(templateSource!, templateBinder.PayloadOffset - HeaderSize, binderBytes, 0, rawLength);
            rewritten = InsertBytes(targetSource!, insertionOffset, binderBytes);
            IncreaseRootSize(rewritten, paddedLength);

            if (!TryReadChunk(rewritten, 0, rewritten.Length, out var rootAfterBind, out error)
                || !TryReadChunks(rewritten, rootAfterBind.PayloadOffset, rootAfterBind.Size, out var rootChunksAfterBind, out error))
            {
                rewritten = Array.Empty<byte>();
                error = "AVFX could not be reparsed after inserting the Camera binder: " + error;
                return false;
            }

            var updatedCountChunks = rootChunksAfterBind.Where(x => x.Tag == "BdCn").ToArray();
            if (updatedCountChunks.Length > 1 || (updatedCountChunks.Length == 1 && updatedCountChunks[0].Size != sizeof(int)))
            {
                rewritten = Array.Empty<byte>();
                error = "BdCn became ambiguous or malformed after binder insertion.";
                return false;
            }
            if (updatedCountChunks.Length == 1)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    rewritten.AsSpan(updatedCountChunks[0].PayloadOffset, sizeof(int)), newBinderIndex + 1);
            }
            else
            {
                int countOffset = DetermineBinderCountInsertionOffset(rootAfterBind, rootChunksAfterBind);
                var countBytes = CreateIntLeafChunk("BdCn", newBinderIndex + 1);
                rewritten = InsertBytes(rewritten, countOffset, countBytes);
                IncreaseRootSize(rewritten, countBytes.Length);
            }

            if (!TryAssignTimelineBinderIndex(rewritten, newBinderIndex,
                    out byte[] withTimelineLinks, out int linkedTimelines, out int insertedTimelineFields, out error))
            {
                rewritten = Array.Empty<byte>();
                error = "Camera binder was inserted, but timeline assignment failed: " + error;
                return false;
            }
            rewritten = withTimelineLinks;

            var after = Inspect(rewritten);
            bool allTimelinesLinked = after.Success && after.Timelines.Count == targetInspection.Timelines.Count
                && after.Timelines.All(t => t.BinderIndex == newBinderIndex);
            bool rootOrderValid = HasCanonicalBinderSectionOrder(rewritten, out string rootOrderError);
            if (!after.Success || after.BinderCount != newBinderIndex + 1
                || after.CameraBinderCount < 1 || after.Binders[^1].TypeValue != 3
                || after.DeclaredBinderCount != after.BinderCount || !after.BinderCountMatchesDeclaration
                || linkedTimelines != targetInspection.Timelines.Count || !allTimelinesLinked || !rootOrderValid)
            {
                rewritten = Array.Empty<byte>();
                error = "The generated AVFX failed post-write validation: " + (after.Success
                    ? $"binders={after.BinderCount}, declared={after.DeclaredBinderCount}, camera={after.CameraBinderCount}, timelines={after.Timelines.Count}, all linked={allTimelinesLinked}, root order={rootOrderValid}{(rootOrderValid ? string.Empty : " (" + rootOrderError + ")")}."
                    : after.Error);
                return false;
            }

            error = $"Appended Camera binder #{newBinderIndex}; linked all {linkedTimelines} timeline(s) through TmLn.BnNo ({insertedTimelineFields} missing fields created). The target contained no original binders; BdCn and root section order validated.";
            return true;
        }
        catch (Exception ex)
        {
            rewritten = Array.Empty<byte>();
            previousBinderCount = 0;
            error = "Camera-binder injection failed safely: " + ex.Message;
            return false;
        }
    }

    private static bool HasCanonicalBinderSectionOrder(byte[] data, out string error)
    {
        error = string.Empty;
        if (!TryReadChunk(data, 0, data.Length, out var root, out error)
            || !TryReadChunks(data, root.PayloadOffset, root.Size, out var chunks, out error))
            return false;

        int binderCountField = chunks.FindIndex(x => x.Tag == "BdCn");
        if (binderCountField < 0)
        {
            error = "No BdCn binder-count field was found after rewriting.";
            return false;
        }
        int lastPriorCountField = Math.Max(
            Math.Max(chunks.FindLastIndex(x => x.Tag == "ScCn"), chunks.FindLastIndex(x => x.Tag == "TlCn")),
            Math.Max(Math.Max(chunks.FindLastIndex(x => x.Tag == "EmCn"), chunks.FindLastIndex(x => x.Tag == "PrCn")),
                chunks.FindLastIndex(x => x.Tag == "EfCn")));
        int textureCountField = chunks.FindIndex(x => x.Tag == "TxCn");
        int modelCountField = chunks.FindIndex(x => x.Tag == "MdCn");
        int firstDataSection = chunks.FindIndex(x => x.Tag is "Schd" or "TmLn" or "Emit" or "Ptcl" or "Efct" or "Bind" or "Tex" or "Modl");
        if ((lastPriorCountField >= 0 && binderCountField <= lastPriorCountField)
            || (textureCountField >= 0 && binderCountField >= textureCountField)
            || (modelCountField >= 0 && binderCountField >= modelCountField)
            || (firstDataSection >= 0 && binderCountField >= firstDataSection))
        {
            error = "BdCn is outside the AVFX root count-field section (expected after earlier counts and before TxCn/MdCn and all data blocks).";
            return false;
        }

        int firstBinder = chunks.FindIndex(x => x.Tag == "Bind");
        if (firstBinder < 0)
        {
            error = "No Bind section was found after rewriting.";
            return false;
        }

        int lastEffector = chunks.FindLastIndex(x => x.Tag == "Efct");
        int firstTextureOrModel = chunks.FindIndex(x => x.Tag is "Tex" or "Modl");
        if (lastEffector >= 0 && firstBinder < lastEffector)
        {
            error = "Bind appears before the final Efct section.";
            return false;
        }
        if (firstTextureOrModel >= 0 && firstBinder > firstTextureOrModel)
        {
            error = "Bind appears after a Tex or Modl section.";
            return false;
        }
        if (chunks.Skip(firstBinder + 1).Any(x => x.Tag == "Efct"))
        {
            error = "An Efct section appears after Bind.";
            return false;
        }
        return true;
    }

    private static int DetermineBinderInsertionOffset(Chunk root, List<Chunk> rootChunks, Chunk[] existingBinders)
    {
        if (existingBinders.Length > 0)
            return GetAlignedChunkEnd(existingBinders[^1], root.PayloadEnd);

        // AVFX root section order is scheduler, timelines, emitters, particles, effectors,
        // binders, textures, models. Insert before the first section that follows Bind.
        int firstLater = rootChunks.FindIndex(x => x.Tag is "Tex" or "Modl");
        if (firstLater >= 0)
            return rootChunks[firstLater].PayloadOffset - HeaderSize;

        foreach (string tag in new[] { "Efct", "Ptcl", "Emit", "TmLn", "Schd" })
        {
            var previous = rootChunks.LastOrDefault(x => x.Tag == tag);
            if (previous.PayloadOffset > 0)
                return GetAlignedChunkEnd(previous, root.PayloadEnd);
        }

        return root.PayloadEnd;
    }

    private static int DetermineBinderCountInsertionOffset(Chunk root, List<Chunk> rootChunks)
    {
        var effectorCount = rootChunks.FirstOrDefault(x => x.Tag == "EfCn");
        if (effectorCount.PayloadOffset > 0)
            return GetAlignedChunkEnd(effectorCount, root.PayloadEnd);

        foreach (string tag in new[] { "TxCn", "MdCn" })
        {
            var nextCount = rootChunks.FirstOrDefault(x => x.Tag == tag);
            if (nextCount.PayloadOffset > 0)
                return nextCount.PayloadOffset - HeaderSize;
        }

        int firstData = rootChunks.FindIndex(x => x.Tag is "Schd" or "TmLn" or "Emit" or "Ptcl" or "Efct" or "Bind" or "Tex" or "Modl");
        if (firstData >= 0)
            return rootChunks[firstData].PayloadOffset - HeaderSize;

        var version = rootChunks.FirstOrDefault(x => x.Tag == "Ver");
        return version.PayloadOffset > 0 ? GetAlignedChunkEnd(version, root.PayloadEnd) : root.PayloadEnd;
    }

    private static bool TryAssignTimelineBinderIndex(
        byte[] source,
        int binderIndex,
        out byte[] rewritten,
        out int linkedTimelines,
        out int insertedFields,
        out string error)
    {
        rewritten = (byte[])source.Clone();
        linkedTimelines = 0;
        insertedFields = 0;
        error = string.Empty;

        if (!TryReadChunk(rewritten, 0, rewritten.Length, out var root, out error)
            || !TryReadChunks(rewritten, root.PayloadOffset, root.Size, out var rootChunks, out error))
            return false;

        var timelines = new List<(Chunk Timeline, int InsertOffset, bool HasBinderField)>();
        int timelineIndex = 0;
        foreach (var timeline in rootChunks.Where(x => x.Tag == "TmLn"))
        {
            if (!TryReadChunks(rewritten, timeline.PayloadOffset, timeline.Size, out var fields, out string parseError))
            {
                error = $"Timeline #{timelineIndex} could not be parsed: {parseError}";
                return false;
            }

            var binderFields = fields.Where(x => x.Tag == "BnNo").ToArray();
            if (binderFields.Length > 1 || (binderFields.Length == 1 && binderFields[0].Size != sizeof(int)))
            {
                error = $"Timeline #{timelineIndex} has duplicate or malformed BnNo fields.";
                return false;
            }

            int binderFieldPosition = binderFields.Length == 1
                ? fields.FindIndex(x => x.Tag == "BnNo")
                : -1;
            int loopStartPosition = fields.FindIndex(x => x.Tag == "LpSt");
            int loopEndPosition = fields.FindIndex(x => x.Tag == "LpEd");
            int firstCountPosition = fields.FindIndex(x => x.Tag is "TICn" or "CpCn");
            int firstNestedPosition = fields.FindIndex(x => x.Tag is "Item" or "Clip");
            if (binderFields.Length == 1)
            {
                if ((loopStartPosition >= 0 && loopEndPosition >= 0 && loopStartPosition > loopEndPosition)
                    || (loopStartPosition >= 0 && binderFieldPosition < loopStartPosition)
                    || (loopEndPosition >= 0 && binderFieldPosition < loopEndPosition)
                    || (firstCountPosition >= 0 && binderFieldPosition > firstCountPosition)
                    || (firstNestedPosition >= 0 && binderFieldPosition > firstNestedPosition))
                {
                    error = $"Timeline #{timelineIndex} has BnNo outside the expected header order (LpSt, LpEd, BnNo, TICn, CpCn); refusing to rewrite a non-canonical timeline header.";
                    return false;
                }

                timelines.Add((timeline, 0, true));
                BinaryPrimitives.WriteInt32LittleEndian(rewritten.AsSpan(binderFields[0].PayloadOffset, sizeof(int)), binderIndex);
            }
            else
            {
                var loopEnd = fields.LastOrDefault(x => x.Tag == "LpEd");
                var loopStart = fields.LastOrDefault(x => x.Tag == "LpSt");
                var itemCount = fields.FirstOrDefault(x => x.Tag == "TICn");
                int insertion = loopEnd.PayloadOffset > 0
                    ? GetAlignedChunkEnd(loopEnd, timeline.PayloadEnd)
                    : loopStart.PayloadOffset > 0
                        ? GetAlignedChunkEnd(loopStart, timeline.PayloadEnd)
                        : itemCount.PayloadOffset > 0
                            ? itemCount.PayloadOffset - HeaderSize
                            : timeline.PayloadOffset;
                if (firstNestedPosition >= 0)
                {
                    var firstNested = fields[firstNestedPosition];
                    int firstNestedOffset = firstNested.PayloadOffset - HeaderSize;
                    if (insertion > firstNestedOffset)
                        insertion = firstNestedOffset;
                }
                timelines.Add((timeline, insertion, false));
            }

            timelineIndex++;
        }

        if (timelines.Count == 0)
        {
            error = "No TmLn timeline blocks were found to link to the Camera binder.";
            return false;
        }

        foreach (var item in timelines.Where(x => !x.HasBinderField).OrderByDescending(x => x.Timeline.PayloadOffset))
        {
            byte[] binderIndexChunk = CreateIntLeafChunk("BnNo", binderIndex);
            rewritten = InsertBytes(rewritten, item.InsertOffset, binderIndexChunk);
            int timelineHeaderOffset = item.Timeline.PayloadOffset - HeaderSize;
            int timelineSize = BinaryPrimitives.ReadInt32LittleEndian(rewritten.AsSpan(timelineHeaderOffset + 4, sizeof(int)));
            BinaryPrimitives.WriteInt32LittleEndian(rewritten.AsSpan(timelineHeaderOffset + 4, sizeof(int)), timelineSize + binderIndexChunk.Length);
            IncreaseRootSize(rewritten, binderIndexChunk.Length);
            insertedFields++;
        }

        linkedTimelines = timelines.Count;
        var validation = Inspect(rewritten);
        if (!validation.Success || validation.Timelines.Count != timelineIndex
            || validation.Timelines.Any(x => x.BinderIndex != binderIndex))
        {
            error = validation.Success
                ? "Post-write timeline validation found a missing or incorrect BnNo reference."
                : validation.Error;
            return false;
        }

        return true;
    }

    private static int GetAlignedChunkEnd(Chunk chunk, int parentEnd)
    {
        int paddedEnd = chunk.PayloadEnd + ((4 - (chunk.Size & 3)) & 3);
        return paddedEnd <= parentEnd ? paddedEnd : chunk.PayloadEnd;
    }

    private static byte[] CreateIntLeafChunk(string tag, int value)
    {
        var chunk = new byte[HeaderSize + sizeof(int)];
        Encoding.ASCII.GetBytes(Reverse(tag)).CopyTo(chunk, 0);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4, sizeof(int)), sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(HeaderSize, sizeof(int)), value);
        return chunk;
    }

    private static byte[] InsertBytes(byte[] source, int offset, byte[] inserted)
    {
        if (offset < 0 || offset > source.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (inserted.Length > int.MaxValue - source.Length)
            throw new InvalidOperationException("The rewritten AVFX would exceed the supported byte-array size.");
        var result = new byte[source.Length + inserted.Length];
        Buffer.BlockCopy(source, 0, result, 0, offset);
        Buffer.BlockCopy(inserted, 0, result, offset, inserted.Length);
        Buffer.BlockCopy(source, offset, result, offset + inserted.Length, source.Length - offset);
        return result;
    }

    private static void IncreaseRootSize(byte[] data, int amount)
    {
        int current = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, sizeof(int)));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, sizeof(int)), checked(current + amount));
    }

    private static Result Failure(string error, int bytes) => new()
    {
        Success = false,
        Error = error,
        FileBytes = bytes,
        Report = "AVFX binder inspection failed\n" + error,
    };

    private static string BuildReport(int fileBytes, int rootBytes, int? declaredCount, List<BinderInfo> binders, List<TimelineInfo> timelines, bool? cameraSpace, int? drawLayer, bool countMatches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AVFX binder inspection");
        sb.AppendLine($"File bytes: {fileBytes:N0}");
        sb.AppendLine($"AVFX root payload: {rootBytes:N0} bytes");
        sb.AppendLine($"Declared binder count (BdCn): {declaredCount?.ToString(CultureInfo.InvariantCulture) ?? "not present"}");
        sb.AppendLine($"Parsed Bind blocks: {binders.Count}");
        sb.AppendLine($"Camera binders (BnVr=3): {binders.Count(x => x.TypeValue == 3)}");
        sb.AppendLine($"Timelines (TmLn): {timelines.Count}");
        string countStatus = !declaredCount.HasValue
            ? "MISSING — diagnostic only; Camera conversion does not require an existing Camera binder"
            : countMatches
                ? "matches the parsed Bind block count"
                : "MISMATCH — diagnostic only; template injection rewrites BdCn from the parsed table";
        sb.AppendLine($"Declared count validation: {countStatus}");
        sb.AppendLine($"AVFX Camera Space (bCmS): {FormatBool(cameraSpace)}");
        sb.AppendLine($"Draw Layer (DwLy): {drawLayer?.ToString(CultureInfo.InvariantCulture) ?? "not present"} · {GetDrawLayerName(drawLayer)}");
        sb.AppendLine("Note: bCmS and DwLy are AVFX-wide rendering settings; they are not the same thing as a Binder whose BnVr type is Camera.");
        for (int i = 0; i < timelines.Count; i++)
        {
            int? binderIndex = timelines[i].BinderIndex;
            string linked = binderIndex.HasValue && binderIndex.Value >= 0 && binderIndex.Value < binders.Count
                ? $"Binder #{binderIndex.Value} ({binders[binderIndex.Value].TypeName})"
                : binderIndex == -1 ? "none (-1)" : "missing / out of range";
            sb.AppendLine($"Timeline #{i}: BnNo={binderIndex?.ToString(CultureInfo.InvariantCulture) ?? "missing"} · {linked}");
        }

        for (int i = 0; i < binders.Count; i++)
        {
            var binder = binders[i];
            sb.AppendLine();
            sb.AppendLine($"Binder #{i}: {binder.TypeName} (BnVr={binder.TypeValue?.ToString(CultureInfo.InvariantCulture) ?? "missing"})");
            sb.AppendLine($"  Rotation: {binder.RotationName} (RoTp={binder.RotationValue?.ToString(CultureInfo.InvariantCulture) ?? "missing"})");
            sb.AppendLine($"  Following Target Orientation (bFTO): {FormatBool(binder.FollowingTargetOrientation)}");
            sb.AppendLine($"  Adjust to Screen (bATS): {FormatBool(binder.AdjustToScreen)}");
            sb.AppendLine($"  VFX Scale (bVSc): {FormatBool(binder.VfxScaleEnabled)}");
            sb.AppendLine($"  Start to Global Direction (bStG): {FormatBool(binder.StartToGlobalDirection)}");
            sb.AppendLine($"  Life: {binder.Life?.ToString(CultureInfo.InvariantCulture) ?? "not present"}");
            if (binder.TypeValue == 3)
            {
                sb.AppendLine($"  Camera Data children: {binder.DataChildren}");
                sb.AppendLine($"  Distance curve (Dst): {(binder.CameraDistanceCurveBytes.HasValue ? binder.CameraDistanceCurveBytes.Value + " bytes" : "not present / empty")} · {binder.CameraDistanceCurveSummary}");
                sb.AppendLine($"  Distance Random curve (DstR): {(binder.CameraDistanceRandomCurveBytes.HasValue ? binder.CameraDistanceRandomCurveBytes.Value + " bytes" : "not present / empty")} · {binder.CameraDistanceRandomCurveSummary}");
            }
            else
                sb.AppendLine($"  Data children: {binder.DataChildren}");
            sb.AppendLine($"  Properties: {binder.PropertySummary}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Decodes only the stable AVFX 1-axis curve key envelope used by Dst/DstR:
    /// Int16 time, UInt16 key type, then three Float32 values (16 bytes per key).
    /// The Z component is the displayed/evaluated scalar value in VFXEditor.
    /// </summary>
    private static string DescribeCurve(byte[] data, Chunk curve)
    {
        if (!TryReadChunks(data, curve.PayloadOffset, curve.Size, out var curveChunks, out string error))
            return "curve parse failed: " + error;

        int? declaredKeys = ReadInt(data, curveChunks, "KeyC");
        var keysChunk = curveChunks.FirstOrDefault(x => x.Tag == "Keys");
        if (keysChunk.Size <= 0 || keysChunk.PayloadOffset <= 0)
            return $"KeyC={declaredKeys?.ToString(CultureInfo.InvariantCulture) ?? "?"}, Keys block absent";

        int decodedKeys = keysChunk.Size / 16;
        int trailingBytes = keysChunk.Size % 16;
        var samples = new List<string>();
        int sampleCount = Math.Min(decodedKeys, 6);
        for (int i = 0; i < sampleCount; i++)
        {
            int keyOffset = keysChunk.PayloadOffset + (i * 16);
            short time = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(keyOffset, 2));
            ushort keyType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(keyOffset + 2, 2));
            float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(keyOffset + 12, 4)));
            samples.Add(float.IsFinite(value)
                ? $"t={time}, type={keyType}, value={value.ToString("0.###", CultureInfo.InvariantCulture)}"
                : $"t={time}, type={keyType}, value=non-finite");
        }

        string mismatch = declaredKeys.HasValue && declaredKeys.Value != decodedKeys
            ? $", KeyC mismatch ({declaredKeys.Value} declared / {decodedKeys} decoded)"
            : string.Empty;
        string tail = trailingBytes == 0 ? string.Empty : $", {trailingBytes} trailing byte(s)";
        string sampleText = samples.Count == 0 ? "no keys" : string.Join("; ", samples);
        if (decodedKeys > sampleCount) sampleText += $"; … +{decodedKeys - sampleCount} more";
        return $"KeyC={declaredKeys?.ToString(CultureInfo.InvariantCulture) ?? "?"}, decoded={decodedKeys}{mismatch}{tail}; {sampleText}";
    }

    private static bool TryReadChunk(byte[] data, int offset, int end, out Chunk chunk, out string error)
    {
        chunk = default;
        error = "";
        if (offset < 0 || end > data.Length || offset > end - HeaderSize)
        {
            error = "Block header is outside the file bounds.";
            return false;
        }

        string rawTag = Encoding.ASCII.GetString(data, offset, 4);
        int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
        if (size < 0 || size > end - offset - HeaderSize)
        {
            error = $"Block '{rawTag}' declares invalid size {size}.";
            return false;
        }

        chunk = new Chunk(NormalizeTag(rawTag), rawTag, offset + HeaderSize, size);
        return true;
    }

    private static bool TryReadChunks(byte[] data, int offset, int size, out List<Chunk> chunks, out string error)
    {
        chunks = new List<Chunk>();
        error = "";
        if (offset < 0 || size < 0 || offset > data.Length - size)
        {
            error = "Nested block range is outside the file bounds.";
            return false;
        }

        int end = offset + size;
        int position = offset;
        while (position < end)
        {
            int remaining = end - position;
            if (remaining < HeaderSize)
            {
                if (IsZeroPadding(data, position, end)) return true;
                error = $"Unexpected {remaining}-byte tail at offset 0x{position:X}.";
                return false;
            }

            if (IsZeroPadding(data, position, end))
                return true;

            if (chunks.Count >= MaximumChunks)
            {
                error = "Chunk count safety limit exceeded.";
                return false;
            }

            if (!TryReadChunk(data, position, end, out var chunk, out error))
                return false;

            chunks.Add(chunk);
            int payloadEnd = chunk.PayloadEnd;
            int padding = (4 - (chunk.Size & 3)) & 3;
            long next = (long)payloadEnd + padding;
            if (next > end)
            {
                // Some valid files omit final alignment bytes at the very end of a nested stream.
                if (payloadEnd == end) return true;
                error = $"Padding for '{chunk.RawTag}' extends beyond its parent block.";
                return false;
            }
            position = (int)next;
        }
        return true;
    }

    private static bool IsZeroPadding(byte[] data, int start, int end)
    {
        for (int i = start; i < end; i++)
            if (data[i] != 0) return false;
        return true;
    }

    private static string NormalizeTag(string raw)
    {
        string trimmed = raw.Trim('\0', ' ');
        if (KnownTags.Contains(trimmed)) return trimmed;
        string reversed = Reverse(raw).Trim('\0', ' ');
        return KnownTags.Contains(reversed) ? reversed : trimmed;
    }

    private static string Reverse(string value)
    {
        var chars = value.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static int? ReadInt(byte[] data, List<Chunk> chunks, string tag)
    {
        var chunk = chunks.FirstOrDefault(x => x.Tag == tag);
        return chunk.Size == 4 ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(chunk.PayloadOffset, 4)) : null;
    }

    private static bool? ReadBool(byte[] data, List<Chunk> chunks, string tag)
    {
        int? value = ReadInt(data, chunks, tag);
        return value switch { 0 => false, 1 => true, _ => null };
    }

    private static string FormatBool(bool? value) => value.HasValue ? (value.Value ? "true" : "false") : "not present / unknown";

    private static string GetBinderTypeName(int? value) => value switch
    {
        0 => "Point",
        1 => "Linear",
        2 => "Spline",
        3 => "Camera",
        4 => "LinearAdjust",
        null => "Missing / unknown",
        _ => "Unknown(" + value.Value.ToString(CultureInfo.InvariantCulture) + ")",
    };

    private static string GetBinderRotationName(int? value) => value switch
    {
        0 => "Standard",
        1 => "Billboard",
        2 => "BillboardAxisY",
        3 => "LookAtCamera",
        4 => "CameraBillboardAxisY",
        null => "Not present / unknown",
        _ => "Unknown(" + value.Value.ToString(CultureInfo.InvariantCulture) + ")",
    };

    private static string GetDrawLayerName(int? value) => value switch
    {
        0 => "Screen",
        1 => "BaseUpper",
        2 => "Base",
        3 => "BaseLower",
        4 => "InWater",
        5 => "BeforeCloud",
        6 => "BehindCloud",
        7 => "BeforeSky",
        8 => "PostUI",
        9 => "PrevUI",
        10 => "FitWater",
        null => "Not present / unknown",
        _ => "Unknown(" + value.Value.ToString(CultureInfo.InvariantCulture) + ")",
    };
}

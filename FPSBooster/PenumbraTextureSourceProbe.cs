using System;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Dalamud.Plugin;
using Lumina.Data.Files;

namespace FPSBooster;

/// <summary>
/// Manually resolves a game texture path through Penumbra for the local player's effective
/// collection, then inspects only the resolved file's small metadata/header region.
/// It never changes Penumbra settings or accesses game-owned GPU resources.
/// </summary>
public sealed unsafe class PenumbraTextureSourceProbe
{
    private const int TexHeaderSize = 80;
    private const int MaxType4HeaderSize = 1024 * 1024;
    private const uint SqPackType4 = 4;
    private readonly IDalamudPluginInterface _pluginInterface;

    public PenumbraTextureSourceProbe(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
    }

    public PenumbraTextureSourceSnapshot? Result { get; private set; }
    public string Status { get; private set; } = "No Penumbra path resolution performed.";
    public string? Error { get; private set; }

    public void Resolve(string gamePath)
    {
        Clear();

        if (string.IsNullOrWhiteSpace(gamePath) || !gamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a game .tex path before resolving its Penumbra source.", nameof(gamePath));

        var stopwatch = Stopwatch.StartNew();
        string? resolvedPath;
        try
        {
            // Penumbra's public ResolvePlayerPath IPC resolves against the local player's active
            // collection. The returned string is the effective supplying path reported by Penumbra.
            var resolvePlayerPath = _pluginInterface.GetIpcSubscriber<string, string>("Penumbra.ResolvePlayerPath");
            resolvedPath = resolvePlayerPath.InvokeFunc(gamePath);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Error = ex.Message;
            Status = "Penumbra path resolution failed. Check that Penumbra is installed, initialized, and exposing its IPC API.";
            return;
        }

        stopwatch.Stop();
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            Status = "Penumbra returned no resolved file path for the local player's collection. This can mean no replacement is active, the path is not player-scoped, or Penumbra could not resolve it.";
            Result = new PenumbraTextureSourceSnapshot
            {
                GamePath = gamePath,
                ResolveMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                IsResolved = false,
            };
            return;
        }

        var snapshot = new PenumbraTextureSourceSnapshot
        {
            GamePath = gamePath,
            ResolvedPath = resolvedPath,
            ResolveMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            IsResolved = true,
        };
        Result = snapshot;

        try
        {
            InspectResolvedFile(snapshot);
            Status = snapshot.FileExists
                ? "Penumbra resolved the game path to a file that exists on disk. Only its file metadata and texture/container header were inspected."
                : "Penumbra returned a resolved path, but it is not directly available as a filesystem file. The path is reported without attempting to decode Penumbra's internal path representation.";
        }
        catch (Exception ex)
        {
            snapshot.FileInspectionError = ex.Message;
            Status = "Penumbra resolved a source path, but its metadata header could not be read.";
        }
    }

    /// <summary>
    /// Inspects a user-supplied absolute loose .tex path directly. This deliberately bypasses
    /// Penumbra IPC: the caller has already provided the actual filesystem path. Type-4 files
    /// may still be identified and have metadata inspected, but their mip payloads remain
    /// unsupported by the direct loose-file reader.
    /// </summary>
    public void InspectFilePath(string filePath)
    {
        Clear();

        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Enter an absolute .tex file path.", nameof(filePath));

            string trimmedPath = filePath.Trim().Trim('"');
            if (!Path.IsPathRooted(trimmedPath))
                throw new ArgumentException("The supplied file path must be absolute. Use a game-relative path to resolve it through Penumbra.", nameof(filePath));
            if (!trimmedPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The supplied file must have a .tex extension.", nameof(filePath));

            string fullPath = Path.GetFullPath(trimmedPath);
            var snapshot = new PenumbraTextureSourceSnapshot
            {
                GamePath = string.Empty,
                ResolvedPath = fullPath,
                ResolveMilliseconds = 0,
                IsResolved = true,
                IsManualFilePath = true,
            };
            Result = snapshot;

            InspectResolvedFile(snapshot);
            Status = snapshot.FileExists
                ? "Inspected the supplied local .tex file directly. Penumbra IPC was not called; only file metadata and texture/container header data were read."
                : "The supplied absolute .tex path does not exist or is not accessible.";
            if (!snapshot.FileExists)
                Error = "File not found or inaccessible: " + fullPath;
            else if (!snapshot.HasValidTextureHeader && string.IsNullOrEmpty(snapshot.FileInspectionError))
                snapshot.FileInspectionError = "The file exists, but its texture header could not be validated.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = "The supplied local .tex path could not be inspected.";
        }
    }

    public void Clear()
    {
        Result = null;
        Error = null;
        Status = "No Penumbra path resolution performed.";
    }

    private static void InspectResolvedFile(PenumbraTextureSourceSnapshot snapshot)
    {
        string path = snapshot.ResolvedPath;
        if (!File.Exists(path))
            return;

        var fileInfo = new FileInfo(path);
        snapshot.FileExists = true;
        snapshot.FileLength = fileInfo.Length;
        snapshot.FileLastWriteUtc = fileInfo.LastWriteTimeUtc;

        // Do not read the full replacement asset. A type-4 texture entry has a bounded metadata
        // header (mip descriptors and shared block-stride table) followed by its 80-byte TexHeader;
        // a loose decompressed texture begins with TexHeader.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < TexHeaderSize)
        {
            snapshot.ContainerDescription = "File is shorter than an 80-byte TexHeader.";
            return;
        }

        int texHeaderOffset = 0;
        if (stream.Length >= 24)
        {
            byte[] entryHeader = new byte[24];
            ReadExactly(stream, entryHeader, 0, entryHeader.Length);
            uint headerSize = BitConverter.ToUInt32(entryHeader, 0);
            uint fileType = BitConverter.ToUInt32(entryHeader, 4);
            uint rawFileSize = BitConverter.ToUInt32(entryHeader, 8);
            uint mipDescriptorCount = BitConverter.ToUInt32(entryHeader, 20);

            if (fileType == SqPackType4)
            {
                long minimumHeaderSize = checked(24L + (long)mipDescriptorCount * 20L);
                if (mipDescriptorCount is 0 or > 16 || headerSize < minimumHeaderSize
                    || headerSize > MaxType4HeaderSize || (long)headerSize + TexHeaderSize > stream.Length)
                {
                    snapshot.ContainerDescription = "SqPack Type-4 signature found, but its header size or mip-descriptor count is invalid.";
                    snapshot.IsType4Container = true;
                    return;
                }

                texHeaderOffset = checked((int)headerSize);
                snapshot.IsType4Container = true;
                snapshot.ContainerDescription = "SqPack Type-4 texture container (header, mip descriptors, and stride table only; mip payloads were not read).";
                snapshot.DeclaredRawFileSize = rawFileSize;
                snapshot.ContainerMipDescriptorCount = checked((int)mipDescriptorCount);
                snapshot.Groups = ReadType4MipDescriptors(stream, headerSize, checked((int)mipDescriptorCount), stream.Length,
                    out long blockStrideTableBytes, out bool blockStrideTableRangeValid,
                    out bool blockStrideTableValuesValid, out string? blockStrideTableValidationError);
                snapshot.BlockStrideTableBytes = blockStrideTableBytes;
                snapshot.BlockStrideTableRangeValid = blockStrideTableRangeValid;
                snapshot.BlockStrideTableValuesValid = blockStrideTableValuesValid;
                snapshot.BlockStrideTableValidationError = blockStrideTableValidationError;
            }
            else
            {
                snapshot.ContainerDescription = "Loose texture file (TexHeader expected at offset 0).";
            }
        }
        else
        {
            snapshot.ContainerDescription = "Loose texture file (TexHeader expected at offset 0).";
        }

        stream.Position = texHeaderOffset;
        byte[] texHeaderBytes = new byte[TexHeaderSize];
        ReadExactly(stream, texHeaderBytes, 0, texHeaderBytes.Length);
        var header = MemoryMarshal.Read<TexFile.TexHeader>(texHeaderBytes.AsSpan());
        int headerMipCount = Math.Clamp(header.MipCount, 0, 13);
        var mipFileOffsets = new uint[headerMipCount];
        for (int mipIndex = 0; mipIndex < headerMipCount; mipIndex++)
        {
            // TexHeader.OffsetToSurface begins at byte 28 and contains absolute file offsets.
            mipFileOffsets[mipIndex] = BinaryPrimitives.ReadUInt32LittleEndian(texHeaderBytes.AsSpan(28 + mipIndex * sizeof(uint), sizeof(uint)));
        }

        // Validate dimensions and mip metadata through Lumina's own slice-size calculation.
        // This also rejects an arbitrary file that merely happens to be named *.tex.
        if (header.Width == 0 || header.Height == 0 || header.MipCount is 0 or > 13)
        {
            snapshot.ContainerDescription += " Header bytes were present but did not contain plausible 2D texture metadata.";
            return;
        }

        var report = TextureFileReport.Create(
            header,
            path,
            metadataReadMilliseconds: 0,
            metadataBytesRequested: TexHeaderSize,
            sqPack: null,
            mipFileOffsets: mipFileOffsets);

        snapshot.TextureHeaderOffset = texHeaderOffset;
        snapshot.HasValidTextureHeader = true;
        snapshot.TextureReport = report;

        if (snapshot.IsType4Container)
        {
            bool mappingValid = snapshot.Groups.Count == report.Mips.Count
                && snapshot.BlockStrideTableRangeValid && snapshot.BlockStrideTableValuesValid;
            long totalExpandedBytes = 0;
            long expectedBlockOffset = 0;
            for (int i = 0; i < snapshot.Groups.Count; i++)
            {
                var group = snapshot.Groups[i];
                totalExpandedBytes = checked(totalExpandedBytes + group.DecompressedSize);
                bool matches = i < report.Mips.Count && group.DecompressedSize == report.Mips[i].Bytes;
                snapshot.Groups[i] = group with { MatchedMipIndex = matches ? i : null };
                if (!matches || !group.IsDataRangeValid || group.BlockOffset != expectedBlockOffset)
                    mappingValid = false;
                expectedBlockOffset = checked(expectedBlockOffset + group.BlockCount);

                // A descriptor may be individually in-bounds yet still alias another mip's
                // compressed bytes. Reject any overlapping pair before enabling selective reads.
                for (int previousIndex = 0; previousIndex < i; previousIndex++)
                {
                    var previous = snapshot.Groups[previousIndex];
                    long groupEnd = checked(group.AbsoluteDataOffset + group.CompressedSize);
                    long previousEnd = checked(previous.AbsoluteDataOffset + previous.CompressedSize);
                    if (group.AbsoluteDataOffset < previousEnd && previous.AbsoluteDataOffset < groupEnd)
                        mappingValid = false;
                }
            }

            long expectedRawSize = checked(TexHeaderSize + report.TotalMipBytes);
            snapshot.DeclaredRawSizeMatchesHeader = snapshot.DeclaredRawFileSize > 0
                && snapshot.DeclaredRawFileSize == expectedRawSize;
            if (snapshot.DeclaredRawSizeMatchesHeader != true)
                mappingValid = false;

            snapshot.HasStrictMipGroupMapping = mappingValid && totalExpandedBytes == report.TotalMipBytes;
            snapshot.MipMappingNote = snapshot.HasStrictMipGroupMapping
                ? "Type-4 groups match every calculated mip payload in order; data ranges do not overlap, block strides sum to each stored group size, the stride table fits inside the header, and the declared raw size matches."
                : "No strict one-to-one Type-4 group-to-mip mapping was established. Do not selectively read these groups until the descriptor/table layout is understood.";
        }
        else
        {
            // OffsetToSurface is the authoritative per-mip byte offset from the beginning
            // of the complete TEX file. The format does not promise that mip payloads are
            // tightly concatenated after the 80-byte header, nor that the final mip ends
            // exactly at EOF. Padding, gaps, and trailing data must not invalidate otherwise
            // readable subresources.
            snapshot.MipPayloadStartOffset = TexHeaderSize;
            bool allMipEntriesAccountedFor = report.Mips.Count == headerMipCount && report.Mips.Count > 0;
            var ranges = new List<(long Start, long End, int Mip)>();
            int validMipCount = 0;
            int absentMipCount = 0;
            int invalidMipCount = 0;

            foreach (var mip in report.Mips)
            {
                long start = mip.SourceFileOffset;
                long end = 0;
                string status;
                string detail;
                bool isReadable = false;

                // Lumina documents OffsetToSurface == 0 as "no corresponding mipmap exists".
                // This is an absent level, not a malformed file range.
                if (start == 0)
                {
                    status = "Absent";
                    detail = "OffsetToSurface is zero; this mip has no stored payload.";
                    absentMipCount++;
                }
                else if (start < TexHeaderSize)
                {
                    status = "Before header";
                    detail = $"Offset 0x{start:X} overlaps the 80-byte TexHeader.";
                    invalidMipCount++;
                }
                else if (mip.Bytes <= 0)
                {
                    status = "Empty";
                    detail = "Calculated mip payload size is zero.";
                    invalidMipCount++;
                }
                else
                {
                    try
                    {
                        end = checked(start + mip.Bytes);
                        if (end > stream.Length)
                        {
                            status = "Past EOF";
                            detail = $"Range ends at 0x{end:X}, beyond file length 0x{stream.Length:X}.";
                            invalidMipCount++;
                        }
                        else
                        {
                            status = "OK";
                            detail = $"Bytes 0x{start:X}..0x{end:X} are within the source file.";
                            isReadable = true;
                            validMipCount++;
                            ranges.Add((start, end, mip.Index));
                        }
                    }
                    catch (OverflowException)
                    {
                        status = "Overflow";
                        detail = "Offset plus calculated mip payload size overflowed the supported range.";
                        invalidMipCount++;
                    }
                }

                snapshot.LooseMipRanges.Add(new PenumbraTextureMipRangeInfo
                {
                    MipIndex = mip.Index,
                    SourceOffset = start,
                    PayloadBytes = mip.Bytes,
                    EndOffset = end,
                    Status = status,
                    Detail = detail,
                    IsReadable = isReadable,
                });
            }

            // Overlapping nonzero ranges are unusual, but keep them visible rather than
            // assuming the table must be a unique, gap-free chain. A single-mip read still
            // uses precisely that mip's explicit header offset.
            ranges.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            snapshot.LooseMipOverlapWarning = null;
            bool hasOverlappingRanges = false;
            for (int i = 1; i < ranges.Count; i++)
            {
                if (ranges[i].Start < ranges[i - 1].End)
                {
                    hasOverlappingRanges = true;
                    snapshot.LooseMipOverlapWarning = $"Mip {ranges[i - 1].Mip} and Mip {ranges[i].Mip} source ranges overlap. Individual reads remain available for in-bounds entries, but the full-chain upload is disabled until the overlap is understood.";
                    break;
                }
            }

            snapshot.LooseMipValidCount = validMipCount;
            snapshot.LooseMipAbsentCount = absentMipCount;
            snapshot.LooseMipInvalidCount = invalidMipCount;
            snapshot.HasAnyValidLooseMipRanges = validMipCount > 0;
            snapshot.HasValidLooseMipRanges = allMipEntriesAccountedFor
                && validMipCount == report.Mips.Count
                && absentMipCount == 0
                && invalidMipCount == 0
                && !hasOverlappingRanges;

            if (invalidMipCount > 0 || absentMipCount > 0)
            {
                var parts = new List<string>();
                if (validMipCount > 0) parts.Add($"{validMipCount} readable");
                if (absentMipCount > 0) parts.Add($"{absentMipCount} absent (OffsetToSurface = 0)");
                if (invalidMipCount > 0) parts.Add($"{invalidMipCount} invalid/out-of-bounds");
                snapshot.LooseMipLayoutValidationError = $"Loose TEX mip table: {string.Join(", ", parts)} of {report.Mips.Count} entries. Readable mips can be tested individually; the full progressive chain is enabled only when every mip has a valid stored range.";
            }
            else if (!snapshot.HasValidLooseMipRanges)
            {
                snapshot.LooseMipLayoutValidationError = "The loose TEX mip table could not be fully validated.";
            }
        }
    }

    private static List<PenumbraTextureMipGroupInfo> ReadType4MipDescriptors(
        Stream stream,
        uint headerSize,
        int mipCount,
        long fileLength,
        out long blockStrideTableBytes,
        out bool blockStrideTableRangeValid,
        out bool blockStrideTableValuesValid,
        out string? blockStrideTableValidationError)
    {
        const int FileHeaderSize = 24;
        const int DescriptorSize = 20;
        var groups = new List<PenumbraTextureMipGroupInfo>(mipCount);
        byte[] descriptors = new byte[checked(mipCount * DescriptorSize)];
        stream.Position = FileHeaderSize;
        ReadExactly(stream, descriptors, 0, descriptors.Length);

        long totalBlocks = 0;
        for (int i = 0; i < mipCount; i++)
        {
            ReadOnlySpan<byte> descriptor = descriptors.AsSpan(i * DescriptorSize, DescriptorSize);
            uint compressedOffset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor);
            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
            uint decompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]);
            uint blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]);
            uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[16..]);

            long absoluteStart = checked((long)headerSize + compressedOffset);
            long absoluteEnd = checked(absoluteStart + compressedSize);
            long texturePayloadBase = checked((long)headerSize + TexHeaderSize);
            bool rangeValid = blockCount > 0 && compressedSize > 0
                && absoluteStart >= texturePayloadBase && absoluteEnd <= fileLength;

            groups.Add(new PenumbraTextureMipGroupInfo
            {
                Index = i,
                CompressedOffset = compressedOffset,
                CompressedSize = compressedSize,
                DecompressedSize = decompressedSize,
                BlockOffset = blockOffset,
                BlockCount = blockCount,
                AbsoluteDataOffset = absoluteStart,
                IsDataRangeValid = rangeValid,
            });

            totalBlocks = checked(totalBlocks + blockCount);
        }

        long tableOffset = checked(FileHeaderSize + (long)mipCount * DescriptorSize);
        blockStrideTableBytes = checked(totalBlocks * sizeof(ushort));
        long tableEnd = checked(tableOffset + blockStrideTableBytes);
        blockStrideTableRangeValid = tableEnd <= headerSize && headerSize <= fileLength && totalBlocks <= int.MaxValue;
        blockStrideTableValuesValid = blockStrideTableRangeValid;
        blockStrideTableValidationError = null;

        if (blockStrideTableRangeValid)
        {
            // Validate the small shared table without reading any mip block payloads. Each
            // signed 16-bit stride must cover a 16-byte block header, and each group's
            // strides must add up to its declared compressed byte range.
            stream.Position = tableOffset;
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                long groupStrideTotal = 0;
                for (uint blockIndex = 0; blockIndex < group.BlockCount; blockIndex++)
                {
                    short stride = reader.ReadInt16();
                    if (stride < 16)
                    {
                        blockStrideTableValuesValid = false;
                        blockStrideTableValidationError = $"Group {groupIndex} has an invalid block stride ({stride}) at entry {blockIndex}.";
                        break;
                    }
                    groupStrideTotal = checked(groupStrideTotal + stride);
                }

                if (!blockStrideTableValuesValid)
                    break;
                if (groupStrideTotal != group.CompressedSize)
                {
                    blockStrideTableValuesValid = false;
                    blockStrideTableValidationError = $"Group {groupIndex} block strides total {groupStrideTotal:N0} bytes, but its descriptor declares {group.CompressedSize:N0} bytes.";
                    break;
                }
            }
        }
        else
        {
            blockStrideTableValidationError = "The shared block-stride table falls outside the declared Type-4 header or is too large to validate.";
        }

        return groups;
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
                throw new EndOfStreamException("The resolved texture file ended before its metadata header was complete.");
            totalRead += read;
        }
    }
}

public sealed class PenumbraTextureSourceSnapshot
{
    public string GamePath { get; init; } = string.Empty;
    /// <summary>True when the user supplied ResolvedPath directly instead of resolving a game path through Penumbra IPC.</summary>
    public bool IsManualFilePath { get; init; }
    public string ResolvedPath { get; init; } = string.Empty;
    public bool IsResolved { get; init; }
    public bool FileExists { get; set; }
    public long FileLength { get; set; }
    public DateTime FileLastWriteUtc { get; set; }
    public double ResolveMilliseconds { get; init; }
    public bool IsType4Container { get; set; }
    public string ContainerDescription { get; set; } = "Not inspected.";
    public uint DeclaredRawFileSize { get; set; }
    public int ContainerMipDescriptorCount { get; set; }
    public int TextureHeaderOffset { get; set; }
    public bool HasValidTextureHeader { get; set; }
    public bool? DeclaredRawSizeMatchesHeader { get; set; }
    /// <summary>True only when every declared mip has a nonzero, in-bounds, non-overlapping source range.</summary>
    public bool HasValidLooseMipRanges { get; set; }
    /// <summary>True when at least one mip has a readable, in-bounds source range.</summary>
    public bool HasAnyValidLooseMipRanges { get; set; }
    public int LooseMipValidCount { get; set; }
    public int LooseMipAbsentCount { get; set; }
    public int LooseMipInvalidCount { get; set; }
    public List<PenumbraTextureMipRangeInfo> LooseMipRanges { get; } = new();
    public string? LooseMipLayoutValidationError { get; set; }
    public string? LooseMipOverlapWarning { get; set; }
    public long MipPayloadStartOffset { get; set; }
    public long BlockStrideTableBytes { get; set; }
    public bool BlockStrideTableRangeValid { get; set; }
    public bool BlockStrideTableValuesValid { get; set; }
    public string? BlockStrideTableValidationError { get; set; }
    public bool HasStrictMipGroupMapping { get; set; }
    public string? MipMappingNote { get; set; }
    public List<PenumbraTextureMipGroupInfo> Groups { get; set; } = new();
    public string? FileInspectionError { get; set; }
    public TextureFileReport? TextureReport { get; set; }
}


/// <summary>Per-mip interpretation of the loose TEX OffsetToSurface entry.</summary>
public sealed record PenumbraTextureMipRangeInfo
{
    public int MipIndex { get; init; }
    public long SourceOffset { get; init; }
    public long PayloadBytes { get; init; }
    public long EndOffset { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public bool IsReadable { get; init; }
}

public sealed record PenumbraTextureMipGroupInfo
{
    public int Index { get; init; }
    public uint CompressedOffset { get; init; }
    public uint CompressedSize { get; init; }
    public uint DecompressedSize { get; init; }
    public uint BlockOffset { get; init; }
    public uint BlockCount { get; init; }
    public long AbsoluteDataOffset { get; init; }
    public bool IsDataRangeValid { get; init; }
    public int? MatchedMipIndex { get; init; }
}

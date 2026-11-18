using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files;

namespace FPSBooster;

/// <summary>Metadata for one mip level, ordered by its original index in the .tex file.</summary>
public sealed class TextureMipInfo
{
    public int Index { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long Bytes { get; init; }
    /// <summary>Absolute byte offset from the beginning of a raw loose .tex file, or -1 when unavailable.</summary>
    public long SourceFileOffset { get; init; } = -1;
}

/// <summary>The mip level at which one of the file's High / Medium / Low LODs begins.</summary>
public sealed class TextureLodInfo
{
    public string Name { get; init; } = string.Empty;
    public uint StartMip { get; init; }
    public bool IsValid { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}


/// <summary>One SqPack LOD-group descriptor read from the texture entry in its .dat file.</summary>
public sealed class TextureSqPackLodGroupInfo
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
    public int? MatchedMipWidth { get; init; }
    public int? MatchedMipHeight { get; init; }
}

/// <summary>Archive-level metadata for the SqPack texture entry. This is not a live GPU residency report.</summary>
public sealed class TextureSqPackReport
{
    public string DatFileName { get; init; } = string.Empty;
    public long FileOffset { get; init; }
    public uint FileHeaderSize { get; init; }
    public uint RawFileSize { get; init; }
    public IReadOnlyList<TextureSqPackLodGroupInfo> Groups { get; init; } = Array.Empty<TextureSqPackLodGroupInfo>();
    public long TotalCompressedBytes { get; init; }
    public long TotalDecompressedBytes { get; init; }
    public string? Note { get; init; }
    public bool HasInvalidDataRanges { get; init; }
    public bool HasStrictMipGroupMapping { get; init; }
    public string? MipMappingNote { get; init; }

    // Runtime-only locator used by the selective-read experiment. It is intentionally
    // not displayed because it is an installation-specific absolute path.
    internal FileInfo? DatFileInfo { get; init; }
    internal uint GroupCount { get; init; }
}

/// <summary>
/// Read-only analysis of a game .tex file. Mip upload order is intentionally coarse-to-fine:
/// the highest mip index (smallest image) is first, followed by progressively larger levels.
/// </summary>
public sealed class TextureFileReport
{
    public string Path { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int Depth { get; init; }
    public int MipCount { get; init; }
    public string Format { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public uint TypeFlags { get; init; }
    public long TotalMipBytes { get; init; }
    public double MetadataReadMilliseconds { get; init; }
    public long MetadataBytesRequested { get; init; }
    public IReadOnlyList<TextureMipInfo> Mips { get; init; } = Array.Empty<TextureMipInfo>();
    public IReadOnlyList<TextureLodInfo> Lods { get; init; } = Array.Empty<TextureLodInfo>();
    public TextureSqPackReport? SqPack { get; init; }

    public long CoarsestMipBytes => Mips.Count > 0 ? Mips[^1].Bytes : 0;
    public double CoarsestMipPercent => TotalMipBytes > 0 ? (double)CoarsestMipBytes / TotalMipBytes * 100d : 0d;

    public static unsafe TextureFileReport Create(
        TexFile.TexHeader header,
        string path,
        double metadataReadMilliseconds,
        long metadataBytesRequested,
        TextureSqPackReport? sqPack,
        IReadOnlyList<uint>? mipFileOffsets = null)
    {
        // SliceSize only requires TexHeader; constructing this metadata-only resource does not
        // allocate or load the texture pixel/mip payload.
        var metadataFile = new TexFile { Header = header };
        int mipCount = Math.Clamp(header.MipCount, 0, 13);
        var mipInfos = new List<TextureMipInfo>(mipCount);
        long totalBytes = 0;

        for (int mipIndex = 0; mipIndex < mipCount; mipIndex++)
        {
            int sliceBytes = metadataFile.SliceSize(mipIndex, out int mipWidth, out int mipHeight);
            int slices = GetSliceCount(header, mipIndex);
            long mipBytes = checked((long)sliceBytes * slices);
            totalBytes = checked(totalBytes + mipBytes);

            mipInfos.Add(new TextureMipInfo
            {
                Index = mipIndex,
                Width = mipWidth,
                Height = mipHeight,
                Bytes = mipBytes,
                SourceFileOffset = mipFileOffsets != null && mipIndex < mipFileOffsets.Count
                    ? mipFileOffsets[mipIndex]
                    : -1,
            });
        }

        var correlatedSqPack = CorrelateSqPackMips(sqPack, mipInfos, totalBytes);

        return new TextureFileReport
        {
            Path = path,
            Width = header.Width,
            Height = header.Height,
            Depth = header.Depth,
            MipCount = mipCount,
            Format = header.Format.ToString(),
            Type = header.Type.ToString(),
            TypeFlags = (uint)header.Type,
            TotalMipBytes = totalBytes,
            MetadataReadMilliseconds = metadataReadMilliseconds,
            MetadataBytesRequested = metadataBytesRequested,
            Mips = mipInfos,
            SqPack = correlatedSqPack,
            Lods = new[]
            {
                CreateLodInfo("High", header.LodOffset[0], header.Width, header.Height, mipCount),
                CreateLodInfo("Medium", header.LodOffset[1], header.Width, header.Height, mipCount),
                CreateLodInfo("Low", header.LodOffset[2], header.Width, header.Height, mipCount),
            },
        };
    }

    /// <summary>
    /// Confirms the archive-to-mip relationship only when every SqPack group maps one-to-one
    /// to a mip by decompressed byte size, in order, and the totals agree. Otherwise the
    /// inspector deliberately leaves the groups unmapped.
    /// </summary>
    private static TextureSqPackReport? CorrelateSqPackMips(
        TextureSqPackReport? sqPack,
        IReadOnlyList<TextureMipInfo> mips,
        long totalMipBytes)
    {
        if (sqPack == null)
            return null;

        if (sqPack.Groups.Count == 0 || sqPack.Note != null)
        {
            return CopySqPackReport(sqPack,
                hasStrictMipGroupMapping: false,
                mappingNote: "Mip mapping unavailable because the archive group table could not be validated.");
        }

        if (sqPack.Groups.Count != mips.Count)
        {
            return CopySqPackReport(sqPack,
                hasStrictMipGroupMapping: false,
                mappingNote: $"No one-to-one mapping: archive has {sqPack.Groups.Count} groups while the texture has {mips.Count} mip levels.");
        }

        if (sqPack.HasInvalidDataRanges)
        {
            return CopySqPackReport(sqPack,
                hasStrictMipGroupMapping: false,
                mappingNote: "Mip mapping withheld because at least one archive data range is invalid.");
        }

        for (int i = 0; i < mips.Count; i++)
        {
            if (sqPack.Groups[i].DecompressedSize != mips[i].Bytes)
            {
                return CopySqPackReport(sqPack,
                    hasStrictMipGroupMapping: false,
                    mappingNote: $"No exact mapping: Group {i} expands to {sqPack.Groups[i].DecompressedSize:N0} bytes, but Mip {i} is {mips[i].Bytes:N0} bytes.");
            }
        }

        if (sqPack.TotalDecompressedBytes != totalMipBytes)
        {
            return CopySqPackReport(sqPack,
                hasStrictMipGroupMapping: false,
                mappingNote: $"Mip mapping withheld: archive groups total {sqPack.TotalDecompressedBytes:N0} bytes, while computed mip data totals {totalMipBytes:N0} bytes.");
        }

        var mappedGroups = new List<TextureSqPackLodGroupInfo>(sqPack.Groups.Count);
        for (int i = 0; i < sqPack.Groups.Count; i++)
        {
            var group = sqPack.Groups[i];
            var mip = mips[i];
            mappedGroups.Add(new TextureSqPackLodGroupInfo
            {
                Index = group.Index,
                CompressedOffset = group.CompressedOffset,
                CompressedSize = group.CompressedSize,
                DecompressedSize = group.DecompressedSize,
                BlockOffset = group.BlockOffset,
                BlockCount = group.BlockCount,
                AbsoluteDataOffset = group.AbsoluteDataOffset,
                IsDataRangeValid = group.IsDataRangeValid,
                MatchedMipIndex = mip.Index,
                MatchedMipWidth = mip.Width,
                MatchedMipHeight = mip.Height,
            });
        }

        return new TextureSqPackReport
        {
            DatFileName = sqPack.DatFileName,
            FileOffset = sqPack.FileOffset,
            FileHeaderSize = sqPack.FileHeaderSize,
            RawFileSize = sqPack.RawFileSize,
            Groups = mappedGroups,
            TotalCompressedBytes = sqPack.TotalCompressedBytes,
            TotalDecompressedBytes = sqPack.TotalDecompressedBytes,
            Note = sqPack.Note,
            HasInvalidDataRanges = sqPack.HasInvalidDataRanges,
            DatFileInfo = sqPack.DatFileInfo,
            GroupCount = sqPack.GroupCount,
            HasStrictMipGroupMapping = true,
            MipMappingNote = $"Strict structural match for this file: all {mips.Count} SqPack groups match the corresponding mip payload sizes, in order. Lumina writes the groups in this order; Group N is therefore a structural candidate for Mip N. Earlier testing compared Mips 0, 5, and 9 byte-for-byte against Lumina; the current archive-only read validates the selected output size and reports its SHA-256.",
        };
    }

    private static TextureSqPackReport CopySqPackReport(
        TextureSqPackReport source, bool hasStrictMipGroupMapping, string mappingNote)
    {
        return new TextureSqPackReport
        {
            DatFileName = source.DatFileName,
            FileOffset = source.FileOffset,
            FileHeaderSize = source.FileHeaderSize,
            RawFileSize = source.RawFileSize,
            Groups = source.Groups,
            TotalCompressedBytes = source.TotalCompressedBytes,
            TotalDecompressedBytes = source.TotalDecompressedBytes,
            Note = source.Note,
            HasInvalidDataRanges = source.HasInvalidDataRanges,
            DatFileInfo = source.DatFileInfo,
            GroupCount = source.GroupCount,
            HasStrictMipGroupMapping = hasStrictMipGroupMapping,
            MipMappingNote = mappingNote,
        };
    }

    private static TextureLodInfo CreateLodInfo(string name, uint startMip, int baseWidth, int baseHeight, int mipCount)
    {
        bool valid = startMip < mipCount;
        int shift = valid ? (int)startMip : 0;
        return new TextureLodInfo
        {
            Name = name,
            StartMip = startMip,
            IsValid = valid,
            Width = valid ? Math.Max(1, baseWidth >> shift) : 0,
            Height = valid ? Math.Max(1, baseHeight >> shift) : 0,
        };
    }

    private static int GetSliceCount(TexFile.TexHeader header, int mipIndex)
    {
        return (header.Type & TexFile.Attribute.TextureTypeMask) switch
        {
            TexFile.Attribute.TextureType3D => Math.Max(1, header.Depth >> mipIndex),
            TexFile.Attribute.TextureTypeCube => 6,
            TexFile.Attribute.TextureType2DArray => Math.Max(1, (int)header.ArraySize),
            _ => 1,
        };
    }
}

/// <summary>Result of selectively reading one SqPack mip group without loading the full texture.</summary>
public sealed class TextureMipReadReport
{
    public int MipIndex { get; init; }
    public int BlockCount { get; init; }
    public long DeclaredGroupStoredBytes { get; init; }
    public long DataBytesRequested { get; init; }
    public long BlockTableBytesRequested { get; init; }
    public long GroupBytesRequested { get; init; }
    public long DecompressedBytes { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string BlockTypeSummary { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    // Kept internally until the UI optionally submits this single mip to the plugin-owned GPU test texture.
    internal byte[] Data { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// First-stage texture-streaming research service. It analyses game .tex files asynchronously,
/// without changing the game's texture resources or allocating GPU textures.
/// </summary>
public sealed class TextureStreamingService : IDisposable
{
    private readonly IDataManager _dataManager;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeLoad;
    private CancellationTokenSource? _activeMipRead;
    private bool _isLoading;
    private bool _isReadingMip;
    private string? _error;
    private string? _mipReadError;
    private string _status = "Enter a game .tex path to inspect its mip chain.";
    private TextureFileReport? _report;
    private TextureMipReadReport? _mipRead;
    private TextureFileReport? _mipReadSourceReport;
    private string? _mipReadSourcePath;

    public TextureStreamingService(IDataManager dataManager)
    {
        _dataManager = dataManager;
    }

    public bool IsLoading
    {
        get { lock (_sync) return _isLoading; }
    }

    public string Status
    {
        get { lock (_sync) return _status; }
    }

    public string? Error
    {
        get { lock (_sync) return _error; }
    }

    public TextureFileReport? Report
    {
        get { lock (_sync) return _report; }
    }

    public bool IsReadingMip
    {
        get { lock (_sync) return _isReadingMip; }
    }

    public string? MipReadError
    {
        get { lock (_sync) return _mipReadError; }
    }

    public TextureMipReadReport? MipRead
    {
        get { lock (_sync) return _mipRead; }
    }

    /// <summary>The exact metadata report that owns the most recently read mip payload.</summary>
    public TextureFileReport? MipReadSourceReport
    {
        get { lock (_sync) return _mipReadSourceReport; }
    }

    /// <summary>Non-null when the most recently read mip came from a loose replacement file.</summary>
    public string? MipReadSourcePath
    {
        get { lock (_sync) return _mipReadSourcePath; }
    }

    public void Inspect(string path)
    {
        string normalizedPath = path.Trim().Replace('\\', '/');
        if (normalizedPath.Length == 0 || !normalizedPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            CancellationTokenSource? previous;
            CancellationTokenSource? previousInvalidMipRead;
            lock (_sync)
            {
                previous = _activeLoad;
                previousInvalidMipRead = _activeMipRead;
                _activeLoad = null;
                _activeMipRead = null;
                _isLoading = false;
                _isReadingMip = false;
                _error = "Enter a valid game .tex path (for example, a path under chara/, bg/, or ui/).";
                _mipReadError = null;
                _mipRead = null;
                _mipReadSourceReport = null;
                _mipReadSourcePath = null;
                _status = "Path not inspected.";
                _report = null;
            }

            previous?.Cancel();
            previousInvalidMipRead?.Cancel();
            return;
        }

        CancellationTokenSource? previousLoad;
        CancellationTokenSource? previousMipRead;
        CancellationTokenSource cts;
        lock (_sync)
        {
            previousLoad = _activeLoad;
            previousMipRead = _activeMipRead;
            _activeMipRead = null;
            _isReadingMip = false;
            cts = new CancellationTokenSource();
            _activeLoad = cts;
            _isLoading = true;
            _error = null;
            _mipReadError = null;
            _mipRead = null;
            _mipReadSourceReport = null;
            _mipReadSourcePath = null;
            _report = null;
            _status = "Reading the .tex header and SqPack metadata only; mip payloads are not loaded...";
        }

        previousLoad?.Cancel();
        previousMipRead?.Cancel();
        _ = LoadAsync(normalizedPath, cts);
    }

    public void Clear()
    {
        CancellationTokenSource? cts;
        CancellationTokenSource? mipReadCts;
        lock (_sync)
        {
            cts = _activeLoad;
            mipReadCts = _activeMipRead;
            _activeLoad = null;
            _activeMipRead = null;
            _isLoading = false;
            _isReadingMip = false;
            _error = null;
            _mipReadError = null;
            _mipRead = null;
            _mipReadSourceReport = null;
            _mipReadSourcePath = null;
            _report = null;
            _status = "Enter a game .tex path to inspect its mip chain.";
        }

        cts?.Cancel();
        mipReadCts?.Cancel();
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        CancellationTokenSource? mipReadCts;
        lock (_sync)
        {
            cts = _activeLoad;
            mipReadCts = _activeMipRead;
            _activeLoad = null;
            _activeMipRead = null;
            _isLoading = false;
            _isReadingMip = false;
            _mipRead = null;
            _mipReadSourceReport = null;
            _mipReadSourcePath = null;
        }

        cts?.Cancel();
        mipReadCts?.Cancel();
    }

    /// <summary>
    /// Reads one mip group directly from the owning SqPack .dat file. The operation does not
    /// call GetFileAsync<TexFile> and does not load any other mip payload as a reference.
    /// </summary>
    public void ReadMip(int mipIndex)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource cts;
        TextureFileReport report;

        lock (_sync)
        {
            if (_report == null)
            {
                _mipReadError = "Inspect a .tex file successfully before reading an individual mip.";
                _mipRead = null;
                return;
            }

            if (_isLoading || _isReadingMip)
                return;

            if (mipIndex < 0 || mipIndex >= _report.Mips.Count)
            {
                _mipReadError = $"Mip index must be between 0 and {_report.Mips.Count - 1}.";
                _mipRead = null;
                return;
            }

            previous = _activeMipRead;
            cts = new CancellationTokenSource();
            _activeMipRead = cts;
            _isReadingMip = true;
            _mipReadError = null;
            _mipRead = null;
            report = _report;
            _mipReadSourceReport = report;
            _mipReadSourcePath = null;
        }

        previous?.Cancel();
        _ = ReadMipAsync(report, mipIndex, cts);
    }

    /// <summary>
    /// Reads exactly one mip from a validated loose TEX file, either supplied directly by the user
    /// or resolved through Penumbra.
    /// The 80-byte TexHeader stores an absolute OffsetToSurface entry for each mip. Mips may
    /// have gaps, padding, trailing data, or unusual overlaps; each requested range is checked
    /// independently against the file bounds before it is read.
    /// </summary>
    public void ReadLooseMip(string path, TextureFileReport report, int mipIndex, long mipPayloadStartOffset, long expectedFileLength, DateTime expectedLastWriteUtc)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource cts;

        if (string.IsNullOrWhiteSpace(path))
        {
            lock (_sync)
            {
                _mipReadError = "The selected loose texture source path is empty.";
                _mipRead = null;
            }
            return;
        }

        if (mipPayloadStartOffset != 80)
            throw new InvalidOperationException("Only loose TEX files with the standard 80-byte header at offset zero are supported.");
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(report.Path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected loose-file path does not match the path used to inspect this texture header.");
        if (((TexFile.Attribute)report.TypeFlags & TexFile.Attribute.TextureTypeMask) != TexFile.Attribute.TextureType2D)
            throw new NotSupportedException("Loose-file selective reads currently support ordinary 2D textures only.");
        if (mipIndex < 0 || mipIndex >= report.Mips.Count)
            throw new ArgumentOutOfRangeException(nameof(mipIndex), $"Mip index must be between 0 and {report.Mips.Count - 1}.");
        // Validate only the requested entry. Lumina documents OffsetToSurface == 0 as an
        // absent mip; that must not prevent reading a different mip with a valid range.
        // The automated full-chain upload is gated separately on every entry being present.
        var selectedMip = report.Mips[mipIndex];
        long selectedStart = selectedMip.SourceFileOffset;
        long selectedEnd;
        try
        {
            selectedEnd = checked(selectedStart + selectedMip.Bytes);
        }
        catch (OverflowException)
        {
            throw new InvalidDataException($"Mip {mipIndex} has an overflowing source range.");
        }

        if (selectedStart == 0)
            throw new InvalidDataException($"Mip {mipIndex} is absent: TexHeader.OffsetToSurface[{mipIndex}] is zero.");
        if (selectedStart < mipPayloadStartOffset || selectedMip.Bytes <= 0 || selectedEnd > expectedFileLength)
            throw new InvalidDataException($"Mip {mipIndex}'s OffsetToSurface range (0x{selectedStart:X}..0x{selectedEnd:X}) is outside the validated loose texture file.");

        lock (_sync)
        {
            if (_isLoading || _isReadingMip)
                return;

            previous = _activeMipRead;
            cts = new CancellationTokenSource();
            _activeMipRead = cts;
            _isReadingMip = true;
            _mipReadError = null;
            _mipRead = null;
            _mipReadSourceReport = report;
            _mipReadSourcePath = path;
        }

        previous?.Cancel();
        _ = ReadLooseMipAsync(path, report, mipIndex, mipPayloadStartOffset, expectedFileLength, expectedLastWriteUtc, cts);
    }

    /// <summary>Clears the last mip result without discarding the vanilla SqPack metadata report.</summary>
    public void ClearMipReadResult()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _activeMipRead;
            _activeMipRead = null;
            _isReadingMip = false;
            _mipRead = null;
            _mipReadError = null;
            _mipReadSourceReport = null;
            _mipReadSourcePath = null;
        }
        cts?.Cancel();
    }

    private sealed class TextureArchiveMetadata
    {
        public TexFile.TexHeader Header { get; init; }
        public TextureSqPackReport SqPack { get; init; } = new();
        public double ReadMilliseconds { get; init; }
        public long BytesRequested { get; init; }
    }

    /// <summary>
    /// Resolves the path through Lumina's in-memory repository/category indexes, then reads
    /// only the raw SqPack entry header, LOD descriptor table, and 80-byte TexHeader from
    /// the owning .dat file. No mip data blocks are read or decompressed during Inspect.
    /// </summary>
    private TextureArchiveMetadata ReadTextureArchiveMetadata(string path, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        if (GameData.ParseFilePath(path) is not { } parsedPath)
            throw new InvalidDataException("Lumina could not parse the game texture path.");

        var gameData = _dataManager.GameData;
        if (!gameData.Repositories.TryGetValue(parsedPath.Repository, out var repository))
            throw new FileNotFoundException($"Repository '{parsedPath.Repository}' was not found.");

        if (!Repository.CategoryNameToIdMap.TryGetValue(parsedPath.Category, out byte categoryId) ||
            !repository.Categories.TryGetValue(categoryId, out var categories))
        {
            throw new FileNotFoundException($"SqPack category '{parsedPath.Category}' was not found.");
        }

        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!category.TryGetFileDatOffset(parsedPath, out byte datFileId, out long fileOffset))
                continue;

            if (!category.DatFiles.TryGetValue(datFileId, out var dat))
                throw new FileNotFoundException($"SqPack dat{datFileId} was not available.");

            var datFile = dat.File;
            uint headerSize;
            uint rawFileSize;
            uint groupCount;
            byte[] texHeaderBytes = new byte[80];

            using (var stream = new FileStream(
                datFile.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            {
                if (fileOffset < 0 || checked(fileOffset + 24) > stream.Length)
                    throw new InvalidDataException("The resolved SqPack entry header falls outside the .dat file.");

                stream.Position = fileOffset;
                headerSize = reader.ReadUInt32();
                uint fileType = reader.ReadUInt32();
                rawFileSize = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                groupCount = reader.ReadUInt32();

                if (fileType != 4)
                    throw new InvalidDataException($"The resolved SqPack entry is type {fileType}, not a texture entry.");
                if (headerSize < 24 || groupCount == 0 || groupCount > 18)
                    throw new InvalidDataException($"Unexpected texture entry header: {headerSize} bytes, {groupCount} LOD groups.");
                if (rawFileSize < texHeaderBytes.Length)
                    throw new InvalidDataException($"The texture raw size ({rawFileSize}) is smaller than its {texHeaderBytes.Length}-byte header.");

                long lodTableEnd = checked(fileOffset + 24L + (long)groupCount * 20L);
                long dataBaseOffset = checked(fileOffset + headerSize);
                if (lodTableEnd > dataBaseOffset)
                    throw new InvalidDataException("The SqPack entry header does not contain the complete LOD descriptor table.");

                // In the standard Win32 texture layout, the first LOD group's compressed
                // offset is also the size of the leading raw .tex header/prefix copied by
                // Lumina before it writes decompressed mip groups into the output buffer.
                stream.Position = fileOffset + 24;
                uint leadingPrefixSize = reader.ReadUInt32();
                if (leadingPrefixSize < texHeaderBytes.Length)
                    throw new NotSupportedException($"This texture uses a leading prefix of only {leadingPrefixSize} bytes; the metadata-only reader requires at least the {texHeaderBytes.Length}-byte TexHeader.");
                if (leadingPrefixSize > rawFileSize)
                    throw new InvalidDataException($"The leading prefix ({leadingPrefixSize} bytes) exceeds the declared raw texture size ({rawFileSize} bytes).");

                if (checked(dataBaseOffset + texHeaderBytes.Length) > stream.Length)
                    throw new InvalidDataException("The .tex header falls outside the owning .dat file.");

                stream.Position = dataBaseOffset;
                ReadExactly(stream, texHeaderBytes, 0, texHeaderBytes.Length);
            }

            var header = MemoryMarshal.Read<TexFile.TexHeader>(texHeaderBytes.AsSpan());
            if (header.Width == 0 || header.Height == 0 || header.MipCount is < 1 or > 13)
                throw new InvalidDataException($"The raw .tex header is invalid (resolution {header.Width}×{header.Height}, mip count {header.MipCount}).");

            var sqPackReport = ReadSqPackLodGroups(datFile, fileOffset, headerSize, groupCount);
            timer.Stop();
            long metadataBytesRequested = checked(24L + sizeof(uint) + texHeaderBytes.Length + 24L + (long)groupCount * 20L);

            return new TextureArchiveMetadata
            {
                Header = header,
                SqPack = sqPackReport,
                ReadMilliseconds = timer.Elapsed.TotalMilliseconds,
                BytesRequested = metadataBytesRequested,
            };
        }

        throw new FileNotFoundException("The game path was not found in the resolved SqPack index.");
    }

    private static TextureSqPackReport ReadSqPackLodGroups(
        FileInfo datFile,
        long fileOffset,
        uint fileHeaderSize,
        uint groupCount)
    {
        const int SqPackFileInfoSize = 24;
        const int LodGroupRecordSize = 20;
        const uint MaximumTextureLodGroups = 18; // Defensive upper bound; ordinary 2D files may have one group per mip.

        if (groupCount == 0 || groupCount > MaximumTextureLodGroups)
        {
            return new TextureSqPackReport
            {
                DatFileName = datFile.Name,
                FileOffset = fileOffset,
                FileHeaderSize = fileHeaderSize,
                Note = $"Unexpected SqPack texture group count: {groupCount}.",
            };
        }

        try
        {
            long tableOffset = checked(fileOffset + SqPackFileInfoSize);
            long tableSize = checked((long)groupCount * LodGroupRecordSize);
            long dataBaseOffset = checked(fileOffset + fileHeaderSize);
            long tableEnd = checked(tableOffset + tableSize);

            if (fileOffset < 0 || fileHeaderSize < SqPackFileInfoSize || tableEnd > dataBaseOffset)
            {
                return new TextureSqPackReport
                {
                    DatFileName = datFile.Name,
                    FileOffset = fileOffset,
                    FileHeaderSize = fileHeaderSize,
                    Note = "The SqPack header size does not contain the complete LOD-group table.",
                };
            }

            using var stream = new FileStream(
                datFile.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

            if (tableEnd > stream.Length)
            {
                return new TextureSqPackReport
                {
                    DatFileName = datFile.Name,
                    FileOffset = fileOffset,
                    FileHeaderSize = fileHeaderSize,
                    Note = "The LOD-group table extends beyond the .dat file.",
                };
            }

            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            // SqPackFileInfo is a fixed 24-byte record: size, type, raw size, two
            // reserved uints, then the number of following LodBlock records.
            stream.Position = fileOffset;
            uint storedHeaderSize = reader.ReadUInt32();
            uint storedFileType = reader.ReadUInt32();
            uint rawFileSize = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            uint storedGroupCount = reader.ReadUInt32();

            if (storedFileType != 4)
            {
                return new TextureSqPackReport
                {
                    DatFileName = datFile.Name,
                    FileOffset = fileOffset,
                    FileHeaderSize = fileHeaderSize,
                    Note = $"The resolved SqPack entry is file type {storedFileType}, not a texture entry.",
                };
            }

            if (storedHeaderSize != fileHeaderSize || storedGroupCount != groupCount)
            {
                return new TextureSqPackReport
                {
                    DatFileName = datFile.Name,
                    FileOffset = fileOffset,
                    FileHeaderSize = fileHeaderSize,
                    Note = "The raw SqPack entry header does not match Lumina's parsed metadata. The LOD table was not interpreted.",
                };
            }

            stream.Position = tableOffset;
            var groups = new List<TextureSqPackLodGroupInfo>((int)groupCount);
            long totalCompressedBytes = 0;
            long totalDecompressedBytes = 0;
            bool hasInvalidRanges = false;

            for (int i = 0; i < groupCount; i++)
            {
                uint compressedOffset = reader.ReadUInt32();
                uint compressedSize = reader.ReadUInt32();
                uint decompressedSize = reader.ReadUInt32();
                uint blockOffset = reader.ReadUInt32();
                uint blockCount = reader.ReadUInt32();

                long absoluteDataOffset = checked(dataBaseOffset + compressedOffset);
                long compressedEnd = checked(absoluteDataOffset + compressedSize);
                bool rangeValid = absoluteDataOffset >= dataBaseOffset && compressedEnd <= stream.Length;
                hasInvalidRanges |= !rangeValid;

                totalCompressedBytes = checked(totalCompressedBytes + compressedSize);
                totalDecompressedBytes = checked(totalDecompressedBytes + decompressedSize);

                groups.Add(new TextureSqPackLodGroupInfo
                {
                    Index = i,
                    CompressedOffset = compressedOffset,
                    CompressedSize = compressedSize,
                    DecompressedSize = decompressedSize,
                    BlockOffset = blockOffset,
                    BlockCount = blockCount,
                    AbsoluteDataOffset = absoluteDataOffset,
                    IsDataRangeValid = rangeValid,
                });
            }

            return new TextureSqPackReport
            {
                DatFileName = datFile.Name,
                FileOffset = fileOffset,
                FileHeaderSize = fileHeaderSize,
                RawFileSize = rawFileSize,
                Groups = groups,
                TotalCompressedBytes = totalCompressedBytes,
                TotalDecompressedBytes = totalDecompressedBytes,
                HasInvalidDataRanges = hasInvalidRanges,
                DatFileInfo = datFile,
                GroupCount = groupCount,
                Note = hasInvalidRanges
                    ? "At least one group's declared compressed range falls outside the .dat file. Treat the offsets as diagnostic only."
                    : null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return new TextureSqPackReport
            {
                DatFileName = datFile.Name,
                FileOffset = fileOffset,
                FileHeaderSize = fileHeaderSize,
                Note = $"Could not read SqPack LOD metadata: {ex.Message}",
            };
        }
    }

    internal static TextureMipReadReport ReadMipGroup(
        TextureFileReport report,
        int mipIndex,
        CancellationToken cancellationToken)
    {
        var sqPack = report.SqPack ?? throw new InvalidOperationException("SqPack metadata is unavailable for this texture.");
        if (!sqPack.HasStrictMipGroupMapping)
            throw new InvalidOperationException(sqPack.MipMappingNote ?? "The archive groups could not be mapped safely to mip levels.");
        if (sqPack.DatFileInfo == null)
            throw new InvalidOperationException("The owning SqPack .dat file could not be resolved.");
        if (((TexFile.Attribute)report.TypeFlags & TexFile.Attribute.TextureTypeMask) != TexFile.Attribute.TextureType2D)
            throw new NotSupportedException("Selective mip reads currently support ordinary 2D textures only. Array, cube, and volume textures need separate slice-layout validation.");

        var group = sqPack.Groups[mipIndex];
        if (group.MatchedMipIndex != mipIndex || !group.IsDataRangeValid)
            throw new InvalidOperationException($"SqPack Group {group.Index} is not safely mapped to Mip {mipIndex}.");

        var timer = Stopwatch.StartNew();
        byte[] selectedMip = ReadSqPackMipGroup(
            sqPack.DatFileInfo, sqPack, group, cancellationToken, out string blockTypeSummary, out long dataBytesRequested);
        timer.Stop();

        long expectedBytes = report.Mips[mipIndex].Bytes;
        if (selectedMip.LongLength != expectedBytes)
            throw new InvalidDataException($"Selective SqPack read produced {selectedMip.Length:N0} bytes, but Mip {mipIndex} is expected to contain {expectedBytes:N0} bytes.");

        string sha256 = Convert.ToHexString(SHA256.HashData(selectedMip));
        long blockTableBytes = checked((long)group.BlockCount * sizeof(short));
        long groupBytesRequested = checked(dataBytesRequested + blockTableBytes);

        return new TextureMipReadReport
        {
            MipIndex = mipIndex,
            BlockCount = checked((int)group.BlockCount),
            DeclaredGroupStoredBytes = group.CompressedSize,
            DataBytesRequested = dataBytesRequested,
            BlockTableBytesRequested = blockTableBytes,
            GroupBytesRequested = groupBytesRequested,
            DecompressedBytes = selectedMip.LongLength,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            Sha256 = sha256,
            BlockTypeSummary = blockTypeSummary,
            Data = selectedMip,
            Message = $"Read SqPack Group {group.Index} independently. Its {selectedMip.Length:N0}-byte output matches the expected Mip {mipIndex} payload size. No full texture reference was loaded.",
        };
    }

    /// <summary>
    /// Decompresses only one texture LOD group. This mirrors Lumina's SqPack reader:
    /// 20-byte LOD records, a shared ushort block-stride table, and 16-byte block headers
    /// followed by either raw or Deflate-compressed data. Only the selected group's blocks
    /// are read from the .dat file.
    /// </summary>
    private static byte[] ReadSqPackMipGroup(
        FileInfo datFile,
        TextureSqPackReport sqPack,
        TextureSqPackLodGroupInfo group,
        CancellationToken cancellationToken,
        out string blockTypeSummary,
        out long dataBytesRequested)
    {
        const int SqPackFileInfoSize = 24;
        const int LodGroupRecordSize = 20;
        const int DatBlockHeaderSize = 16;
        const uint UncompressedBlockType = 32000;

        if (group.DecompressedSize > int.MaxValue)
            throw new InvalidDataException("Mip group is too large to read into memory.");
        if (sqPack.GroupCount == 0 || group.BlockCount == 0)
            throw new InvalidDataException("The selected mip group has no SqPack data blocks.");

        long dataBaseOffset = checked(sqPack.FileOffset + sqPack.FileHeaderSize);
        long blockSizeTableOffset = checked(sqPack.FileOffset + SqPackFileInfoSize + (long)sqPack.GroupCount * LodGroupRecordSize);
        long totalBlockEntries = 0;
        long expectedBlockOffset = 0;
        foreach (var lodGroup in sqPack.Groups)
        {
            if (lodGroup.Index < group.Index)
                expectedBlockOffset = checked(expectedBlockOffset + lodGroup.BlockCount);
            totalBlockEntries = checked(totalBlockEntries + lodGroup.BlockCount);
        }

        // Lumina consumes this shared size table sequentially while walking groups. Only
        // use BlockOffset as a direct table index after confirming it matches that order.
        if (group.BlockOffset != expectedBlockOffset)
            throw new InvalidDataException($"Group {group.Index} declares block-table offset {group.BlockOffset}, but the preceding groups account for {expectedBlockOffset} blocks.");

        long blockSizeIndex = checked((long)group.BlockOffset + group.BlockCount);
        long blockSizeTableEnd = checked(blockSizeTableOffset + blockSizeIndex * sizeof(ushort));
        if (blockSizeTableOffset < sqPack.FileOffset || blockSizeIndex > totalBlockEntries || blockSizeTableEnd > dataBaseOffset)
            throw new InvalidDataException("The selected group's block-size table entries fall outside the validated SqPack entry header/table.");

        long groupStart = checked(dataBaseOffset + group.CompressedOffset);
        long groupEnd = checked(groupStart + group.CompressedSize);

        using var stream = new FileStream(
            datFile.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.RandomAccess);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (groupStart < dataBaseOffset || groupEnd > stream.Length)
            throw new InvalidDataException("The selected group's compressed data range falls outside the .dat file.");

        byte[] output = new byte[(int)group.DecompressedSize];
        int outputOffset = 0;
        long summedBlockStrides = 0;
        long blockPosition = groupStart;
        var observedBlockTypes = new SortedDictionary<uint, int>();
        dataBytesRequested = 0;

        for (uint blockIndex = 0; blockIndex < group.BlockCount; blockIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long sizeEntryOffset = checked(blockSizeTableOffset + ((long)group.BlockOffset + blockIndex) * sizeof(ushort));
            stream.Position = sizeEntryOffset;
            short signedBlockStride = reader.ReadInt16();
            if (signedBlockStride < DatBlockHeaderSize)
                throw new InvalidDataException($"Mip block {blockIndex} has an invalid on-disk stride ({signedBlockStride} bytes).");
            int blockStride = signedBlockStride;

            long blockEnd = checked(blockPosition + blockStride);
            if (blockEnd > groupEnd || blockEnd > stream.Length)
                throw new InvalidDataException($"Mip block {blockIndex} extends beyond its declared SqPack group range.");

            stream.Position = blockPosition;
            uint blockHeaderSize = reader.ReadUInt32();
            _ = reader.ReadUInt32(); // Reserved / unknown field, matching Lumina's DatBlockHeader.
            uint blockType = reader.ReadUInt32();
            uint blockDataSize = reader.ReadUInt32();
            dataBytesRequested = checked(dataBytesRequested + DatBlockHeaderSize);
            observedBlockTypes[blockType] = observedBlockTypes.TryGetValue(blockType, out int typeCount)
                ? typeCount + 1
                : 1;

            if (blockHeaderSize != DatBlockHeaderSize)
                throw new InvalidDataException($"Mip block {blockIndex} has an unexpected header size ({blockHeaderSize}); expected the fixed {DatBlockHeaderSize}-byte SqPack block header.");
            if (blockDataSize == 0 || blockDataSize > output.Length - outputOffset)
                throw new InvalidDataException($"Mip block {blockIndex} declares an invalid decompressed size ({blockDataSize}).");

            int storedPayloadSize = checked(blockStride - DatBlockHeaderSize);
            long payloadOffset = checked(blockPosition + DatBlockHeaderSize);

            if (blockType == UncompressedBlockType)
            {
                if (blockDataSize > storedPayloadSize)
                    throw new InvalidDataException($"Uncompressed mip block {blockIndex} is shorter than its declared output size.");

                stream.Position = payloadOffset;
                ReadExactly(stream, output, outputOffset, checked((int)blockDataSize));
                dataBytesRequested = checked(dataBytesRequested + blockDataSize);
            }
            else
            {
                // Match Lumina's ReadFileBlock implementation exactly: only type 32000
                // is treated as raw data. Every other type is passed through Deflate.
                // Retail archives can contain non-16000 values here (for example 14
                // and 2418); rejecting those values is stricter than Lumina and prevents
                // otherwise-readable mip groups from being read.
                if (storedPayloadSize <= 0)
                    throw new InvalidDataException($"Compressed mip block {blockIndex} (SqPack type {blockType}) has no compressed payload.");

                byte[] compressedPayload = new byte[storedPayloadSize];
                stream.Position = payloadOffset;
                ReadExactly(stream, compressedPayload, 0, compressedPayload.Length);
                dataBytesRequested = checked(dataBytesRequested + compressedPayload.Length);

                try
                {
                    using var compressedStream = new MemoryStream(compressedPayload, writable: false);
                    using var inflater = new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: false);
                    ReadExactly(inflater, output, outputOffset, checked((int)blockDataSize));
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
                {
                    throw new InvalidDataException(
                        $"Mip block {blockIndex} (SqPack type {blockType}) could not be inflated as Deflate data: {ex.Message}", ex);
                }
            }

            outputOffset = checked(outputOffset + (int)blockDataSize);
            summedBlockStrides = checked(summedBlockStrides + blockStride);
            blockPosition = blockEnd;
        }

        if (summedBlockStrides != group.CompressedSize)
            throw new InvalidDataException($"Block strides total {summedBlockStrides:N0} bytes, but the LOD record declares {group.CompressedSize:N0} compressed bytes.");
        if (outputOffset != output.Length)
            throw new InvalidDataException($"Decompressed blocks produced {outputOffset:N0} bytes, but the LOD record declares {output.Length:N0}.");

        var typeSummary = new StringBuilder();
        foreach (var pair in observedBlockTypes)
        {
            if (typeSummary.Length > 0)
                typeSummary.Append(", ");

            string handling = pair.Key == UncompressedBlockType ? "raw" : "Deflate";
            typeSummary.Append($"{pair.Key} ({pair.Value} {handling})");
        }

        blockTypeSummary = typeSummary.ToString();
        return output;
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
                throw new EndOfStreamException($"Expected {count:N0} bytes, received {totalRead:N0}.");
            totalRead += read;
        }
    }

    private async Task ReadLooseMipAsync(
        string path, TextureFileReport report, int mipIndex, long mipPayloadStartOffset, long expectedFileLength, DateTime expectedLastWriteUtc, CancellationTokenSource cts)
    {
        try
        {
            var result = await Task.Run(
                () => ReadLooseMipPayload(path, report, mipIndex, mipPayloadStartOffset, expectedFileLength, expectedLastWriteUtc, cts.Token), cts.Token);
            lock (_sync)
            {
                if (!ReferenceEquals(_activeMipRead, cts))
                    return;

                _mipRead = result;
                _mipReadError = null;
            }
        }
        catch (OperationCanceledException)
        {
            // A new inspection, source selection, or window close superseded this read.
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeMipRead, cts))
                    _mipReadError = ex.Message;
            }
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeMipRead, cts))
                {
                    _activeMipRead = null;
                    _isReadingMip = false;
                }
            }
            cts.Dispose();
        }
    }

    internal static TextureMipReadReport ReadLooseMipPayload(
        string path, TextureFileReport report, int mipIndex, long mipPayloadStartOffset, long expectedFileLength, DateTime expectedLastWriteUtc, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var mip = report.Mips[mipIndex];
        if (mip.Bytes <= 0 || mip.Bytes > int.MaxValue)
            throw new InvalidDataException($"Mip {mipIndex} has an unsupported payload size ({mip.Bytes:N0} bytes).");

        // OffsetToSurface in the 80-byte TexHeader stores absolute offsets from the beginning
        // of the .tex file. Use that authoritative table rather than assuming mip payloads are
        // tightly concatenated just because the overall file length looks plausible.
        long mipOffset = mip.SourceFileOffset;
        long mipEnd = checked(mipOffset + mip.Bytes);
        if (mipOffset < mipPayloadStartOffset || mipEnd > expectedFileLength)
            throw new InvalidDataException($"Mip {mipIndex}'s TexHeader OffsetToSurface range is missing or falls outside the validated loose texture file.");

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        if (stream.Length != expectedFileLength)
            throw new IOException($"The loose TEX source changed after metadata inspection (expected {expectedFileLength:N0} bytes, found {stream.Length:N0}). Inspect it again before reading mips.");
        DateTime currentLastWriteUtc = File.GetLastWriteTimeUtc(path);
        if (expectedLastWriteUtc != default && currentLastWriteUtc != expectedLastWriteUtc)
            throw new IOException("The loose TEX source's last-write timestamp changed after metadata inspection. Inspect it again before reading mips.");

        byte[] data = new byte[checked((int)mip.Bytes)];
        stream.Position = mipOffset;
        int totalRead = 0;
        while (totalRead < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(data, totalRead, data.Length - totalRead);
            if (read == 0)
                throw new EndOfStreamException($"The loose TEX source ended while reading Mip {mipIndex}.");
            totalRead += read;
        }

        // The file may be replaced or edited while the read is in progress. Recheck the
        // snapshot after the selected range has been read so we never upload a torn mip.
        var afterRead = new FileInfo(path);
        afterRead.Refresh();
        if (!afterRead.Exists || afterRead.Length != expectedFileLength
            || (expectedLastWriteUtc != default && afterRead.LastWriteTimeUtc != expectedLastWriteUtc))
        {
            throw new IOException("The loose TEX source changed while the mip was being read. Inspect it again before uploading this payload.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        timer.Stop();

        string sha256 = Convert.ToHexString(SHA256.HashData(data));
        return new TextureMipReadReport
        {
            MipIndex = mipIndex,
            BlockCount = 0,
            DeclaredGroupStoredBytes = mip.Bytes,
            DataBytesRequested = data.LongLength,
            BlockTableBytesRequested = 0,
            GroupBytesRequested = data.LongLength,
            DecompressedBytes = data.LongLength,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            Sha256 = sha256,
            BlockTypeSummary = "Raw loose TEX mip (no SqPack block decompression)",
            Data = data,
            Message = $"Read loose-file Mip {mipIndex} directly from offset 0x{mipOffset:X}. Its {data.Length:N0}-byte output matches the expected mip payload size; no other mip payload was read or decompressed.",
        };
    }

    private async Task ReadMipAsync(TextureFileReport report, int mipIndex, CancellationTokenSource cts)
    {
        try
        {
            var result = await Task.Run(() => ReadMipGroup(report, mipIndex, cts.Token), cts.Token);
            lock (_sync)
            {
                if (!ReferenceEquals(_activeMipRead, cts))
                    return;

                _mipRead = result;
                _mipReadError = null;
            }
        }
        catch (OperationCanceledException)
        {
            // A new inspection or window close superseded this read.
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeMipRead, cts))
                    _mipReadError = ex.Message;
            }
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeMipRead, cts))
                {
                    _activeMipRead = null;
                    _isReadingMip = false;
                }
            }

            cts.Dispose();
        }
    }

    private async Task LoadAsync(string path, CancellationTokenSource cts)
    {
        try
        {
            // This reads the raw .tex header and LOD descriptor metadata only. Unlike
            // GetFileAsync<TexFile>, it does not allocate RawFileSize or decompress mip data.
            var metadata = await Task.Run(
                () => ReadTextureArchiveMetadata(path, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            var report = TextureFileReport.Create(
                metadata.Header, path, metadata.ReadMilliseconds, metadata.BytesRequested, metadata.SqPack);
            lock (_sync)
            {
                if (!ReferenceEquals(_activeLoad, cts))
                    return;

                _report = report;
                _status = "Texture header and SqPack metadata loaded. No mip payloads were read or decompressed.";
            }
        }
        catch (OperationCanceledException)
        {
            // A newer inspection or window close superseded this request.
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeLoad, cts))
                {
                    _error = ex.Message;
                    _status = "Texture inspection failed.";
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeLoad, cts))
                {
                    _activeLoad = null;
                    _isLoading = false;
                }
            }

            cts.Dispose();
        }
    }

}

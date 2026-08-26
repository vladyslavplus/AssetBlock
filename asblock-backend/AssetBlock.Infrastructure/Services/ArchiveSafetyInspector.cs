using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpCompress.Readers;

namespace AssetBlock.Infrastructure.Services;

internal sealed partial class ArchiveSafetyInspector(
    IOptions<ArchiveInspectionOptions> options,
    ILogger<ArchiveSafetyInspector> logger) : IArchiveSafetyInspector
{
    private readonly ArchiveInspectionOptions _options = options.Value;

    private static readonly HashSet<string> _readmeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README.md", "README.txt", "README.rst", "README", "readme.markdown"
    };

    private static readonly HashSet<string> _manifestNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "Cargo.toml", "pyproject.toml", "requirements.txt",
        "go.mod", "pom.xml", "build.gradle", "build.gradle.kts", "composer.json"
    };

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex ControlCharRegex();

    [GeneratedRegex(@"<PackageReference\s+Include=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex CsprojPackageReferenceRegex();

    public async Task<ArchiveSafetyResult> Inspect(
        Stream archiveStream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        cancellationToken.ThrowIfCancellationRequested();

        // Outer ZIP/TAR/TAR.GZ only. Nested archives are ordinary files here; ClamAV
        // MaxRecursion/MaxScanSize/MaxFiles/AlertExceedsMax enforce nested depth and size.

        try
        {
            (var prefix, Stream combined) = await PrefixStream(archiveStream, 512, cancellationToken).ConfigureAwait(false);
            await using (combined)
            {
                if (LooksLikeZip(prefix))
                {
                    using var reader = ReaderFactory.OpenReader(combined, new ReaderOptions { LeaveStreamOpen = true });
                    return await InspectZipEntries(reader, cancellationToken).ConfigureAwait(false);
                }

                if (LooksLikeGzip(prefix))
                {
                    var compressed = new CountingReadStream(combined);
                    await using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
                    var limited = new GzipRatioLimitingStream(gzip, compressed, _options.MaxCompressionRatio);
                    var result = await InspectTarEntries(limited, cancellationToken).ConfigureAwait(false);
                    limited.ThrowIfRatioExceeded();
                    return result;
                }

                return await InspectTarEntries(combined, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RejectedArchiveException ex)
        {
            return ArchiveSafetyResult.Rejected(
                ex.ErrorCode,
                ErrorCodesToErrorMessages.GetMessage(ex.ErrorCode),
                ex.ExpandedBytes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Archive inspection failed with a parsing or decompression error.");
            return ArchiveSafetyResult.Rejected(
                ErrorCodes.ARCHIVE_CORRUPT,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT));
        }
    }

    private async Task<ArchiveSafetyResult> InspectZipEntries(IReader reader, CancellationToken cancellationToken)
    {
        var acc = new InspectionAccumulator();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = reader.Entry;

            if (!TryCanonicalizePath(entry.Key, _options.MaxPathLength, _options.MaxPathDepth, out var canonical, out var pathError))
            {
                throw new RejectedArchiveException(pathError);
            }

            if (!seenPaths.Add(canonical))
            {
                throw new RejectedArchiveException(ErrorCodes.ARCHIVE_DUPLICATE_ENTRY);
            }

            if (!string.IsNullOrEmpty(entry.LinkTarget))
            {
                throw new RejectedArchiveException(ErrorCodes.ARCHIVE_SYMLINK_NOT_ALLOWED);
            }

            if (entry.IsEncrypted)
            {
                throw new RejectedArchiveException(ErrorCodes.ARCHIVE_ENCRYPTED);
            }

            if (entry.IsDirectory)
            {
                continue;
            }

            await using var entryStream = reader.OpenEntryStream();
            await ConsumeCountedFile(acc, canonical, entryStream, entry.CompressedSize, cancellationToken)
                .ConfigureAwait(false);
        }

        return FinishInspection(acc);
    }

    private async Task<ArchiveSafetyResult> InspectTarEntries(Stream tarStream, CancellationToken cancellationToken)
    {
        var acc = new InspectionAccumulator();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = new TarReader(tarStream, leaveOpen: true);

        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsTarMetadataEntry(entry.EntryType))
            {
                continue;
            }

            if (!IsAllowedTarEntryType(entry.EntryType, out var entryKindError))
            {
                throw new RejectedArchiveException(entryKindError);
            }

            if (!TryCanonicalizePath(entry.Name, _options.MaxPathLength, _options.MaxPathDepth, out var canonical, out var pathError))
            {
                throw new RejectedArchiveException(pathError);
            }

            if (!seenPaths.Add(canonical))
            {
                throw new RejectedArchiveException(ErrorCodes.ARCHIVE_DUPLICATE_ENTRY);
            }

            if (entry.EntryType is TarEntryType.Directory)
            {
                continue;
            }

            var entryStream = entry.DataStream ?? Stream.Null;
            await ConsumeCountedFile(acc, canonical, entryStream, compressedSize: 0, cancellationToken)
                .ConfigureAwait(false);
        }

        return FinishInspection(acc);
    }

    private async Task ConsumeCountedFile(
        InspectionAccumulator acc,
        string canonical,
        Stream entryStream,
        long compressedSize,
        CancellationToken cancellationToken)
    {
        acc.FileCount++;
        if (acc.FileCount > _options.MaxEntries)
        {
            throw new RejectedArchiveException(ErrorCodes.ARCHIVE_TOO_MANY_ENTRIES);
        }

        var baseName = Path.GetFileName(canonical);
        var captureReadme = acc.Readme is null && _readmeNames.Contains(baseName);
        var captureManifest = acc.Manifests.Count < _options.MaxManifestFiles && IsManifestName(baseName);
        var captureLimit = captureReadme
            ? _options.MaxReadmeBytes + 1
            : captureManifest
                ? _options.MaxManifestBytes + 1
                : 0;

        var remainingTotal = _options.MaxTotalExpandedBytes - acc.TotalExpandedBytes;
        if (remainingTotal <= 0)
        {
            throw new RejectedArchiveException(ErrorCodes.ARCHIVE_TOTAL_SIZE_EXCEEDED);
        }

        var ratioCap = long.MaxValue;
        if (compressedSize > 0)
        {
            var ratioBudget = compressedSize * _options.MaxCompressionRatio;
            if (double.IsFinite(ratioBudget) && ratioBudget < long.MaxValue)
            {
                ratioCap = Math.Max(0, (long)Math.Floor(ratioBudget));
            }
        }

        var readCap = MinPositive(
            _options.MaxEntryExpandedBytes,
            remainingTotal,
            ratioCap);
        var (entryBytes, captured) = await ReadBounded(
            entryStream,
            readCap,
            captureLimit,
            cancellationToken).ConfigureAwait(false);

        if (entryBytes > _options.MaxEntryExpandedBytes)
        {
            throw new RejectedArchiveException(ErrorCodes.ARCHIVE_ENTRY_TOO_LARGE);
        }

        if (compressedSize > 0 && entryBytes > 0
            && entryBytes / (double)compressedSize > _options.MaxCompressionRatio)
        {
            throw new RejectedArchiveException(ErrorCodes.ARCHIVE_COMPRESSION_RATIO_EXCEEDED);
        }

        acc.TotalExpandedBytes += entryBytes;
        if (acc.TotalExpandedBytes > _options.MaxTotalExpandedBytes)
        {
            throw new RejectedArchiveException(ErrorCodes.ARCHIVE_TOTAL_SIZE_EXCEEDED);
        }

        if (captureReadme && captured is { Length: > 0 })
        {
            acc.Readme = DecodeUtf8(captured, _options.MaxReadmeBytes);
        }
        else if (captureManifest && captured is { Length: > 0 })
        {
            var parsed = TryParseManifest(canonical, baseName, captured);
            if (parsed is not null)
            {
                acc.Manifests.Add(parsed);
            }
        }
    }

    private static ArchiveSafetyResult FinishInspection(InspectionAccumulator acc)
    {
        if (acc.FileCount == 0)
        {
            throw new RejectedArchiveException(ErrorCodes.ARCHIVE_EMPTY);
        }

        return ArchiveSafetyResult.Safe(
            acc.FileCount,
            acc.TotalExpandedBytes,
            acc.Readme,
            FitManifestMetadata(acc.Manifests));
    }

    private static long MinPositive(long first, long second, long third)
    {
        var min = first < second ? first : second;
        return min < third ? min : third;
    }

    private static bool LooksLikeZip(ReadOnlySpan<byte> prefix) =>
        prefix.Length >= 4 && prefix[0] == (byte)'P' && prefix[1] == (byte)'K';

    private static bool LooksLikeGzip(ReadOnlySpan<byte> prefix) =>
        prefix.Length >= 2 && prefix[0] == 0x1F && prefix[1] == 0x8B;

    private static bool IsTarMetadataEntry(TarEntryType entryType) =>
        entryType is TarEntryType.ExtendedAttributes
            or TarEntryType.GlobalExtendedAttributes
            or TarEntryType.LongLink
            or TarEntryType.LongPath;

    internal static bool IsAllowedTarEntryType(TarEntryType? entryType, out string errorCode)
    {
        if (entryType is null)
        {
            errorCode = ErrorCodes.ARCHIVE_SPECIAL_ENTRY_NOT_ALLOWED;
            return false;
        }

        switch (entryType.Value)
        {
            case TarEntryType.RegularFile:
            case TarEntryType.V7RegularFile:
            case TarEntryType.ContiguousFile:
            case TarEntryType.Directory:
            case TarEntryType.ExtendedAttributes:
            case TarEntryType.GlobalExtendedAttributes:
            case TarEntryType.LongLink:
            case TarEntryType.LongPath:
                errorCode = string.Empty;
                return true;
            case TarEntryType.SymbolicLink:
            case TarEntryType.HardLink:
                errorCode = ErrorCodes.ARCHIVE_SYMLINK_NOT_ALLOWED;
                return false;
            default:
                errorCode = ErrorCodes.ARCHIVE_SPECIAL_ENTRY_NOT_ALLOWED;
                return false;
        }
    }

    private static async Task<(byte[] Prefix, Stream Combined)> PrefixStream(
        Stream source,
        int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await source.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var prefix = read == length ? buffer : buffer.AsSpan(0, read).ToArray();
        return (prefix, new PrefixedReadStream(prefix, source));
    }

    private static bool TryCanonicalizePath(
        string? rawKey,
        int maxPathLength,
        int maxPathDepth,
        out string canonical,
        out string errorCode)
    {
        canonical = string.Empty;
        errorCode = ErrorCodes.ARCHIVE_EMPTY_PATH;
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return false;
        }

        var normalized = rawKey.Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        if (normalized.StartsWith('/') || rawKey.StartsWith('\\') || Path.IsPathRooted(rawKey) || normalized.Contains(':'))
        {
            errorCode = ErrorCodes.ARCHIVE_ABSOLUTE_PATH;
            return false;
        }

        normalized = normalized.Trim('/');
        if (normalized.Length == 0)
        {
            errorCode = ErrorCodes.ARCHIVE_EMPTY_PATH;
            return false;
        }

        if (normalized.Length > maxPathLength)
        {
            errorCode = ErrorCodes.ARCHIVE_PATH_TOO_LONG;
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > maxPathDepth)
        {
            errorCode = ErrorCodes.ARCHIVE_PATH_TOO_DEEP;
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                errorCode = ErrorCodes.ARCHIVE_PATH_TRAVERSAL;
                return false;
            }
        }

        canonical = string.Join('/', segments);
        return true;
    }

    private static async Task<(long BytesRead, byte[]? Captured)> ReadBounded(
        Stream entryStream,
        long maxEntryBytes,
        int captureLimit,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        MemoryStream? capture = captureLimit > 0 ? new MemoryStream(Math.Min(captureLimit, 16385)) : null;
        long total = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await entryStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxEntryBytes + 1)
                {
                    return (total, null);
                }

                if (capture is not null && capture.Length < captureLimit)
                {
                    var remaining = captureLimit - (int)capture.Length;
                    capture.Write(buffer, 0, Math.Min(read, remaining));
                }
            }

            return (total, capture?.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            capture?.Dispose();
        }
    }

    private static string? DecodeUtf8(byte[] buffer, int maxBytes)
    {
        var usable = TruncateToUtf8Boundary(buffer, Math.Min(buffer.Length, maxBytes));
        if (usable <= 0)
        {
            return null;
        }

        var text = ControlCharRegex().Replace(Encoding.UTF8.GetString(buffer, 0, usable), string.Empty)
            .Trim('\uFEFF', ' ', '\r', '\n', '\t');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int TruncateToUtf8Boundary(byte[] buffer, int length)
    {
        if (length <= 0)
        {
            return 0;
        }

        if ((buffer[length - 1] & 0x80) == 0)
        {
            return length;
        }

        var i = length;
        var continuation = 0;
        while (i > 0 && (buffer[i - 1] & 0xC0) == 0x80)
        {
            i--;
            continuation++;
            if (continuation > 3)
            {
                return 0;
            }
        }

        if (i == 0)
        {
            return 0;
        }

        var expected = buffer[i - 1] switch
        {
            >= 0xF0 => 3,
            >= 0xE0 => 2,
            >= 0xC0 => 1,
            _ => 0
        };

        return continuation == expected ? length : i - 1;
    }

    private static bool IsManifestName(string baseName) =>
        _manifestNames.Contains(baseName) || baseName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private RecognizedManifestItem? TryParseManifest(string canonicalPath, string baseName, byte[] captured)
    {
        try
        {
            var text = DecodeUtf8(captured, _options.MaxManifestBytes);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var dependencies = new List<string>();
            if (baseName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in CsprojPackageReferenceRegex().Matches(text))
                {
                    if (match.Groups.Count > 1
                        && dependencies.Count < 32
                        && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
                        && !dependencies.Contains(match.Groups[1].Value.Trim()))
                    {
                        dependencies.Add(match.Groups[1].Value.Trim());
                    }
                }
            }
            else if (baseName.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            {
                TryExtractNpmDependencies(text, dependencies);
            }

            return new RecognizedManifestItem(canonicalPath, ResolveManifestType(baseName), Dependencies: dependencies);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring malformed optional manifest {Path}", canonicalPath);
            return null;
        }
    }

    private static ArchiveAnalysisManifestMetadata? FitManifestMetadata(List<RecognizedManifestItem> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        for (var count = items.Count; count >= 1; count--)
        {
            var candidate = new ArchiveAnalysisManifestMetadata(items.Take(count).ToList());
            try
            {
                ArchiveAnalysisSerializer.SerializeManifestMetadata(candidate);
                return candidate;
            }
            catch (ArchiveAnalysisSerializerException)
            {
            }
        }

        return null;
    }

    private static string ResolveManifestType(string baseName)
    {
        if (baseName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return "dotnet";
        }

        return baseName.ToLowerInvariant() switch
        {
            "package.json" => "npm",
            "cargo.toml" => "cargo",
            "pyproject.toml" or "requirements.txt" => "python",
            "go.mod" => "go",
            "pom.xml" or "build.gradle" or "build.gradle.kts" => "jvm",
            "composer.json" => "composer",
            _ => "generic"
        };
    }

    private static void TryExtractNpmDependencies(string jsonText, List<string> dependencies)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
            foreach (var section in new[] { "dependencies", "devDependencies" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var deps)
                    || deps.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var prop in deps.EnumerateObject())
                {
                    if (dependencies.Count >= 32)
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(prop.Name) && !dependencies.Contains(prop.Name))
                    {
                        dependencies.Add(prop.Name);
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
    }

    private sealed class InspectionAccumulator
    {
        public long TotalExpandedBytes { get; set; }
        public int FileCount { get; set; }
        public string? Readme { get; set; }
        public List<RecognizedManifestItem> Manifests { get; } = [];
    }

    private sealed class PrefixedReadStream(byte[] prefix, Stream remainder) : Stream
    {
        private int _prefixOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_prefixOffset < prefix.Length)
            {
                var available = prefix.Length - _prefixOffset;
                var copied = Math.Min(available, buffer.Length);
                prefix.AsSpan(_prefixOffset, copied).CopyTo(buffer);
                _prefixOffset += copied;
                return copied;
            }

            return remainder.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_prefixOffset < prefix.Length)
            {
                var available = prefix.Length - _prefixOffset;
                var copied = Math.Min(available, buffer.Length);
                prefix.AsSpan(_prefixOffset, copied).CopyTo(buffer.Span);
                _prefixOffset += copied;
                return copied;
            }

            return await remainder.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // Remainder is owned by the caller.
        }
    }

    private sealed class RejectedArchiveException(string errorCode, long expandedBytes = 0) : Exception
    {
        public string ErrorCode { get; } = errorCode;
        public long ExpandedBytes { get; } = expandedBytes;
    }

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0)
            {
                BytesRead += read;
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                BytesRead += read;
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // Inner stream is owned by the caller.
        }
    }

    private sealed class GzipRatioLimitingStream(Stream expandedSource, CountingReadStream compressed, double maxRatio)
        : Stream
    {
        private const int RATIO_CHECK_SLICE = 8192;

        private long ExpandedBytes { get; set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var sliceLength = Math.Min(RATIO_CHECK_SLICE, buffer.Length - total);
                var read = expandedSource.Read(buffer.Slice(total, sliceLength));
                if (read == 0)
                {
                    break;
                }

                total += read;
                ExpandedBytes += read;
                ThrowIfRatioExceeded();
            }

            return total;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sliceLength = Math.Min(RATIO_CHECK_SLICE, buffer.Length - total);
                var read = await expandedSource.ReadAsync(buffer.Slice(total, sliceLength), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                ExpandedBytes += read;
                ThrowIfRatioExceeded();
            }

            return total;
        }

        public void ThrowIfRatioExceeded()
        {
            if (compressed.BytesRead <= 0 || ExpandedBytes <= 0)
            {
                return;
            }

            if (ExpandedBytes / (double)compressed.BytesRead > maxRatio)
            {
                throw new RejectedArchiveException(ErrorCodes.ARCHIVE_COMPRESSION_RATIO_EXCEEDED, ExpandedBytes);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // Expanded source is owned by the caller.
        }
    }
}

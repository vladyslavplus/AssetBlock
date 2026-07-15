using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Exceptions;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace AssetBlock.Infrastructure.Services;

/// <summary>
/// Validates archive entries without recursively extracting nested archives.
/// </summary>
internal sealed class SharpCompressAssetArchiveInspector : IAssetArchiveInspector
{
    private const int MAX_ENTRIES = 10_000;
    private const long MAX_TOTAL_UNCOMPRESSED_BYTES = 1L * 1024 * 1024 * 1024; // 1 GiB
    private const long MAX_ENTRY_UNCOMPRESSED_BYTES = 500L * 1024 * 1024; // 500 MiB
    private const double MAX_COMPRESSION_RATIO = 100.0;

    public Task Inspect(Stream archiveStream, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        if (!archiveStream.CanSeek)
        {
            throw new ArchiveRejectedException("Archive stream must be seekable for inspection.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var extensionHint = Path.GetExtension(fileName).TrimStart('.');
            if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                extensionHint = "tar.gz";
            }

            var options = new ReaderOptions
            {
                LeaveStreamOpen = true,
                ExtensionHint = string.IsNullOrWhiteSpace(extensionHint) ? null : extensionHint
            };

            using var archive = ArchiveFactory.OpenArchive(archiveStream, options);

            if (archive.IsEncrypted)
            {
                throw new ArchiveRejectedException("Password-protected archives are not allowed.");
            }

            long totalUncompressed = 0;
            var entryCount = 0;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                entryCount++;
                if (entryCount > MAX_ENTRIES)
                {
                    throw new ArchiveRejectedException($"Archive exceeds the maximum of {MAX_ENTRIES} entries.");
                }

                if (entry.IsDirectory)
                {
                    ValidateEntryPath(entry.Key);
                    continue;
                }

                if (entry.IsEncrypted)
                {
                    throw new ArchiveRejectedException("Password-protected archives are not allowed.");
                }

                ValidateEntryPath(entry.Key);

                if (!string.IsNullOrEmpty(entry.LinkTarget))
                {
                    throw new ArchiveRejectedException("Symbolic links are not allowed in archives.");
                }

                var uncompressed = entry.Size;
                if (uncompressed < 0)
                {
                    throw new ArchiveRejectedException("Archive entry has an invalid uncompressed size.");
                }

                if (uncompressed > MAX_ENTRY_UNCOMPRESSED_BYTES)
                {
                    throw new ArchiveRejectedException(
                        $"Archive entry exceeds the maximum uncompressed size of {MAX_ENTRY_UNCOMPRESSED_BYTES} bytes.");
                }

                var packed = entry.CompressedSize;
                if (packed > 0 && uncompressed > 0)
                {
                    var ratio = (double)uncompressed / packed;
                    if (ratio > MAX_COMPRESSION_RATIO)
                    {
                        throw new ArchiveRejectedException(
                            $"Archive entry compression ratio exceeds {MAX_COMPRESSION_RATIO}:1.");
                    }
                }

                totalUncompressed += uncompressed;
                if (totalUncompressed > MAX_TOTAL_UNCOMPRESSED_BYTES)
                {
                    throw new ArchiveRejectedException(
                        $"Archive exceeds the maximum total uncompressed size of {MAX_TOTAL_UNCOMPRESSED_BYTES} bytes.");
                }
            }
        }
        catch (ArchiveRejectedException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ArchiveRejectedException($"Archive could not be inspected: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private static void ValidateEntryPath(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArchiveRejectedException("Archive contains an entry with an empty path.");
        }

        var normalized = key.Replace('\\', '/');
        if (Path.IsPathRooted(key) || normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw new ArchiveRejectedException("Archive contains an absolute path entry.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
        {
            throw new ArchiveRejectedException("Archive contains a path traversal entry.");
        }
    }
}

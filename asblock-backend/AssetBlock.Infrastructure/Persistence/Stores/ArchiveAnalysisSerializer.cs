using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Infrastructure.Persistence.Stores;

public sealed class ArchiveAnalysisSerializerException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Strict, non-polymorphic, bounded serializer and validator for archive manifest metadata.
/// Enforces max 16 KiB UTF-8 serialized byte limit and semantic structure before database write.
/// </summary>
public static class ArchiveAnalysisSerializer
{
    private const int MAX_MANIFEST_METADATA_BYTES = 16384;
    private const int MAX_MANIFEST_COUNT = 8;
    private const int MAX_FILE_NAME_LENGTH = 512;
    private const int MAX_DEPENDENCY_COUNT = 128;

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 8,
        WriteIndented = false
    };

    public static string SerializeManifestMetadata(ArchiveAnalysisManifestMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Validate(metadata);

        string json;
        try
        {
            json = JsonSerializer.Serialize(metadata, _options);
        }
        catch (Exception ex)
        {
            throw new ArchiveAnalysisSerializerException("Failed to serialize manifest metadata.", ex);
        }

        return GuardSize(json);
    }

    public static ArchiveAnalysisManifestMetadata DeserializeManifestMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArchiveAnalysisSerializerException("Manifest metadata JSON must not be null or whitespace.");
        }

        GuardSize(json);

        if (json.Contains("\"$type\"", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveAnalysisSerializerException("Polymorphic type metadata ($type) is forbidden in manifest metadata.");
        }

        ArchiveAnalysisManifestMetadata? result;
        try
        {
            result = JsonSerializer.Deserialize<ArchiveAnalysisManifestMetadata>(json, _options);
        }
        catch (Exception ex) when (ex is not ArchiveAnalysisSerializerException)
        {
            throw new ArchiveAnalysisSerializerException("Failed to deserialize manifest metadata JSON.", ex);
        }

        if (result is null)
        {
            throw new ArchiveAnalysisSerializerException("Deserialized manifest metadata must not be null.");
        }

        Validate(result);
        return result;
    }

    private static void Validate(ArchiveAnalysisManifestMetadata metadata)
    {
        if (metadata.Manifests == null)
        {
            throw new ArchiveAnalysisSerializerException("Manifest list must not be null.");
        }

        if (metadata.Manifests.Count > MAX_MANIFEST_COUNT)
        {
            throw new ArchiveAnalysisSerializerException($"Manifest count exceeds maximum of {MAX_MANIFEST_COUNT}.");
        }

        foreach (RecognizedManifestItem item in metadata.Manifests)
        {
            if (string.IsNullOrWhiteSpace(item.FileName))
            {
                throw new ArchiveAnalysisSerializerException("Manifest FileName must not be null or whitespace.");
            }

            if (item.FileName.Length > MAX_FILE_NAME_LENGTH)
            {
                throw new ArchiveAnalysisSerializerException($"Manifest FileName exceeds maximum length of {MAX_FILE_NAME_LENGTH}.");
            }

            if (string.IsNullOrWhiteSpace(item.ManifestType))
            {
                throw new ArchiveAnalysisSerializerException("ManifestType must not be null or whitespace.");
            }

            if (item.ManifestType.Length > 64)
            {
                throw new ArchiveAnalysisSerializerException("ManifestType exceeds maximum length of 64 characters.");
            }

            if (item.PackageName is { Length: > 256 })
            {
                throw new ArchiveAnalysisSerializerException("PackageName exceeds maximum length of 256 characters.");
            }

            if (item.PackageVersion is { Length: > 64 })
            {
                throw new ArchiveAnalysisSerializerException("PackageVersion exceeds maximum length of 64 characters.");
            }

            if (item.Description is { Length: > 2048 })
            {
                throw new ArchiveAnalysisSerializerException("Description exceeds maximum length of 2048 characters.");
            }

            if (item.Dependencies is { Count: > MAX_DEPENDENCY_COUNT })
            {
                throw new ArchiveAnalysisSerializerException($"Dependencies count exceeds maximum of {MAX_DEPENDENCY_COUNT}.");
            }
        }
    }

    private static string GuardSize(string json)
    {
        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > MAX_MANIFEST_METADATA_BYTES)
        {
            throw new ArchiveAnalysisSerializerException(
                $"Serialized manifest metadata ({byteCount} bytes) exceeds limit of {MAX_MANIFEST_METADATA_BYTES} bytes.");
        }

        return json;
    }
}

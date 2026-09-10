using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// Controlled fail-closed failure for any contract-violating or malformed payload/result JSON.
/// Callers must never treat stored job JSON as trusted input.
/// </summary>
public sealed class AssetProcessingSerializerException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Allowlist-only typed serialization for asset-processing job payloads and results. Every
/// <see cref="AssetProcessingJobType"/> maps to exactly one concrete payload and result DTO; semantic
/// validation runs on both serialize and deserialize so a tampered or corrupted row fails closed.
/// No CLR polymorphic deserialization ("$type") is permitted.
/// </summary>
public static partial class AssetProcessingSerializer
{
    private const int MAX_JSON_BYTES = 4000;

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        MaxDepth = 4,
        WriteIndented = false
    };

    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex Sha256Regex();

    public static string SerializePayload(AssetProcessingJobType type, AssetProcessingPayload payload)
    {
        var json = type switch
        {
            AssetProcessingJobType.ARCHIVE_INSPECTION => payload is ArchiveInspectionPayload archivePayload
                ? SerializeValidated(archivePayload, ValidateArchiveInspectionPayload)
                : throw WrongDto(type, nameof(ArchiveInspectionPayload)),

            AssetProcessingJobType.MALWARE_SCAN => payload is MalwareScanPayload malwarePayload
                ? SerializeValidated(malwarePayload, ValidateMalwareScanPayload)
                : throw WrongDto(type, nameof(MalwareScanPayload)),

            AssetProcessingJobType.LISTING_COPILOT => payload is ListingCopilotPayload copilotPayload
                ? SerializeValidated(copilotPayload, ValidateListingCopilotPayload)
                : throw WrongDto(type, nameof(ListingCopilotPayload)),

            AssetProcessingJobType.EMBEDDING_GENERATION => payload is EmbeddingGenerationPayload embeddingPayload
                ? SerializeValidated(embeddingPayload, ValidateEmbeddingGenerationPayload)
                : throw WrongDto(type, nameof(EmbeddingGenerationPayload)),

            _ => throw UnknownType(type)
        };

        return GuardSize(json);
    }

    public static AssetProcessingPayload DeserializePayload(AssetProcessingJobType type, string json)
    {
        return type switch
        {
            AssetProcessingJobType.ARCHIVE_INSPECTION =>
                DeserializeValidated(json, () => JsonSerializer.Deserialize<ArchiveInspectionPayload>(json, _options), ValidateArchiveInspectionPayload),

            AssetProcessingJobType.MALWARE_SCAN =>
                DeserializeValidated(json, () => JsonSerializer.Deserialize<MalwareScanPayload>(json, _options), ValidateMalwareScanPayload),

            AssetProcessingJobType.LISTING_COPILOT =>
                DeserializeValidated(json, () => JsonSerializer.Deserialize<ListingCopilotPayload>(json, _options), ValidateListingCopilotPayload),

            AssetProcessingJobType.EMBEDDING_GENERATION =>
                DeserializeValidated(json, () => JsonSerializer.Deserialize<EmbeddingGenerationPayload>(json, _options), ValidateEmbeddingGenerationPayload),

            _ => throw UnknownType(type)
        };
    }

    public static string SerializeResult(AssetProcessingJobType type, AssetProcessingResult result)
    {
        var json = type switch
        {
            AssetProcessingJobType.ARCHIVE_INSPECTION => result is ArchiveInspectionResult archiveResult
                ? SerializeValidated(archiveResult, ValidateArchiveInspectionResult)
                : throw WrongDto(type, nameof(ArchiveInspectionResult)),

            AssetProcessingJobType.MALWARE_SCAN => result is MalwareScanResult malwareResult
                ? SerializeValidated(malwareResult, ValidateMalwareScanResult)
                : throw WrongDto(type, nameof(MalwareScanResult)),

            AssetProcessingJobType.LISTING_COPILOT => result is ListingCopilotResult copilotResult
                ? SerializeValidated(copilotResult, ValidateListingCopilotResult)
                : throw WrongDto(type, nameof(ListingCopilotResult)),

            AssetProcessingJobType.EMBEDDING_GENERATION => result is EmbeddingGenerationResult embeddingResult
                ? SerializeValidated(embeddingResult, ValidateEmbeddingGenerationResult)
                : throw WrongDto(type, nameof(EmbeddingGenerationResult)),

            _ => throw UnknownType(type)
        };

        return GuardSize(json);
    }

    public static AssetProcessingResult DeserializeResult(AssetProcessingJobType type, string json)
    {
        return type switch
        {
            AssetProcessingJobType.ARCHIVE_INSPECTION =>
                DeserializeValidated(json, () => JsonSerializer.Deserialize<ArchiveInspectionResult>(json, _options), ValidateArchiveInspectionResult),

            AssetProcessingJobType.MALWARE_SCAN =>
                DeserializeValidated(json, () => JsonSerializer.Deserialize<MalwareScanResult>(json, _options), ValidateMalwareScanResult),

            AssetProcessingJobType.LISTING_COPILOT =>
                DeserializeValidated(json, () => JsonSerializer.Deserialize<ListingCopilotResult>(json, _options), ValidateListingCopilotResult),

            AssetProcessingJobType.EMBEDDING_GENERATION =>
                DeserializeValidated(json, () => JsonSerializer.Deserialize<EmbeddingGenerationResult>(json, _options), ValidateEmbeddingGenerationResult),

            _ => throw UnknownType(type)
        };
    }

    private static string SerializeValidated<T>(T dto, Action<T> validate)
    {
        validate(dto);

        try
        {
            return JsonSerializer.Serialize(dto, _options);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new AssetProcessingSerializerException($"Failed to serialize {typeof(T).Name}.", ex);
        }
    }

    private static T DeserializeValidated<T>(string json, Func<T?> parse, Action<T> validate)
    {
        RejectUnsafeInput(json);

        T? dto;

        try
        {
            dto = parse();
        }
        catch (JsonException ex)
        {
            throw new AssetProcessingSerializerException($"JSON for {typeof(T).Name} is malformed, has an unsupported shape, or contains unknown fields.", ex);
        }

        if (dto is null)
        {
            throw new AssetProcessingSerializerException($"JSON for {typeof(T).Name} must not be null.");
        }

        validate(dto);
        return dto;
    }

    private static void RejectUnsafeInput(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new AssetProcessingSerializerException("JSON input must be non-empty.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(json) > MAX_JSON_BYTES)
        {
            throw new AssetProcessingSerializerException($"JSON input exceeds {MAX_JSON_BYTES} bytes.");
        }

        if (json.Contains("$type", StringComparison.OrdinalIgnoreCase))
        {
            throw new AssetProcessingSerializerException("Polymorphic type metadata is not allowed.");
        }
    }

    private static string GuardSize(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MAX_JSON_BYTES)
        {
            throw new AssetProcessingSerializerException($"Serialized JSON exceeds {MAX_JSON_BYTES} bytes.");
        }

        return json;
    }

    private static AssetProcessingSerializerException UnknownType(AssetProcessingJobType type) =>
        new($"Unknown job type: {type}");

    private static AssetProcessingSerializerException WrongDto(AssetProcessingJobType type, string expectedDtoName) =>
        new($"Expected {expectedDtoName} for job type {type}.");

    private static AssetProcessingSerializerException Fail(string message) =>
        new(message);

    private static void ValidatePolicyVersion(string policyVersion)
    {
        if (string.IsNullOrWhiteSpace(policyVersion) || policyVersion.Length > 64)
        {
            throw Fail("PolicyVersion must be non-empty and <= 64 characters.");
        }
    }

    private static void ValidateArchiveInspectionPayload(ArchiveInspectionPayload payload) { }

    private static void ValidateMalwareScanPayload(MalwareScanPayload payload) =>
        ValidatePolicyVersion(payload.PolicyVersion);

    private static void ValidateListingCopilotPayload(ListingCopilotPayload payload) =>
        ValidatePolicyVersion(payload.PolicyVersion);

    private static void ValidateArchiveInspectionResult(ArchiveInspectionResult result)
    {
        if (result.FileCount < 0)
        {
            throw Fail("FileCount cannot be negative.");
        }

        if (result.TotalSizeUncompressed < 0)
        {
            throw Fail("TotalSizeUncompressed cannot be negative.");
        }
    }

    private static void ValidateMalwareScanResult(MalwareScanResult result) { }

    private static void ValidateListingCopilotResult(ListingCopilotResult result)
    {
        if (string.IsNullOrWhiteSpace(result.ContentHash) || !Sha256Regex().IsMatch(result.ContentHash))
        {
            throw Fail("ContentHash must be a valid lowercase SHA-256 hex string.");
        }
    }

    private static void ValidateEmbeddingGenerationPayload(EmbeddingGenerationPayload payload)
    {
        if (payload.AssetId == Guid.Empty)
        {
            throw Fail("AssetId must not be empty.");
        }

        if (payload.AssetVersionId == Guid.Empty)
        {
            throw Fail("AssetVersionId must not be empty.");
        }

        if (payload.TargetRevision <= 0)
        {
            throw Fail("TargetRevision must be positive.");
        }

        if (string.IsNullOrWhiteSpace(payload.ContentHash) || !Sha256Regex().IsMatch(payload.ContentHash))
        {
            throw Fail("ContentHash must be a valid lowercase SHA-256 hex string.");
        }

        if (string.IsNullOrWhiteSpace(payload.ModelKey) || !Sha256Regex().IsMatch(payload.ModelKey))
        {
            throw Fail("ModelKey must be a valid lowercase SHA-256 hex string.");
        }

        if (!string.Equals(payload.ContentSchemaVersion, "asset-public-metadata-v1", StringComparison.Ordinal))
        {
            throw Fail("ContentSchemaVersion must be 'asset-public-metadata-v1'.");
        }
    }

    private static void ValidateEmbeddingGenerationResult(EmbeddingGenerationResult result)
    {
        if (result.AssetId == Guid.Empty)
        {
            throw Fail("AssetId must not be empty.");
        }

        if (result.SourceRevision <= 0)
        {
            throw Fail("SourceRevision must be positive.");
        }

        if (string.IsNullOrWhiteSpace(result.ContentHash) || !Sha256Regex().IsMatch(result.ContentHash))
        {
            throw Fail("ContentHash must be a valid lowercase SHA-256 hex string.");
        }

        if (string.IsNullOrWhiteSpace(result.ModelKey) || !Sha256Regex().IsMatch(result.ModelKey))
        {
            throw Fail("ModelKey must be a valid lowercase SHA-256 hex string.");
        }

        if (result.Dimension <= 0)
        {
            throw Fail("Dimension must be positive.");
        }
    }
}

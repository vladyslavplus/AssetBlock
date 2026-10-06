using System.Text.Json;

namespace AssetBlock.Domain.Core.Publication;

/// <summary>Bounded approved public metadata copied into publication snapshots.</summary>
public static class ApprovedPublicationMetadata
{
    public const int SCHEMA_VERSION = 1;

    public sealed record PublicProjection(
        string Title,
        string? Description,
        Guid CategoryId,
        IReadOnlyList<string> Tags);

    public static string BuildJson(string title, string? description, Guid categoryId, IReadOnlyList<string> tags)
    {
        var payload = new Payload(title, description, categoryId, tags.OrderBy(t => t, StringComparer.Ordinal).ToArray());
        return JsonSerializer.Serialize(payload, _serializerOptions);
    }

    public static bool TryReadPublicProjection(string approvedMetadataJson, out PublicProjection projection)
    {
        projection = null!;
        if (!TryReadPayload(approvedMetadataJson, out Payload? payload))
        {
            return false;
        }

        projection = new PublicProjection(
            payload.Title,
            payload.Description,
            payload.CategoryId,
            payload.Tags!);
        return true;
    }

    private static bool TryReadPayload(string json, out Payload payload)
    {
        payload = null!;
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return false;
        }

        try
        {
            Payload? parsed = JsonSerializer.Deserialize<Payload>(json, _serializerOptions);
            if (parsed is null
                || string.IsNullOrWhiteSpace(parsed.Title)
                || parsed.CategoryId == Guid.Empty
                || parsed.Tags is null
                || parsed.Tags.Any(string.IsNullOrWhiteSpace))
            {
                return false;
            }

            var normalizedTags = parsed.Tags!.Select(t => t.Trim()).ToArray();
            payload = parsed with { Tags = normalizedTags };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private sealed record Payload(string Title, string? Description, Guid CategoryId, string[]? Tags);
}

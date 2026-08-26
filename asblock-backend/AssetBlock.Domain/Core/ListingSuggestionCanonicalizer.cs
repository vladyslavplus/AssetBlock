using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Domain.Core;

public static class ListingSuggestionCanonicalizer
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ComputeContentHash(ListingSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        var canonical = JsonSerializer.Serialize(
            new CanonicalSuggestion(suggestion.Category, suggestion.Description, suggestion.Tags, suggestion.Title),
            _jsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record CanonicalSuggestion(
        string Category,
        string Description,
        IReadOnlyList<string> Tags,
        string Title);
}

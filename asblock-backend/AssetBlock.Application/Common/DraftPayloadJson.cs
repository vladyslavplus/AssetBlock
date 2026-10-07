using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetBlock.Application.Common;

/// <summary>Canonical serialization and digests for draft/declaration JSON mutations.</summary>
public static class DraftPayloadJson
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Enum members serialize as canonical UPPER_SNAKE_CASE; property names stay camelCase.
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, _options);

    public static string ComputeDigest(string payloadJson)
    {
        var bytes = Encoding.UTF8.GetBytes(payloadJson);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// Digest over the full mutation scope, not just the payload: asset, version scope,
    /// operation kind, and the expected workspace revision the CAS will compare against.
    /// Keeps an identical payload on a different resource from replaying a foreign receipt.
    /// </summary>
    public static string ComputeRequestDigest(
        Guid assetId,
        Guid? assetVersionId,
        string operationKind,
        long expectedWorkspaceRevision,
        string payloadJson)
    {
        var envelopeJson = JsonSerializer.Serialize(
            new
            {
                assetId,
                assetVersionId,
                operationKind,
                expectedWorkspaceRevision,
                payloadJson
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return ComputeDigest(envelopeJson);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Infrastructure.Persistence;

/// <summary>
/// Single source of truth for projecting the persisted seller declaration onto the analysis license
/// contract. The enqueuer builds the input digest from these projections and the handler sends the
/// same projections as request metadata, so the two must never be allowed to drift apart.
/// </summary>
internal static class DeclaredLicenseProjector
{
    private static readonly JsonSerializerOptions _declarationJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private const string UNKNOWN_SCOPE = "UNKNOWN_SCOPE";
    private const string UNKNOWN_LICENSE = "UNKNOWN";
    private const int MAX_SCOPE_LENGTH = 512;
    private const int MAX_LICENSE_LENGTH = 128;

    /// <summary>
    /// A malformed persisted payload fails closed instead of being read as an empty valid
    /// declaration, so license reconciliation can never silently lose declared components.
    /// </summary>
    public static IReadOnlyList<DeclaredLicenseProjection> Read(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        SourceDeclarationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SourceDeclarationPayload>(payloadJson, _declarationJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Persisted source declaration payload is not readable.", ex);
        }

        if (payload is null)
        {
            throw new InvalidOperationException("Persisted source declaration payload is empty.");
        }

        var projections = new List<DeclaredLicenseProjection>(payload.Components.Count);
        foreach (SourceDeclarationComponent component in payload.Components)
        {
            projections.Add(new DeclaredLicenseProjection(ProjectScope(component), ProjectLicense(component)));
        }

        return projections;
    }

    /// <summary>
    /// The analysis contract binds a component scope path. A component without a package path or
    /// range is reported as the explicit unknown scope rather than dropped.
    /// </summary>
    private static string ProjectScope(SourceDeclarationComponent component)
    {
        var scope = component.PackagePathOrRange;
        if (string.IsNullOrWhiteSpace(scope))
        {
            return UNKNOWN_SCOPE;
        }

        return scope.Length <= MAX_SCOPE_LENGTH ? scope : scope[..MAX_SCOPE_LENGTH];
    }

    private static string ProjectLicense(SourceDeclarationComponent component)
    {
        var license = component.License;
        if (string.IsNullOrWhiteSpace(license))
        {
            return UNKNOWN_LICENSE;
        }

        return license.Length <= MAX_LICENSE_LENGTH ? license : license[..MAX_LICENSE_LENGTH];
    }
}

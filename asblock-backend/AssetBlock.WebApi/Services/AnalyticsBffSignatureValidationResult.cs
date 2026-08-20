namespace AssetBlock.WebApi.Services;

public enum AnalyticsBffSignatureValidationOutcome
{
    NO_HEADERS,
    VALID,
    INVALID
}

public sealed record AnalyticsBffSignatureValidationResult(
    AnalyticsBffSignatureValidationOutcome Outcome,
    string? VerifiedPartition = null);

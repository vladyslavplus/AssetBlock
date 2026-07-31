using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.WebApi.Services;

internal sealed class AnalyticsBffSignatureValidator(IOptions<AnalyticsRateLimitingOptions> options)
    : IAnalyticsBffSignatureValidator
{
    public AnalyticsBffSignatureValidationResult Validate(HttpContext httpContext)
    {
        var partition = httpContext.Request.Headers[AnalyticsBffRateLimitHeaders.PARTITION].ToString();
        var timestamp = httpContext.Request.Headers[AnalyticsBffRateLimitHeaders.TIMESTAMP].ToString();
        var signature = httpContext.Request.Headers[AnalyticsBffRateLimitHeaders.SIGNATURE].ToString();

        var hasPartition = !string.IsNullOrWhiteSpace(partition);
        var hasTimestamp = !string.IsNullOrWhiteSpace(timestamp);
        var hasSignature = !string.IsNullOrWhiteSpace(signature);

        if (!hasPartition && !hasTimestamp && !hasSignature)
        {
            return new AnalyticsBffSignatureValidationResult(AnalyticsBffSignatureValidationOutcome.NO_HEADERS);
        }

        if (!hasPartition || !hasTimestamp || !hasSignature)
        {
            return new AnalyticsBffSignatureValidationResult(AnalyticsBffSignatureValidationOutcome.INVALID);
        }

        var secret = options.Value.BffSigningSecret.Trim();
        if (string.IsNullOrEmpty(secret))
        {
            return new AnalyticsBffSignatureValidationResult(AnalyticsBffSignatureValidationOutcome.INVALID);
        }

        if (!AnalyticsBffSignatureHelper.IsLowerHex64(partition)
            || !AnalyticsBffSignatureHelper.IsLowerHex64(signature))
        {
            return new AnalyticsBffSignatureValidationResult(AnalyticsBffSignatureValidationOutcome.INVALID);
        }

        if (!AnalyticsBffSignatureHelper.TryParseUnixTimestampSeconds(timestamp, out var timestampSeconds)
            || !AnalyticsBffSignatureHelper.IsTimestampWithinTolerance(timestampSeconds, DateTimeOffset.UtcNow))
        {
            return new AnalyticsBffSignatureValidationResult(AnalyticsBffSignatureValidationOutcome.INVALID);
        }

        var expectedSignature = AnalyticsBffSignatureHelper.CreateRequestSignature(
            timestamp.Trim(),
            partition,
            secret);

        if (!AnalyticsBffSignatureHelper.FixedTimeEqualsHex(expectedSignature, signature))
        {
            return new AnalyticsBffSignatureValidationResult(AnalyticsBffSignatureValidationOutcome.INVALID);
        }

        return new AnalyticsBffSignatureValidationResult(
            AnalyticsBffSignatureValidationOutcome.VALID,
            partition);
    }
}

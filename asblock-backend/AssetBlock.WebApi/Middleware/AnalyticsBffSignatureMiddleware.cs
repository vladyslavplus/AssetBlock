using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.ProblemDetails;
using AssetBlock.WebApi.Services;

namespace AssetBlock.WebApi.Middleware;

internal sealed class AnalyticsBffSignatureMiddleware(RequestDelegate next, IAnalyticsBffSignatureValidator validator)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsAnalyticsEventsPost(context))
        {
            await next(context);
            return;
        }

        var result = validator.Validate(context);
        switch (result.Outcome)
        {
            case AnalyticsBffSignatureValidationOutcome.NO_HEADERS:
                // Direct API callers need a stable partition key. Without RemoteIpAddress and
                // Connection.Id, TraceIdentifier would be unique per request (fail-open). Accept
                // without ingestion instead of forwarding into the rate limiter / handler.
                if (!HasStableDirectClientIdentifier(context))
                {
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                }

                await next(context);
                return;
            case AnalyticsBffSignatureValidationOutcome.VALID:
                context.Items[AnalyticsRateLimitContextKeys.VERIFIED_BFF_PARTITION] = result.VerifiedPartition!;
                await next(context);
                return;
            default:
                var problem = AssetBlockProblemDetails.Create(
                    context,
                    StatusCodes.Status403Forbidden,
                    ErrorCodes.ERR_ANALYTICS_BFF_SIGNATURE_INVALID);
                await AssetBlockProblemDetails.Write(context, problem);
                return;
        }
    }

    private static bool IsAnalyticsEventsPost(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return false;
        }

        var path = context.Request.Path;
        if (!path.HasValue)
        {
            return false;
        }

        var normalizedPath = path.Value!.TrimEnd('/');
        var expectedPath = $"/{ApiRoutes.Analytics.BASE}/{ApiRoutes.Analytics.EVENTS}".TrimEnd('/');
        return string.Equals(normalizedPath, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasStableDirectClientIdentifier(HttpContext context) =>
        context.Connection.RemoteIpAddress is not null
        || !string.IsNullOrWhiteSpace(context.Connection.Id);
}

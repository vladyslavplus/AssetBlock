using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Infrastructure.Observability;
using AssetBlock.WebApi.ProblemDetails;
using AssetBlock.WebApi.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetBlock.WebApi.Extensions;

internal static class RateLimitingExtensions
{
    private const string UNKNOWN_PARTITION_KEY = "unknown";

    private static string GetUserPartitionKey(HttpContext httpContext) =>
        httpContext.User.FindFirst(JwtClaimTypes.SUB)?.Value
        ?? httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? httpContext.Connection.RemoteIpAddress?.ToString()
        ?? UNKNOWN_PARTITION_KEY;

    private static void ConfigureRejectedHandler(RateLimiterOptions opts)
    {
        opts.OnRejected = async (context, _) =>
        {
            if (context.Lease.TryGetMetadata(
                    AnalyticsRateLimitMetadataNames.Unavailable,
                    out var unavailable)
                && unavailable)
            {
                await HandleUnavailableRateLimitAsync(context);
                return;
            }

            if (context.Lease.TryGetMetadata(
                    AnalyticsRateLimitMetadataNames.RetryAfter,
                    out TimeSpan retryAfter))
            {
                var retrySeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                context.HttpContext.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
            }

            Microsoft.AspNetCore.Mvc.ProblemDetails problem = AssetBlockProblemDetails.Create(
                context.HttpContext,
                StatusCodes.Status429TooManyRequests,
                ErrorCodes.ERR_RATE_LIMITED);
            await AssetBlockProblemDetails.Write(context.HttpContext, problem);
        };
    }

    private static async Task HandleUnavailableRateLimitAsync(OnRejectedContext context)
    {
        Endpoint? endpoint = context.HttpContext.GetEndpoint();
        var policyName = endpoint?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        AssetBlockDiagnostics.RecordAnalyticsRateLimitUnavailable(policyName ?? "unknown");

        if (string.Equals(
                policyName,
                RateLimitingConstants.Policies.ANALYTICS_EVENTS,
                StringComparison.Ordinal))
        {
            IHostEnvironment environment = context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>();
            if (!environment.IsStaging() && !environment.IsProduction())
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }
        }

        if (string.Equals(
                policyName,
                RateLimitingConstants.Policies.SELLER_ANALYTICS_SALES_EXPORT,
                StringComparison.Ordinal))
        {
            Microsoft.AspNetCore.Mvc.ProblemDetails problem = AssetBlockProblemDetails.Create(
                context.HttpContext,
                StatusCodes.Status503ServiceUnavailable,
                ErrorCodes.ERR_ANALYTICS_RATE_LIMIT_UNAVAILABLE);
            await AssetBlockProblemDetails.Write(context.HttpContext, problem);
            return;
        }

        Microsoft.AspNetCore.Mvc.ProblemDetails fallback = AssetBlockProblemDetails.Create(
            context.HttpContext,
            StatusCodes.Status503ServiceUnavailable,
            ErrorCodes.ERR_ANALYTICS_RATE_LIMIT_UNAVAILABLE);
        await AssetBlockProblemDetails.Write(context.HttpContext, fallback);
    }

    extension(IServiceCollection services)
    {
        public void AddApiRateLimiting()
        {
            services.AddRateLimiter(opts =>
            {
                opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                ConfigureRejectedHandler(opts);
                AddAuthPolicies(opts);
                AddSlidingWindowPolicies(opts);
                AddTelemetryPolicies(opts);
                AddSellerAnalyticsPolicies(opts);
            });
        }

        public void AddIntegrationTestingRateLimiting()
        {
            services.AddRateLimiter(opts =>
            {
                opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                ConfigureRejectedHandler(opts);

                AddNoOpPolicy(RateLimitingConstants.Policies.AUTH_REGISTER);
                AddNoOpPolicy(RateLimitingConstants.Policies.AUTH_LOGIN);
                AddNoOpPolicy(RateLimitingConstants.Policies.AUTH_REFRESH);
                AddNoOpPolicy(RateLimitingConstants.Policies.AUTH_PASSWORD_RESET_REQUEST);
                AddNoOpPolicy(RateLimitingConstants.Policies.AUTH_EMAIL_ACTION_CONFIRM);
                AddNoOpPolicy(RateLimitingConstants.Policies.AUTH_SIGNALR_TOKEN);
                AddNoOpPolicy(RateLimitingConstants.Policies.USERS_EMAIL_VERIFICATION_RESEND);
                AddNoOpPolicy(RateLimitingConstants.Policies.USERS_EMAIL_CHANGE_REQUEST);
                AddNoOpPolicy(RateLimitingConstants.Policies.USERS_PASSWORD_CHANGE);
                AddNoOpPolicy(RateLimitingConstants.Policies.ASSETS_UPLOAD);
                AddNoOpPolicy(RateLimitingConstants.Policies.ASSETS_DOWNLOAD);
                AddNoOpPolicy(RateLimitingConstants.Policies.PAYMENTS_CHECKOUT);
                AddNoOpPolicy(RateLimitingConstants.Policies.ANALYTICS_EVENTS);
                AddNoOpPolicy(RateLimitingConstants.Policies.SELLER_ANALYTICS_SALES_EXPORT);
                AddNoOpPolicy(RateLimitingConstants.Policies.LISTING_COPILOT_ENQUEUE);
                AddNoOpPolicy(RateLimitingConstants.Policies.ADMIN_OUTBOX_REPLAY);
                return;

                void AddNoOpPolicy(string policyName)
                {
                    opts.AddPolicy(policyName, _ => RateLimitPartition.GetNoLimiter(policyName));
                }
            });
        }
    }

    private static void AddAuthPolicies(RateLimiterOptions opts)
    {
        opts.AddPolicy(RateLimitingConstants.Policies.AUTH_REGISTER, httpContext =>
            GetAuthIpPartition(
                httpContext,
                RateLimitingConstants.Windows.AUTH_REGISTER_LIMIT,
                RateLimitingConstants.Windows.AUTH_REGISTER_PERIOD_SECONDS));

        opts.AddPolicy(RateLimitingConstants.Policies.AUTH_LOGIN, httpContext =>
            GetAuthIpPartition(
                httpContext,
                RateLimitingConstants.Windows.AUTH_LOGIN_LIMIT,
                RateLimitingConstants.Windows.AUTH_LOGIN_PERIOD_SECONDS));

        opts.AddPolicy(RateLimitingConstants.Policies.AUTH_REFRESH, httpContext =>
            GetAuthIpPartition(
                httpContext,
                RateLimitingConstants.Windows.AUTH_REFRESH_LIMIT,
                RateLimitingConstants.Windows.AUTH_REFRESH_PERIOD_SECONDS));

        opts.AddPolicy(RateLimitingConstants.Policies.AUTH_PASSWORD_RESET_REQUEST, httpContext =>
            GetAuthIpPartition(
                httpContext,
                RateLimitingConstants.Windows.AUTH_PASSWORD_RESET_REQUEST_LIMIT,
                RateLimitingConstants.Windows.AUTH_PASSWORD_RESET_REQUEST_PERIOD_SECONDS));

        opts.AddPolicy(RateLimitingConstants.Policies.AUTH_EMAIL_ACTION_CONFIRM, httpContext =>
            GetAuthIpPartition(
                httpContext,
                RateLimitingConstants.Windows.AUTH_EMAIL_ACTION_CONFIRM_LIMIT,
                RateLimitingConstants.Windows.AUTH_EMAIL_ACTION_CONFIRM_PERIOD_SECONDS));

        opts.AddPolicy(RateLimitingConstants.Policies.AUTH_SIGNALR_TOKEN, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: GetUserPartitionKey(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.AUTH_SIGNALR_TOKEN_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.AUTH_SIGNALR_TOKEN_LIMIT,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));
    }

    private static RateLimitPartition<string> GetAuthIpPartition(
        HttpContext httpContext,
        int permitLimit,
        int periodSeconds)
    {
        if (httpContext.Connection.RemoteIpAddress is not { } remoteIp)
        {
            return RateLimitPartition.Get(
                "auth:missing-client-ip",
                static _ => new MissingClientIpRateLimiter());
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: remoteIp.ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(periodSeconds),
                PermitLimit = permitLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    }

    private static void AddSlidingWindowPolicies(RateLimiterOptions opts)
    {
        opts.AddPolicy(RateLimitingConstants.Policies.USERS_EMAIL_VERIFICATION_RESEND, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: GetUserPartitionKey(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.USERS_EMAIL_VERIFICATION_RESEND_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.USERS_EMAIL_VERIFICATION_RESEND_LIMIT,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.USERS_EMAIL_CHANGE_REQUEST, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: GetUserPartitionKey(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.USERS_EMAIL_CHANGE_REQUEST_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.USERS_EMAIL_CHANGE_REQUEST_LIMIT,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.USERS_PASSWORD_CHANGE, httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: GetUserPartitionKey(httpContext),
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.USERS_PASSWORD_CHANGE_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.USERS_PASSWORD_CHANGE_LIMIT,
                    SegmentsPerWindow = RateLimitingConstants.Windows.SLIDING_WINDOW_SEGMENTS,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.ASSETS_UPLOAD, httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: GetUserPartitionKey(httpContext),
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.ASSETS_UPLOAD_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.ASSETS_UPLOAD_LIMIT,
                    SegmentsPerWindow = RateLimitingConstants.Windows.SLIDING_WINDOW_SEGMENTS,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.ASSETS_DOWNLOAD, httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: GetUserPartitionKey(httpContext),
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.ASSETS_DOWNLOAD_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.ASSETS_DOWNLOAD_LIMIT,
                    SegmentsPerWindow = RateLimitingConstants.Windows.SLIDING_WINDOW_SEGMENTS,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.PAYMENTS_CHECKOUT, httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: GetUserPartitionKey(httpContext),
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.PAYMENTS_CHECKOUT_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.PAYMENTS_CHECKOUT_LIMIT,
                    SegmentsPerWindow = RateLimitingConstants.Windows.SLIDING_WINDOW_SEGMENTS,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.LISTING_COPILOT_ENQUEUE, httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: GetUserPartitionKey(httpContext),
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.LISTING_COPILOT_ENQUEUE_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.LISTING_COPILOT_ENQUEUE_LIMIT,
                    SegmentsPerWindow = RateLimitingConstants.Windows.SLIDING_WINDOW_SEGMENTS,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.ADMIN_OUTBOX_REPLAY, httpContext =>
            RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: GetUserPartitionKey(httpContext),
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.ADMIN_OUTBOX_REPLAY_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.ADMIN_OUTBOX_REPLAY_LIMIT,
                    SegmentsPerWindow = RateLimitingConstants.Windows.SLIDING_WINDOW_SEGMENTS,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));
    }

    private static string GetAnalyticsEventsPartitionKey(HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(
                AnalyticsRateLimitContextKeys.VERIFIED_BFF_PARTITION,
                out var partitionObj)
            && partitionObj is string verifiedPartition
            && !string.IsNullOrWhiteSpace(verifiedPartition))
        {
            return "bff:" + verifiedPartition;
        }

        if (httpContext.Connection.RemoteIpAddress is { } remoteIp)
        {
            return "direct:" + remoteIp;
        }

        var connectionId = httpContext.Connection.Id;
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            return "direct:conn:" + connectionId;
        }

        // Middleware should short-circuit analytics POSTs without a stable identifier before
        // the rate limiter. This fallback must never be a per-request TraceIdentifier.
        return "direct:conn:missing";
    }

    private static void AddTelemetryPolicies(RateLimiterOptions opts)
    {
        opts.AddPolicy(RateLimitingConstants.Policies.ANALYTICS_EVENTS, httpContext =>
        {
            IAnalyticsDistributedRateLimiter distributedLimiter = httpContext.RequestServices.GetRequiredService<IAnalyticsDistributedRateLimiter>();
            TimeProvider timeProvider = httpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            return RateLimitPartition.Get(
                GetAnalyticsEventsPartitionKey(httpContext),
                partitionKey => new AnalyticsDistributedRateLimiterAdapter(
                    distributedLimiter,
                    AnalyticsRateLimitPolicy.ANALYTICS_EVENTS,
                    partitionKey,
                    timeProvider));
        });
    }

    private static void AddSellerAnalyticsPolicies(RateLimiterOptions opts)
    {
        opts.AddPolicy(RateLimitingConstants.Policies.SELLER_ANALYTICS_SALES_EXPORT, httpContext =>
        {
            var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? httpContext.User.FindFirstValue(JwtClaimTypes.SUB);
            if (string.IsNullOrEmpty(userId))
            {
                var connectionId = httpContext.Connection.Id;
                return RateLimitPartition.GetNoLimiter(
                    string.IsNullOrWhiteSpace(connectionId)
                        ? "seller-analytics-export:anon:missing"
                        : "seller-analytics-export:anon:" + connectionId);
            }

            IAnalyticsDistributedRateLimiter distributedLimiter = httpContext.RequestServices.GetRequiredService<IAnalyticsDistributedRateLimiter>();
            TimeProvider timeProvider = httpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            return RateLimitPartition.Get(
                userId,
                partitionKey => new AnalyticsDistributedRateLimiterAdapter(
                    distributedLimiter,
                    AnalyticsRateLimitPolicy.SELLER_ANALYTICS_SALES_EXPORT,
                    partitionKey,
                    timeProvider));
        });
    }
}

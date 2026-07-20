using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.ProblemDetails;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace AssetBlock.WebApi.Extensions;

internal static class RateLimitingExtensions
{
    private const string UNKNOWN_PARTITION_KEY = "unknown";

    private static string GetUserPartitionKey(HttpContext httpContext) =>
        httpContext.User.FindFirst(JwtClaimTypes.SUB)?.Value
        ?? httpContext.Connection.RemoteIpAddress?.ToString()
        ?? UNKNOWN_PARTITION_KEY;

    private static void ConfigureRejectedHandler(RateLimiterOptions opts)
    {
        opts.OnRejected = async (context, _) =>
        {
            var problem = AssetBlockProblemDetails.Create(
                context.HttpContext,
                StatusCodes.Status429TooManyRequests,
                ErrorCodes.ERR_RATE_LIMITED);
            await AssetBlockProblemDetails.Write(context.HttpContext, problem);
        };
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
                AddNoOpPolicy(RateLimitingConstants.Policies.USERS_EMAIL_VERIFICATION_RESEND);
                AddNoOpPolicy(RateLimitingConstants.Policies.USERS_EMAIL_CHANGE_REQUEST);
                AddNoOpPolicy(RateLimitingConstants.Policies.ASSETS_UPLOAD);
                AddNoOpPolicy(RateLimitingConstants.Policies.ASSETS_DOWNLOAD);
                AddNoOpPolicy(RateLimitingConstants.Policies.PAYMENTS_CHECKOUT);
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
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? UNKNOWN_PARTITION_KEY,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.AUTH_REGISTER_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.AUTH_REGISTER_LIMIT,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.AUTH_LOGIN, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? UNKNOWN_PARTITION_KEY,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.AUTH_LOGIN_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.AUTH_LOGIN_LIMIT,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.AUTH_REFRESH, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? UNKNOWN_PARTITION_KEY,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.AUTH_REFRESH_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.AUTH_REFRESH_LIMIT,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.AUTH_PASSWORD_RESET_REQUEST, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? UNKNOWN_PARTITION_KEY,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.AUTH_PASSWORD_RESET_REQUEST_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.AUTH_PASSWORD_RESET_REQUEST_LIMIT,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));

        opts.AddPolicy(RateLimitingConstants.Policies.AUTH_EMAIL_ACTION_CONFIRM, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? UNKNOWN_PARTITION_KEY,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.AUTH_EMAIL_ACTION_CONFIRM_PERIOD_SECONDS),
                    PermitLimit = RateLimitingConstants.Windows.AUTH_EMAIL_ACTION_CONFIRM_LIMIT,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                }));
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
    }
}

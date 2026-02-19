using AssetBlock.Domain.Core.Constants;
using System.Threading.RateLimiting;

namespace AssetBlock.WebApi.Extensions;

internal static class RateLimitingExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(opts =>
        {
            opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            opts.AddPolicy(RateLimitingConstants.Policies.AUTH_REGISTER, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.AUTH_REGISTER_PERIOD_SECONDS),
                        PermitLimit = RateLimitingConstants.Windows.AUTH_REGISTER_LIMIT,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));

            opts.AddPolicy(RateLimitingConstants.Policies.AUTH_LOGIN, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.AUTH_LOGIN_PERIOD_SECONDS),
                        PermitLimit = RateLimitingConstants.Windows.AUTH_LOGIN_LIMIT,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));

            opts.AddPolicy(RateLimitingConstants.Policies.ASSETS_UPLOAD, httpContext =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: httpContext.User.FindFirst(JwtClaimTypes.SUB)?.Value ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.ASSETS_UPLOAD_PERIOD_SECONDS),
                        PermitLimit = RateLimitingConstants.Windows.ASSETS_UPLOAD_LIMIT,
                        SegmentsPerWindow = 6,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));

            opts.AddPolicy(RateLimitingConstants.Policies.ASSETS_DOWNLOAD, httpContext =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: httpContext.User.FindFirst(JwtClaimTypes.SUB)?.Value ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.ASSETS_DOWNLOAD_PERIOD_SECONDS),
                        PermitLimit = RateLimitingConstants.Windows.ASSETS_DOWNLOAD_LIMIT,
                        SegmentsPerWindow = 6,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));

            opts.AddPolicy(RateLimitingConstants.Policies.PAYMENTS_CHECKOUT, httpContext =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: httpContext.User.FindFirst(JwtClaimTypes.SUB)?.Value ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.PAYMENTS_CHECKOUT_PERIOD_SECONDS),
                        PermitLimit = RateLimitingConstants.Windows.PAYMENTS_CHECKOUT_LIMIT,
                        SegmentsPerWindow = 6,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    }));
        });

        return services;
    }
}

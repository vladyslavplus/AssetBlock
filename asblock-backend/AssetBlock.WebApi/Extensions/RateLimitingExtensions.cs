using AssetBlock.Domain.Core.Constants;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace AssetBlock.WebApi.Extensions;

internal static class RateLimitingExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(opts =>
        {
            opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            opts.AddFixedWindowLimiter(RateLimitingConstants.Policies.AUTH_REGISTER, o =>
            {
                o.Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.AUTH_REGISTER_PERIOD_SECONDS);
                o.PermitLimit = RateLimitingConstants.Windows.AUTH_REGISTER_LIMIT;
                o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });

            opts.AddFixedWindowLimiter(RateLimitingConstants.Policies.AUTH_LOGIN, o =>
            {
                o.Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.AUTH_LOGIN_PERIOD_SECONDS);
                o.PermitLimit = RateLimitingConstants.Windows.AUTH_LOGIN_LIMIT;
                o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });

            opts.AddSlidingWindowLimiter(RateLimitingConstants.Policies.ASSETS_UPLOAD, o =>
            {
                o.Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.ASSETS_UPLOAD_PERIOD_SECONDS);
                o.PermitLimit = RateLimitingConstants.Windows.ASSETS_UPLOAD_LIMIT;
                o.SegmentsPerWindow = 6;
                o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });

            opts.AddSlidingWindowLimiter(RateLimitingConstants.Policies.ASSETS_DOWNLOAD, o =>
            {
                o.Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.ASSETS_DOWNLOAD_PERIOD_SECONDS);
                o.PermitLimit = RateLimitingConstants.Windows.ASSETS_DOWNLOAD_LIMIT;
                o.SegmentsPerWindow = 6;
                o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });

            opts.AddSlidingWindowLimiter(RateLimitingConstants.Policies.PAYMENTS_CHECKOUT, o =>
            {
                o.Window = TimeSpan.FromSeconds(RateLimitingConstants.Windows.PAYMENTS_CHECKOUT_PERIOD_SECONDS);
                o.PermitLimit = RateLimitingConstants.Windows.PAYMENTS_CHECKOUT_LIMIT;
                o.SegmentsPerWindow = 6;
                o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
        });

        return services;
    }
}

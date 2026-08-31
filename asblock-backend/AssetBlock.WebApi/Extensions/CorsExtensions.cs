using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.Configuration;

namespace AssetBlock.WebApi.Extensions;

public static class CorsExtensions
{
    private const string ASSETBLOCK_CORS_POLICY = "AssetBlockCors";

    private static readonly string[] _allowedMethods =
    [
        "GET",
        "POST",
        "PUT",
        "PATCH",
        "DELETE",
        "OPTIONS",
        "HEAD"
    ];

    private static readonly string[] _allowedHeaders =
    [
        "Authorization",
        "Content-Type",
        "Accept",
        "Origin",
        "X-Requested-With",
        "X-SignalR-User-Agent",
        "x-ms-signalr-request-id",
        AnalyticsBffRateLimitHeaders.PARTITION,
        AnalyticsBffRateLimitHeaders.TIMESTAMP,
        AnalyticsBffRateLimitHeaders.SIGNATURE
    ];

    /// <summary>
    /// Registers CORS for browser access to the API. When <see cref="CorsOptions.AllowedOrigins"/> is empty
    /// and the host is Development, falls back to http://localhost:3000.
    /// </summary>
    public static IServiceCollection AddAssetBlockCors(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        IConfigurationSection section = configuration.GetSection(CorsOptions.SECTION_NAME);
        CorsOptions cors = section.Get<CorsOptions>() ?? new CorsOptions();
        var origins = cors.AllowedOrigins.Where(static o => !string.IsNullOrWhiteSpace(o))
            .Select(static o => o.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (origins.Length == 0)
        {
            if (environment.IsDevelopment() || environment.IsEnvironment("IntegrationTesting"))
            {
                origins = ["http://localhost:3000"];
            }
            else
            {
                throw new InvalidOperationException(
                    "Configuration Cors:AllowedOrigins must list at least one origin for browser clients (e.g. your Next.js site URL).");
            }
        }

        services.AddCors(options =>
        {
            options.AddPolicy(ASSETBLOCK_CORS_POLICY, policy =>
            {
                policy
                    .WithOrigins(origins)
                    .WithMethods(_allowedMethods)
                    .WithHeaders(_allowedHeaders)
                    .AllowCredentials();
            });
        });

        return services;
    }

    public static IApplicationBuilder UseAssetBlockCors(this IApplicationBuilder app)
    {
        return app.UseCors(ASSETBLOCK_CORS_POLICY);
    }
}

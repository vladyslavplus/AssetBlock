using AssetBlock.WebApi.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AssetBlock.WebApi.Extensions;

public static class HealthCheckExtensions
{
    private const string LIVE_TAG = "live";
    private const string READY_TAG = "ready";
    private static readonly TimeSpan _dependencyTimeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddAssetBlockHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var builder = services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LIVE_TAG])
            .AddCheck<PostgreSqlHealthCheck>(
                "postgresql",
                failureStatus: HealthStatus.Unhealthy,
                tags: [READY_TAG],
                timeout: _dependencyTimeout)
            .AddCheck<MinioHealthCheck>(
                "minio",
                failureStatus: HealthStatus.Unhealthy,
                tags: [READY_TAG],
                timeout: _dependencyTimeout);

        if (IsConfigured(configuration.GetConnectionString("Redis")))
        {
            builder.AddCheck<RedisHealthCheck>(
                "redis",
                failureStatus: HealthStatus.Unhealthy,
                tags: [READY_TAG],
                timeout: _dependencyTimeout);
        }

        return services;
    }

    public static IEndpointRouteBuilder MapAssetBlockHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains(LIVE_TAG),
                ResponseWriter = WriteResponse
            })
            .AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains(READY_TAG),
                ResponseWriter = WriteResponse
            })
            .AllowAnonymous();

        return endpoints;
    }

    private static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 2),
                    description = entry.Value.Description
                })
        };
        return context.Response.WriteAsJsonAsync(body, cancellationToken: context.RequestAborted);
    }

    private static bool IsConfigured(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return !(trimmed.StartsWith('<') && trimmed.Contains('>'));
    }
}

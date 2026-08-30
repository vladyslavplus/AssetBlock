using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace AssetBlock.WebApi.HealthChecks;

internal sealed class PostgreSqlHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("PostgreSQL is unreachable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL readiness check failed.", ex);
        }
    }
}

/// <summary>
/// Readiness probe for the active asset storage provider via IAssetStorageService.
/// Uses a cheap non-mutating prefix listing; does not depend on provider SDKs or options.
/// </summary>
internal sealed class StorageHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    private const string READINESS_PREFIX = "__health__/";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var storage = scope.ServiceProvider.GetRequiredService<IAssetStorageService>();
            await using var enumerator = storage.ListObjects(READINESS_PREFIX, cancellationToken).GetAsyncEnumerator(cancellationToken);
            _ = await enumerator.MoveNextAsync();
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("The configured storage provider is unavailable.", ex);
        }
    }
}

internal sealed class RedisHealthCheck(IConnectionMultiplexer connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await connection.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis readiness check failed.", ex);
        }
    }
}

internal sealed class ClamAvHealthCheck(IContentMalwareScanner scanner) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var state = await scanner.GetSignatureState(cancellationToken);
            if (!state.IsAvailable)
            {
                return HealthCheckResult.Unhealthy("Malware scanner readiness check failed.");
            }

            if (!state.IsFresh)
            {
                return HealthCheckResult.Unhealthy("Malware scanner signatures are stale.");
            }

            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Malware scanner readiness check failed.");
        }
    }
}

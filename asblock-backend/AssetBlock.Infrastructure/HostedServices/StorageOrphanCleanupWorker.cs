using AssetBlock.Domain.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.HostedServices;

/// <summary>
/// Daily cleanup of MinIO objects under assets/ with no matching Asset row and older than 24 hours.
/// </summary>
internal sealed class StorageOrphanCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<StorageOrphanCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan _orphanAge = TimeSpan.FromHours(24);
    private const string ASSETS_PREFIX = "assets/";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("IntegrationTesting"))
        {
            logger.LogInformation("StorageOrphanCleanupWorker skipped in IntegrationTesting.");
            return;
        }

        logger.LogInformation("StorageOrphanCleanupWorker started");

        // Small delay so startup traffic settles before first scan.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanup(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "StorageOrphanCleanupWorker cycle failed");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task RunCleanup(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IAssetStorageService>();
        var assetStore = scope.ServiceProvider.GetRequiredService<IAssetStore>();

        var objects = await storage.ListObjects(ASSETS_PREFIX, cancellationToken);
        var cutoff = DateTimeOffset.UtcNow - _orphanAge;

        foreach (var obj in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (obj.LastModified is null || obj.LastModified > cutoff)
            {
                continue;
            }

            try
            {
                if (await assetStore.ExistsByStorageKey(obj.Key, cancellationToken))
                {
                    continue;
                }

                await storage.Delete(obj.Key, cancellationToken);
                logger.LogInformation("Deleted orphan storage object {Key} (LastModified={LastModified})",
                    obj.Key, obj.LastModified);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to evaluate/delete orphan object {Key}", obj.Key);
            }
        }
    }
}

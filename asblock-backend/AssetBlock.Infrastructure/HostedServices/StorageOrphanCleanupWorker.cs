using System.Diagnostics;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.Storage;
using AssetBlock.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.HostedServices;

/// <summary>
/// Daily cleanup of storage objects under assets/ with no matching Asset row and older than 24 hours.
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
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IAssetStorageService storage = scope.ServiceProvider.GetRequiredService<IAssetStorageService>();
        IAssetStore assetStore = scope.ServiceProvider.GetRequiredService<IAssetStore>();

        var stopwatch = Stopwatch.StartNew();
        var deletedCount = 0;
        var failedCount = 0;
        DiagnosticsOutcome outcome = DiagnosticsOutcome.SUCCESS;

        try
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - _orphanAge;

            await foreach (StorageObjectInfo obj in storage.ListObjects(ASSETS_PREFIX, cancellationToken))
            {
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
                    deletedCount++;
                    logger.LogInformation("Deleted orphan storage object {Key} (LastModified={LastModified})",
                        obj.Key, obj.LastModified);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    outcome = DiagnosticsOutcome.PARTIAL_FAILURE;
                    logger.LogWarning(ex, "Failed to evaluate/delete orphan object {Key}", obj.Key);
                }
            }
        }
        catch (OperationCanceledException)
        {
            outcome = DiagnosticsOutcome.CANCELLED;
            throw;
        }
        catch (Exception)
        {
            outcome = DiagnosticsOutcome.FAILURE;
            failedCount = Math.Max(failedCount, 1);
            throw;
        }
        finally
        {
            AssetBlockDiagnostics.RecordOrphanCleanup(stopwatch.Elapsed, outcome, deletedCount, failedCount);
        }
    }
}

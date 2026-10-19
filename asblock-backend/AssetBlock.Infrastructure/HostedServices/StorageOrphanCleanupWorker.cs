using System.Diagnostics;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
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
    ILogger<StorageOrphanCleanupWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan _orphanAge = TimeSpan.FromHours(24);
    private const string ASSETS_PREFIX = "assets/";
    private const int MAX_OBJECTS_PER_CYCLE = 250;
    private static readonly TimeSpan _leaseDuration = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
        IStorageOrphanCleanupStateStore stateStore = scope.ServiceProvider.GetRequiredService<IStorageOrphanCleanupStateStore>();

        var stopwatch = Stopwatch.StartNew();
        var deletedCount = 0;
        var failedCount = 0;
        DiagnosticsOutcome outcome = DiagnosticsOutcome.SUCCESS;

        try
        {
            DateTimeOffset cutoff = _timeProvider.GetUtcNow() - _orphanAge;
            StorageOrphanCleanupLease? lease = await stateStore.TryAcquire(
                _timeProvider.GetUtcNow(), _leaseDuration, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                logger.LogDebug("Storage orphan cleanup skipped because another worker holds the scan lease.");
                return;
            }

            var resetCursor = false;
            StorageObjectCursor? cursor = lease.Cursor;
            try
            {
                if (!await stateStore.SaveCursor(
                    lease.LeaseId, cursor, _timeProvider.GetUtcNow(), _leaseDuration, cancellationToken).ConfigureAwait(false))
                {
                    logger.LogWarning("Storage orphan cleanup lost its scan lease before provider listing.");
                    return;
                }

                // The S3 version-list API owns pagination. A page is capped to the full per-cycle
                // processing budget, and the durable cursor advances only after every item in it
                // has been checked. Crashes replay at most one page of idempotent exact-version work.
                StorageObjectPage page = await storage.ListObjectVersionsPage(
                    ASSETS_PREFIX, cursor, MAX_OBJECTS_PER_CYCLE, cancellationToken).ConfigureAwait(false);
                if (page.Items.Count > MAX_OBJECTS_PER_CYCLE)
                {
                    throw new InvalidDataException("Storage provider exceeded the bounded version-list page size.");
                }

                foreach (StorageObjectInfo obj in page.Items)
                {
                    if (!await stateStore.SaveCursor(
                        lease.LeaseId, cursor, _timeProvider.GetUtcNow(), _leaseDuration, cancellationToken).ConfigureAwait(false))
                    {
                        logger.LogWarning("Storage orphan cleanup lost its scan lease; stopping before further deletion.");
                        return;
                    }

                    try
                    {
                        if (!obj.IsDeleteMarker && obj.LastModified is not null && obj.LastModified <= cutoff)
                        {
                            if (!StorageVersionIdentity.IsImmutable(obj.VersionId))
                            {
                                failedCount++;
                                outcome = DiagnosticsOutcome.PARTIAL_FAILURE;
                                logger.LogError("Retaining orphan object {Key}: storage listing returned no exact generation ID", obj.Key);
                            }
                            else if (!await assetStore.ExistsByStorageKey(obj.Key, cancellationToken).ConfigureAwait(false))
                            {
                                // Refresh the lease, then recheck relational protection immediately before exact-generation deletion.
                                if (!await stateStore.SaveCursor(
                                    lease.LeaseId, cursor, _timeProvider.GetUtcNow(), _leaseDuration, cancellationToken).ConfigureAwait(false))
                                {
                                    logger.LogWarning("Storage orphan cleanup lost its scan lease; stopping before deletion of {Key}.", obj.Key);
                                    return;
                                }

                                if (!await assetStore.ExistsByStorageKey(obj.Key, cancellationToken).ConfigureAwait(false))
                                {
                                    await storage.Delete(obj.Key, obj.VersionId!, cancellationToken).ConfigureAwait(false);
                                    deletedCount++;
                                    logger.LogInformation("Deleted orphan storage object {Key} (LastModified={LastModified})",
                                        obj.Key, obj.LastModified);
                                }
                            }
                        }
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

                StorageObjectCursor? nextCursor = page.NextCursor;
                if (!page.IsComplete && (nextCursor is null || nextCursor == lease.Cursor))
                {
                    throw new InvalidDataException("Truncated storage version listing did not advance its durable provider markers.");
                }

                resetCursor = page.IsComplete;
                if (!await stateStore.SaveCursor(
                    lease.LeaseId, resetCursor ? null : nextCursor, _timeProvider.GetUtcNow(), _leaseDuration, cancellationToken).ConfigureAwait(false))
                {
                    logger.LogWarning("Storage orphan cleanup lost its scan lease after processing a provider page; stopping.");
                    return;
                }
            }
            finally
            {
                await stateStore.Release(lease.LeaseId, resetCursor, CancellationToken.None).ConfigureAwait(false);
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

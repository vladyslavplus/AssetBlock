using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.HostedServices;

/// <summary>Removes unsubmitted, unprotected upload work after every version passes its 30-day clock.</summary>
internal sealed class AbandonedUploadRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<AbandonedUploadRetentionWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan _retention = TimeSpan.FromDays(30);
    private const int BATCH_SIZE = 100;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("IntegrationTesting"))
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            { await RunCleanup(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "AbandonedUploadRetentionWorker cycle failed"); }
            try
            { await Task.Delay(_interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    internal async Task RunCleanup(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        IAssetStore assets = services.GetRequiredService<IAssetStore>();
        IPurchaseStore purchases = services.GetRequiredService<IPurchaseStore>();
        ICheckoutIntentStore checkouts = services.GetRequiredService<ICheckoutIntentStore>();
        ICheckoutReconciliationHoldStore holds = services.GetRequiredService<ICheckoutReconciliationHoldStore>();
        IUnitOfWork unitOfWork = services.GetRequiredService<IUnitOfWork>();
        IOutboxStore outbox = services.GetRequiredService<IOutboxStore>();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset cutoff = now - _retention;
        var unknownIdentityCount = await assets.CountAbandonedUploadsWithUnknownStorageIdentity(cutoff, cancellationToken);
        if (unknownIdentityCount > 0)
        {
            logger.LogError("Retaining {Count} abandoned uploads with unknown storage generation; they are excluded from the bounded cleanup batch", unknownIdentityCount);
        }
        IReadOnlyList<Guid> candidates = await assets.GetAbandonedUploadAssetIds(cutoff, BATCH_SIZE, cancellationToken);

        foreach (Guid assetId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await unitOfWork.ExecuteInTransaction(async ct =>
                {
                    Asset? asset = await assets.GetForUpdate(assetId, ct);
                    if (asset is null || asset.DeletedAt is not null)
                    {
                        return;
                    }

                    // Fail closed for every durable obligation. A rejected submission remains
                    // protected because the schema has no closed-appeal timestamp.
                    IReadOnlyList<AssetBlobReference> blobs = await assets.GetAllStorageKeys(assetId, ct);
                    if (blobs.Count == 0 || blobs.Any(blob => blob.CreatedAt > cutoff))
                    {
                        return;
                    }

                    if (await assets.HasAbandonedUploadRetentionProtection(assetId, ct)
                        || await assets.HasActiveProcessingWork(assetId, ct)
                        || await purchases.HasPurchasesForAsset(assetId, ct)
                        || await checkouts.HasActiveForAsset(assetId, now, ct)
                        || await checkouts.HasProviderBoundUnresolvedCheckoutReference(assetId, ct)
                        || await holds.HasUnresolvedHoldForAsset(assetId, ct))
                    {
                        return;
                    }

                    if (blobs.Any(blob => !StorageVersionIdentity.IsImmutable(blob.StorageVersionId)))
                    {
                        logger.LogError("Retaining abandoned upload {AssetId}: at least one version has unknown storage generation", assetId);
                        return;
                    }

                    await checkouts.DeleteTerminalUnpaidReferencingAsset(assetId, ct);
                    // Keep exact version, security evidence, and analysis input identities. The
                    // tombstone closes comparison/admission immediately; only encrypted payloads
                    // are retired and removed by exact-generation outbox work.
                    await assets.RetireAbandonedUpload(assetId, now, ct);
                    foreach (AssetBlobReference blob in blobs)
                    {
                        var storageVersionId = blob.StorageVersionId;
                        if (!StorageVersionIdentity.IsImmutable(storageVersionId))
                        {
                            throw new InvalidOperationException("Storage generation changed during abandoned upload retirement.");
                        }
                        await outbox.Enqueue(OutboxMessageTypes.ASSET_BLOB_DELETE,
                            new AssetBlobDeletePayload(assetId, blob.AssetVersionId, blob.StorageKey, storageVersionId), ct);
                    }
                    logger.LogInformation("Removed unsubmitted abandoned upload {AssetId}; queued {BlobCount} exact-version blob deletions", assetId, blobs.Count);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Deferred abandoned upload cleanup for {AssetId}", assetId); }
        }
    }
}

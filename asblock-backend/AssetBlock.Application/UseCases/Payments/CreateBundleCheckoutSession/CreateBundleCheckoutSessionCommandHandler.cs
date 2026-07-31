using AssetBlock.Application.UseCases.Payments.Checkout;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Payments;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Payments.CreateBundleCheckoutSession;

internal sealed class CreateBundleCheckoutSessionCommandHandler(
    IBundleStore bundleStore,
    IPurchaseStore purchaseStore,
    ICheckoutIntentStore checkoutIntentStore,
    CheckoutSessionOrchestrator checkoutSessionOrchestrator,
    CheckoutAttributionNormalizer attributionNormalizer)
    : IRequestHandler<CreateBundleCheckoutSessionCommand, Result<CreateCheckoutSessionResponse>>
{
    public Task<Result<CreateCheckoutSessionResponse>> Handle(
        CreateBundleCheckoutSessionCommand request,
        CancellationToken cancellationToken)
    {
        return checkoutSessionOrchestrator.Execute(
            ct => PrepareDraft(request, ct),
            ct => checkoutIntentStore.GetPendingForBundle(request.UserId, request.BundleId, ct),
            cancellationToken);
    }

    private async Task<Result<CheckoutDraft>> PrepareDraft(
        CreateBundleCheckoutSessionCommand request,
        CancellationToken cancellationToken)
    {
        var lockedBundle = await bundleStore.LockForUpdate(request.BundleId, cancellationToken);
        if (lockedBundle is null || lockedBundle.ArchivedAt.HasValue)
        {
            return Result.NotFound(ErrorCodes.ERR_BUNDLE_NOT_FOUND);
        }

        var snapshot = await bundleStore.GetCheckoutSnapshot(request.BundleId, cancellationToken);
        if (snapshot is null)
        {
            return Result.NotFound(ErrorCodes.ERR_BUNDLE_NOT_FOUND);
        }

        if (snapshot.SellerId == request.UserId)
        {
            return Result.Forbidden(ErrorCodes.ERR_CANNOT_PURCHASE_OWN_BUNDLE);
        }

        if (snapshot.Items.Count == 0)
        {
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
        }

        var assetIds = snapshot.Items.Select(i => i.AssetId).OrderBy(id => id).ToArray();
        await bundleStore.LockAssetsInOrder(assetIds, cancellationToken);

        // Re-read after asset locks so soft/hard deletes and current versions are fresh.
        snapshot = await bundleStore.GetCheckoutSnapshot(request.BundleId, cancellationToken);
        if (snapshot is null || snapshot.Items.Count == 0)
        {
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
        }

        assetIds = snapshot.Items.Select(i => i.AssetId).OrderBy(id => id).ToArray();
        if (await purchaseStore.ExistsAny(request.UserId, assetIds, cancellationToken))
        {
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_CONTAINS_OWNED_ASSET);
        }

        long bundleTotalCents;
        try
        {
            bundleTotalCents = BundlePriceAllocator.ToCents(snapshot.Price);
        }
        catch (ArgumentException)
        {
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
        }

        IReadOnlyList<BundlePriceAllocator.AllocationResult> allocations;
        try
        {
            allocations = BundlePriceAllocator.Allocate(
                bundleTotalCents,
                snapshot.Items
                    .Select(i => new BundlePriceAllocator.AllocationInput(
                        i.AssetId,
                        i.Position,
                        BundlePriceAllocator.ToCents(i.ListPrice)))
                    .ToArray());
        }
        catch (ArgumentException)
        {
            return Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
        }

        var allocatedByAsset = allocations.ToDictionary(a => a.AssetId);
        var draftItems = snapshot.Items
            .OrderBy(i => i.Position)
            .Select(i =>
            {
                var allocated = allocatedByAsset[i.AssetId];
                return new CheckoutDraftItem(
                    i.AssetId,
                    i.AssetVersionId,
                    snapshot.SellerId,
                    i.Position,
                    i.AssetTitle,
                    i.VersionNumber,
                    i.ListPrice,
                    BundlePriceAllocator.FromCents(allocated.AllocatedCents),
                    i.LicenseCode,
                    i.LicenseTemplateVersion,
                    i.LicenseDisplayName,
                    i.LicenseTerms);
            })
            .ToList();

        // No asset id is passed, so COLLECTION attribution can never survive a bundle checkout.
        var attribution = await attributionNormalizer.TryNormalize(
            request.Attribution,
            assetId: null,
            snapshot.SellerId,
            request.AnalyticsVisitorId,
            request.AnalyticsSessionId,
            cancellationToken);

        return Result.Success(new CheckoutDraft(
            request.UserId,
            AssetId: null,
            snapshot.BundleId,
            snapshot.BundleRevisionId,
            snapshot.Title,
            snapshot.Price,
            snapshot.Currency,
            draftItems,
            attribution));
    }
}
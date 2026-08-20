using AssetBlock.Application.UseCases.Payments.Checkout;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Licenses;
using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;

internal sealed class CreateCheckoutSessionCommandHandler(
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    ICheckoutIntentStore checkoutIntentStore,
    CheckoutSessionOrchestrator checkoutSessionOrchestrator,
    CheckoutAttributionNormalizer attributionNormalizer)
    : IRequestHandler<CreateCheckoutSessionCommand, Result<CreateCheckoutSessionResponse>>
{
    public Task<Result<CreateCheckoutSessionResponse>> Handle(
        CreateCheckoutSessionCommand request,
        CancellationToken cancellationToken)
    {
        return checkoutSessionOrchestrator.Execute(
            ct => PrepareDraft(request, ct),
            ct => checkoutIntentStore.GetPendingForAsset(request.UserId, request.AssetId, ct),
            cancellationToken);
    }

    private async Task<Result<CheckoutDraft>> PrepareDraft(
        CreateCheckoutSessionCommand request,
        CancellationToken cancellationToken)
    {
        var locked = await assetStore.GetForUpdate(request.AssetId, cancellationToken);
        if (locked is null || locked.DeletedAt.HasValue)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        var snapshot = await assetStore.GetCurrentVersionSnapshot(request.AssetId, cancellationToken);
        if (snapshot is null || snapshot.DeletedAt.HasValue)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (snapshot.AuthorId == request.UserId)
        {
            return Result.Forbidden(ErrorCodes.ERR_CANNOT_PURCHASE_OWN_ASSET);
        }

        var existingPurchase = await purchaseStore.GetPurchase(request.UserId, request.AssetId, cancellationToken);
        if (existingPurchase is not null)
        {
            return Result.Conflict(ErrorCodes.ERR_ASSET_ALREADY_PURCHASED);
        }

        if (!AssetLicenseCatalog.TryParseCode(snapshot.LicenseCode, out var licenseCode))
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        var attribution = await attributionNormalizer.TryNormalize(
            request.Attribution,
            snapshot.AssetId,
            snapshot.AuthorId,
            request.AnalyticsVisitorId,
            request.AnalyticsSessionId,
            cancellationToken);

        return Result.Success(new CheckoutDraft(
            request.UserId,
            snapshot.AssetId,
            BundleId: null,
            BundleRevisionId: null,
            snapshot.Title,
            snapshot.Price,
            StripeConstants.CURRENCY_USD,
            [
                new CheckoutDraftItem(
                    snapshot.AssetId,
                    snapshot.AssetVersionId,
                    snapshot.AuthorId,
                    Position: 1,
                    snapshot.Title,
                    snapshot.VersionNumber,
                    snapshot.Price,
                    snapshot.Price,
                    licenseCode,
                    snapshot.LicenseTemplateVersion,
                    snapshot.LicenseDisplayName,
                    snapshot.LicenseTerms)
            ],
            attribution));
    }
}

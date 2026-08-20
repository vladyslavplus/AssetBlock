using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Bundles.CreateBundle;

internal sealed class CreateBundleCommandHandler(
    IBundleStore bundleStore,
    IAssetStore assetStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<CreateBundleCommandHandler> logger)
    : IRequestHandler<CreateBundleCommand, Result<CreateBundleResponse>>
{
    public async Task<Result<CreateBundleResponse>> Handle(CreateBundleCommand request, CancellationToken cancellationToken)
    {
        var title = request.Title.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        Result<CreateBundleResponse>? failure = null;
        CreateBundleResponse? response = null;

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            var prepared = await BundleRevisionDraftBuilder.Build(
                bundleStore,
                assetStore,
                request.SellerId,
                request.AssetIds,
                request.Price,
                ct);

            if (!prepared.IsSuccess)
            {
                var code = prepared.ValidationErrors.FirstOrDefault()?.Identifier
                    ?? ErrorCodes.ERR_BUNDLE_ASSET_INVALID;
                failure = ResultError.Error<CreateBundleResponse>(code);
                return;
            }

            (var listPriceTotal, IReadOnlyList<BundleRevisionItemDraft> items) = prepared.Value;
            (Bundle bundle, BundleRevision revision) = await bundleStore.CreateWithRevision(
                request.SellerId,
                title,
                description,
                request.Price,
                StripeConstants.CURRENCY_USD,
                listPriceTotal,
                items,
                ct);

            await auditWriter.Write(new AuditEvent(
                AuditActions.BUNDLE_CREATE,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.BUNDLE,
                bundle.Id.ToString(),
                new Dictionary<string, object?>
                {
                    ["revisionId"] = revision.Id.ToString(),
                    ["revisionNumber"] = revision.RevisionNumber,
                    ["itemCount"] = items.Count
                }), ct);

            response = new CreateBundleResponse(bundle.Id, revision.Id, revision.RevisionNumber);
        }, cancellationToken);

        if (failure is not null)
        {
            return failure;
        }

        logger.LogInformation(
            "Created bundle {BundleId} revision {RevisionId} for seller {SellerId}",
            response!.Id,
            response.RevisionId,
            request.SellerId);
        return Result.Success(response);
    }
}

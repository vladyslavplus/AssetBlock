using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Bundles.ReviseBundle;

internal sealed class ReviseBundleCommandHandler(
    IBundleStore bundleStore,
    IAssetStore assetStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<ReviseBundleCommandHandler> logger)
    : IRequestHandler<ReviseBundleCommand, Result<ReviseBundleResponse>>
{
    public async Task<Result<ReviseBundleResponse>> Handle(ReviseBundleCommand request, CancellationToken cancellationToken)
    {
        var title = request.Title.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        Result<ReviseBundleResponse>? failure = null;
        ReviseBundleResponse? response = null;

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            Bundle? bundle = await bundleStore.LockForUpdate(request.BundleId, ct);
            if (bundle is null)
            {
                failure = Result.NotFound(ErrorCodes.ERR_BUNDLE_NOT_FOUND);
                return;
            }

            if (bundle.SellerId != request.SellerId)
            {
                await auditWriter.Write(new AuditEvent(
                    AuditActions.BUNDLE_REVISE,
                    AuditOutcome.DENIED,
                    AuditResourceTypes.BUNDLE,
                    request.BundleId.ToString()), ct);
                failure = Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
                return;
            }

            if (bundle.ArchivedAt.HasValue)
            {
                failure = Result.Conflict(ErrorCodes.ERR_BUNDLE_UNAVAILABLE);
                return;
            }

            Result<(decimal ListPriceTotal, IReadOnlyList<BundleRevisionItemDraft> Items)> prepared = await BundleRevisionDraftBuilder.Build(
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
                failure = ResultError.Error<ReviseBundleResponse>(code);
                return;
            }

            (var listPriceTotal, IReadOnlyList<BundleRevisionItemDraft>? items) = prepared.Value;
            BundleRevision revision = await bundleStore.PublishNextRevision(
                request.BundleId,
                title,
                description,
                request.Price,
                StripeConstants.CURRENCY_USD,
                listPriceTotal,
                items,
                ct);

            await auditWriter.Write(new AuditEvent(
                AuditActions.BUNDLE_REVISE,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.BUNDLE,
                request.BundleId.ToString(),
                new Dictionary<string, object?>
                {
                    ["revisionId"] = revision.Id.ToString(),
                    ["revisionNumber"] = revision.RevisionNumber,
                    ["itemCount"] = items.Count
                }), ct);

            response = new ReviseBundleResponse(request.BundleId, revision.Id, revision.RevisionNumber);
        }, cancellationToken);

        if (failure is not null)
        {
            return failure;
        }

        logger.LogInformation(
            "Revised bundle {BundleId} to revision {RevisionId} by seller {SellerId}",
            response!.Id,
            response.RevisionId,
            request.SellerId);
        return Result.Success(response);
    }
}

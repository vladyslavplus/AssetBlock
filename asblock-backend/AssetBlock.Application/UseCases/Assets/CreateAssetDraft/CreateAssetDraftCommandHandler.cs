using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.CreateAssetDraft;

internal sealed class CreateAssetDraftCommandHandler(
    ICategoryStore categoryStore,
    IModerationFoundationStore moderationFoundationStore,
    ILogger<CreateAssetDraftCommandHandler> logger)
    : IRequestHandler<CreateAssetDraftCommand, Result<SellerDraftCreatedDto>>
{
    public async Task<Result<SellerDraftCreatedDto>> Handle(CreateAssetDraftCommand request, CancellationToken cancellationToken)
    {
        Category? category = await categoryStore.GetById(request.CategoryId, cancellationToken);
        if (category is null)
        {
            return Result.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        }

        SellerDraftCreatedDto? draft = await moderationFoundationStore.CreateAssetDraft(
            new CreateAssetDraftRequest(
                request.OwnerId,
                request.OperationId,
                request.Title.Trim(),
                request.Description?.Trim(),
                request.CategoryId,
                request.Price,
                request.DownloadLimitPerHour,
                // The store writes this inside the mutation transaction; replays never re-write it.
                new AuditEvent(
                    AuditActions.ASSET_DRAFT_CREATE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.ASSET)),
            cancellationToken);

        if (draft is null)
        {
            // Same operation id with a different creation payload is a business conflict.
            return Result.Conflict(ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT);
        }

        logger.LogInformation("Draft workspace created for asset {AssetId} by {OwnerId}", draft.AssetId, request.OwnerId);
        return Result.Success(draft);
    }
}

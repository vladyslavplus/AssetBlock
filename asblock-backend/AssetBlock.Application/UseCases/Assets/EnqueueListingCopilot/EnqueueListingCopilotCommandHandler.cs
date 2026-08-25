using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.EnqueueListingCopilot;

internal sealed class EnqueueListingCopilotCommandHandler(
    IListingCopilotStore listingCopilotStore,
    IAssetProcessingJobStore jobStore,
    IAiGenerationProviderRegistry providers,
    IOptions<AiOptions> aiOptions) : IRequestHandler<EnqueueListingCopilotCommand, Result<ListingCopilotEnqueueResponse>>
{
    public async Task<Result<ListingCopilotEnqueueResponse>> Handle(
        EnqueueListingCopilotCommand request,
        CancellationToken cancellationToken)
    {
        var owned = await listingCopilotStore.GetOwnedVersion(request.AssetVersionId, request.OwnerUserId, cancellationToken);
        if (owned is null)
        {
            return Result.NotFound();
        }

        var options = aiOptions.Value;
        if (!options.Enabled)
        {
            return ResultError.Error<ListingCopilotEnqueueResponse>(ErrorCodes.AI_DISABLED);
        }

        if (!AiProviderParser.TryParse(options.Provider, out var providerKind)
            || !providers.TryGet(providerKind, out var provider)
            || provider.OrderedModelIds.Count == 0)
        {
            return ResultError.Error<ListingCopilotEnqueueResponse>(ErrorCodes.AI_ERROR);
        }

        if (owned.ProcessingStatus != AssetVersionProcessingStatus.READY)
        {
            return Result.Conflict(ErrorCodes.AI_VERSION_NOT_READY);
        }

        if (!owned.HasArchiveAnalysis)
        {
            return Result.Conflict(ErrorCodes.AI_ARCHIVE_ANALYSIS_MISSING);
        }

        var jobId = await jobStore.Enqueue(
            owned.AssetId,
            owned.AssetVersionId,
            AssetProcessingJobType.LISTING_COPILOT,
            AiPromptPolicies.LISTING_COPILOT_DEFINITION_VERSION,
            TimeSpan.Zero,
            new ListingCopilotPayload(AiPromptPolicies.LISTING_COPILOT_V1),
            cancellationToken: cancellationToken);

        return Result.Success(new ListingCopilotEnqueueResponse(jobId, owned.AssetVersionId));
    }
}

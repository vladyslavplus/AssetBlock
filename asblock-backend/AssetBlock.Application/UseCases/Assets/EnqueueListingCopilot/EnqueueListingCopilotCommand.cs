using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Application.UseCases.Assets.EnqueueListingCopilot;

public sealed record EnqueueListingCopilotCommand(Guid AssetVersionId, Guid OwnerUserId)
    : IRequest<Result<ListingCopilotEnqueueResponse>>;

using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Application.UseCases.Assets.GetListingCopilotSuggestion;

public sealed record GetListingCopilotSuggestionQuery(Guid AssetVersionId, Guid OwnerUserId)
    : IRequest<Result<ListingCopilotSuggestionDto>>;

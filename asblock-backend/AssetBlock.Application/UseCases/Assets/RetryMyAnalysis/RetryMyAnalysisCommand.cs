using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.RetryMyAnalysis;

public sealed record RetryMyAnalysisCommand(Guid AssetId, Guid AssetVersionId, Guid OwnerId, SellerAnalysisRetryRequest Request)
    : IRequest<Result<SellerAnalysisRetryAcceptedDto>>;

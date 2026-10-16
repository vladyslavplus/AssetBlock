using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.RetryMyAnalysis;

internal sealed class RetryMyAnalysisCommandHandler(ICodeAnalysisSellerStore store)
    : IRequestHandler<RetryMyAnalysisCommand, Result<SellerAnalysisRetryAcceptedDto>>
{
    public async Task<Result<SellerAnalysisRetryAcceptedDto>> Handle(RetryMyAnalysisCommand request, CancellationToken cancellationToken)
    {
        SellerAnalysisRetryStoreResult result = await store.Retry(
            request.AssetId, request.AssetVersionId, request.OwnerId, request.Request, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            SellerAnalysisRetryStatus.ACCEPTED or SellerAnalysisRetryStatus.RECHECK_QUEUED when result.Accepted is not null
                => Result.Success(result.Accepted),
            SellerAnalysisRetryStatus.NOT_FOUND => Result.NotFound(ErrorCodes.ERR_ASSET_VERSION_NOT_FOUND),
            _ => Result.Conflict(ErrorCodes.ERR_ANALYSIS_RETRY_CONFLICT)
        };
    }
}

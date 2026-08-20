using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Collections;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Collections.GetCollection;

internal sealed class GetCollectionQueryHandler(ICollectionStore collectionStore)
    : IRequestHandler<GetCollectionQuery, Result<CollectionDetailDto>>
{
    public async Task<Result<CollectionDetailDto>> Handle(GetCollectionQuery request, CancellationToken cancellationToken)
    {
        var detail = await collectionStore.GetPublicDetail(request.Id, cancellationToken);
        if (detail is null)
        {
            return Result.NotFound(ErrorCodes.ERR_COLLECTION_NOT_FOUND);
        }

        return Result.Success(detail);
    }
}

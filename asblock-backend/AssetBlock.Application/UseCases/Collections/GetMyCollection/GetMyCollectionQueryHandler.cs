using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Collections;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.GetMyCollection;

internal sealed class GetMyCollectionQueryHandler(ICollectionStore collectionStore)
    : IRequestHandler<GetMyCollectionQuery, Result<CollectionDetailDto>>
{
    public async Task<Result<CollectionDetailDto>> Handle(GetMyCollectionQuery request, CancellationToken cancellationToken)
    {
        var collection = await collectionStore.GetById(request.Id, cancellationToken);
        if (collection is null)
        {
            return Result.NotFound(ErrorCodes.ERR_COLLECTION_NOT_FOUND);
        }

        if (collection.SellerId != request.SellerId)
        {
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        var detail = await collectionStore.GetSellerDetail(request.Id, request.SellerId, cancellationToken);
        if (detail is null)
        {
            return Result.NotFound(ErrorCodes.ERR_COLLECTION_NOT_FOUND);
        }

        return Result.Success(detail);
    }
}

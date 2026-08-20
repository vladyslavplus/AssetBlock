using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Collections;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.GetMyCollections;

internal sealed class GetMyCollectionsQueryHandler(ICollectionStore collectionStore)
    : IRequestHandler<GetMyCollectionsQuery, Result<Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto>>> Handle(
        GetMyCollectionsQuery request,
        CancellationToken cancellationToken)
    {
        var paged = await collectionStore.ListForSeller(request.SellerId, request.Request, cancellationToken);
        return Result.Success(paged);
    }
}

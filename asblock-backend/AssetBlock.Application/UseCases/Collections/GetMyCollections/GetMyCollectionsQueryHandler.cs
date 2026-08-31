using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Collections;

namespace AssetBlock.Application.UseCases.Collections.GetMyCollections;

internal sealed class GetMyCollectionsQueryHandler(ICollectionStore collectionStore)
    : IRequestHandler<GetMyCollectionsQuery, Result<Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto>>> Handle(
        GetMyCollectionsQuery request,
        CancellationToken cancellationToken)
    {
        Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto> paged = await collectionStore.ListForSeller(request.SellerId, request.Request, cancellationToken);
        return Result.Success(paged);
    }
}

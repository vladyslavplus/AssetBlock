using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Collections;

namespace AssetBlock.Application.UseCases.Collections.GetCollections;

internal sealed class GetCollectionsQueryHandler(ICollectionStore collectionStore)
    : IRequestHandler<GetCollectionsQuery, Result<Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto>>> Handle(
        GetCollectionsQuery request,
        CancellationToken cancellationToken)
    {
        Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto> paged = await collectionStore.ListPublic(request.Request, cancellationToken);
        return Result.Success(paged);
    }
}

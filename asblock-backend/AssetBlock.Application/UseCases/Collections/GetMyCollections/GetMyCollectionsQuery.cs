using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Collections;

namespace AssetBlock.Application.UseCases.Collections.GetMyCollections;

public sealed record GetMyCollectionsQuery(Guid SellerId, ListMyCollectionsRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto>>>;

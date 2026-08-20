using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Collections;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Collections.GetCollections;

public sealed record GetCollectionsQuery(ListCollectionsRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto>>>;

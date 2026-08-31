using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Collections;

namespace AssetBlock.Application.UseCases.Collections.GetCollections;

public sealed record GetCollectionsQuery(ListCollectionsRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto>>>;

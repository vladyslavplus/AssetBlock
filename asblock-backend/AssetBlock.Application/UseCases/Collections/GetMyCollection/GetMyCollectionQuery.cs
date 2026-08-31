using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Collections;

namespace AssetBlock.Application.UseCases.Collections.GetMyCollection;

public sealed record GetMyCollectionQuery(Guid Id, Guid SellerId) : IRequest<Result<CollectionDetailDto>>;

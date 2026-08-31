using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Collections;

namespace AssetBlock.Application.UseCases.Collections.GetCollection;

public sealed record GetCollectionQuery(Guid Id) : IRequest<Result<CollectionDetailDto>>;

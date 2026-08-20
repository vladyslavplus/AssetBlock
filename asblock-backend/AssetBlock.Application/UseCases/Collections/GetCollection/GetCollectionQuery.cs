using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Collections;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.GetCollection;

public sealed record GetCollectionQuery(Guid Id) : IRequest<Result<CollectionDetailDto>>;

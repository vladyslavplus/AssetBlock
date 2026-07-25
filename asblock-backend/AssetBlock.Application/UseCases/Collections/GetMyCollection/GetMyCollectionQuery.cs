using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Collections;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.GetMyCollection;

public sealed record GetMyCollectionQuery(Guid Id, Guid SellerId) : IRequest<Result<CollectionDetailDto>>;

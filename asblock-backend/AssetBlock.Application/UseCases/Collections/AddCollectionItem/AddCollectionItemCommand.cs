using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.AddCollectionItem;

public sealed record AddCollectionItemCommand(Guid CollectionId, Guid SellerId, Guid AssetId)
    : IRequest<Result>;

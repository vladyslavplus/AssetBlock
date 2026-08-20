using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Collections.RemoveCollectionItem;

public sealed record RemoveCollectionItemCommand(Guid CollectionId, Guid SellerId, Guid AssetId)
    : IRequest<Result>;

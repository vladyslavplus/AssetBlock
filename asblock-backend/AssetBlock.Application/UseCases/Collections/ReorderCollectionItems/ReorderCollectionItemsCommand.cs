using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.ReorderCollectionItems;

public sealed record ReorderCollectionItemsCommand(
    Guid CollectionId,
    Guid SellerId,
    IReadOnlyList<Guid> AssetIds) : IRequest<Result>;

using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Collections.ArchiveCollection;

public sealed record ArchiveCollectionCommand(Guid Id, Guid SellerId) : IRequest<Result>;

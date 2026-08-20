using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Collections.PublishCollection;

public sealed record PublishCollectionCommand(Guid Id, Guid SellerId) : IRequest<Result>;

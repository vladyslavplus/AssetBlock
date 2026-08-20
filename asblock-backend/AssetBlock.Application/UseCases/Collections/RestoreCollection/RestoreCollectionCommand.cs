using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Collections.RestoreCollection;

public sealed record RestoreCollectionCommand(Guid Id, Guid SellerId) : IRequest<Result>;

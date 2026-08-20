using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.RestoreCollection;

public sealed record RestoreCollectionCommand(Guid Id, Guid SellerId) : IRequest<Result>;

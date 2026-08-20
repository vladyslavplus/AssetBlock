using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.ArchiveCollection;

public sealed record ArchiveCollectionCommand(Guid Id, Guid SellerId) : IRequest<Result>;

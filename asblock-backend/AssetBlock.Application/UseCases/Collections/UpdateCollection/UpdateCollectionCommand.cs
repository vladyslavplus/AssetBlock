using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.UpdateCollection;

public sealed record UpdateCollectionCommand(Guid Id, Guid SellerId, string Title, string? Description)
    : IRequest<Result>;

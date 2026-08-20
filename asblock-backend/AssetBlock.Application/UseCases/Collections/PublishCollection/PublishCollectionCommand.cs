using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.PublishCollection;

public sealed record PublishCollectionCommand(Guid Id, Guid SellerId) : IRequest<Result>;

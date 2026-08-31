using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Collections;

namespace AssetBlock.Application.UseCases.Collections.CreateCollection;

public sealed record CreateCollectionCommand(Guid SellerId, string Title, string? Description)
    : IRequest<Result<CreateCollectionResponse>>;

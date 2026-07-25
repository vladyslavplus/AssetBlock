using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Collections;
using MediatR;

namespace AssetBlock.Application.UseCases.Collections.CreateCollection;

public sealed record CreateCollectionCommand(Guid SellerId, string Title, string? Description)
    : IRequest<Result<CreateCollectionResponse>>;

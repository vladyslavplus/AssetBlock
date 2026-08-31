using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Bundles;

namespace AssetBlock.Application.UseCases.Bundles.CreateBundle;

public sealed record CreateBundleCommand(
    Guid SellerId,
    string Title,
    string? Description,
    decimal Price,
    IReadOnlyList<Guid> AssetIds) : IRequest<Result<CreateBundleResponse>>;

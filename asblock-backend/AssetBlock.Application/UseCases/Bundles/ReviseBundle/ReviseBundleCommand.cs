using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Bundles;

namespace AssetBlock.Application.UseCases.Bundles.ReviseBundle;

public sealed record ReviseBundleCommand(
    Guid BundleId,
    Guid SellerId,
    string Title,
    string? Description,
    decimal Price,
    IReadOnlyList<Guid> AssetIds) : IRequest<Result<ReviseBundleResponse>>;

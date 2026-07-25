using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Bundles;
using MediatR;

namespace AssetBlock.Application.UseCases.Bundles.ReviseBundle;

public sealed record ReviseBundleCommand(
    Guid BundleId,
    Guid SellerId,
    string Title,
    string? Description,
    decimal Price,
    IReadOnlyList<Guid> AssetIds) : IRequest<Result<ReviseBundleResponse>>;

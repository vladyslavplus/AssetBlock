using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Bundles.GetBundle;

public sealed record GetBundleQuery(Guid Id) : IRequest<Result<BundleDetailDto>>;

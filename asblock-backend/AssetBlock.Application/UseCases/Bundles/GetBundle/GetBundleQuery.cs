using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Bundles;

namespace AssetBlock.Application.UseCases.Bundles.GetBundle;

public sealed record GetBundleQuery(Guid Id) : IRequest<Result<BundleDetailDto>>;

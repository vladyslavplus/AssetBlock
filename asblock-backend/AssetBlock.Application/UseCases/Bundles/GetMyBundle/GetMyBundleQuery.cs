using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Bundles;

namespace AssetBlock.Application.UseCases.Bundles.GetMyBundle;

public sealed record GetMyBundleQuery(Guid BundleId, Guid SellerId) : IRequest<Result<BundleDetailDto>>;

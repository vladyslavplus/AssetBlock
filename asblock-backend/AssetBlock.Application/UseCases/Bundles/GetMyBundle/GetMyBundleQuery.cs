using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Bundles;
using MediatR;

namespace AssetBlock.Application.UseCases.Bundles.GetMyBundle;

public sealed record GetMyBundleQuery(Guid BundleId, Guid SellerId) : IRequest<Result<BundleDetailDto>>;

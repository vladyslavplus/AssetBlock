using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Analytics;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsBundleDetail;

public sealed record GetSellerAnalyticsBundleDetailQuery(
    Guid SellerId,
    Guid BundleId,
    DateOnly From,
    DateOnly To) : IRequest<Result<AnalyticsBundleDetailDto>>;

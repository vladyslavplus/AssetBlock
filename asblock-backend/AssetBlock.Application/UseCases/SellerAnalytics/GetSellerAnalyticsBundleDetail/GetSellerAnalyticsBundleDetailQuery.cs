using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Analytics;
using MediatR;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsBundleDetail;

public sealed record GetSellerAnalyticsBundleDetailQuery(
    Guid SellerId,
    Guid BundleId,
    DateOnly From,
    DateOnly To) : IRequest<Result<AnalyticsBundleDetailDto>>;

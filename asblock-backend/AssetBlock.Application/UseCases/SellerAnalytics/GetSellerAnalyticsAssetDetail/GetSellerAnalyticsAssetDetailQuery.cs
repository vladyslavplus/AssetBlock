using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Analytics;
using MediatR;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsAssetDetail;

public sealed record GetSellerAnalyticsAssetDetailQuery(
    Guid SellerId,
    Guid AssetId,
    DateOnly From,
    DateOnly To) : IRequest<Result<AnalyticsAssetDetailDto>>;

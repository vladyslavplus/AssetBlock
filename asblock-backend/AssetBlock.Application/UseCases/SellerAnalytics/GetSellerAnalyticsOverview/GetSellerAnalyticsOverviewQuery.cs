using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Analytics;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsOverview;

public sealed record GetSellerAnalyticsOverviewQuery(
    Guid SellerId,
    DateOnly From,
    DateOnly To) : IRequest<Result<SellerAnalyticsOverviewDto>>;

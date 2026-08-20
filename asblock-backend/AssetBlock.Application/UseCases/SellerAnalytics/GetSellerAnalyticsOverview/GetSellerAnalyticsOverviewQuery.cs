using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Analytics;
using MediatR;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsOverview;

public sealed record GetSellerAnalyticsOverviewQuery(
    Guid SellerId,
    DateOnly From,
    DateOnly To) : IRequest<Result<SellerAnalyticsOverviewDto>>;

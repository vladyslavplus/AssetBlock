using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsProducts;

public sealed record GetSellerAnalyticsProductsQuery(
    Guid SellerId,
    AnalyticsProductsRequest Request) : IRequest<Result<AnalyticsProductsResult>>;

using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Analytics;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsProducts;

public sealed record GetSellerAnalyticsProductsQuery(
    Guid SellerId,
    AnalyticsProductsRequest Request) : IRequest<Result<AnalyticsProductsResult>>;

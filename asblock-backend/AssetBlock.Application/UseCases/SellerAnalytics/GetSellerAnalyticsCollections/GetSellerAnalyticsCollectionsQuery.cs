using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Analytics;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsCollections;

public sealed record GetSellerAnalyticsCollectionsQuery(
    Guid SellerId,
    AnalyticsCollectionsRequest Request) : IRequest<Result<AnalyticsCollectionsResult>>;

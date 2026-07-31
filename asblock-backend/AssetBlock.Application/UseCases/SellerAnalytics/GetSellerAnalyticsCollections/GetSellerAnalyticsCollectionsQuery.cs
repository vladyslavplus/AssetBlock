using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Analytics;
using MediatR;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsCollections;

public sealed record GetSellerAnalyticsCollectionsQuery(
    Guid SellerId,
    AnalyticsCollectionsRequest Request) : IRequest<Result<AnalyticsCollectionsResult>>;

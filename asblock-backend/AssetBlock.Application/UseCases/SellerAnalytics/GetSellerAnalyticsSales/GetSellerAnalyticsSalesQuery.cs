using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Analytics;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsSales;

public sealed record GetSellerAnalyticsSalesQuery(
    Guid SellerId,
    AnalyticsSalesRequest Request) : IRequest<Result<AnalyticsSalesResult>>;

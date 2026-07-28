using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Analytics;
using MediatR;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsSales;

public sealed record GetSellerAnalyticsSalesQuery(
    Guid SellerId,
    AnalyticsSalesRequest Request) : IRequest<Result<AnalyticsSalesResult>>;

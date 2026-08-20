using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Enums;
using MediatR;

namespace AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;

public sealed record ExportSellerAnalyticsSalesCommand(
    Guid SellerId,
    DateOnly From,
    DateOnly To,
    AnalyticsProductTypeFilter ProductType,
    Stream OutputStream,
    ISellerAnalyticsSalesExportSession Session) : IRequest<Result<int>>;

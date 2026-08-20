using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;

public sealed record PrepareSellerAnalyticsSalesExportQuery(
    Guid SellerId,
    DateOnly From,
    DateOnly To,
    AnalyticsProductTypeFilter ProductType) : IRequest<Result<PreparedSellerAnalyticsSalesExport>>;

public sealed record PreparedSellerAnalyticsSalesExport(
    ISellerAnalyticsSalesExportSession Session);

public static class SellerAnalyticsExportFileNames
{
    public static string SalesCsv(DateOnly from, DateOnly to) =>
        $"sales-export_{from:yyyy-MM-dd}_to_{to:yyyy-MM-dd}.csv";
}

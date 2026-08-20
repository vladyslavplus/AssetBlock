using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using FluentValidation;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;

internal sealed class PrepareSellerAnalyticsSalesExportQueryValidator
    : AbstractValidator<PrepareSellerAnalyticsSalesExportQuery>
{
    public PrepareSellerAnalyticsSalesExportQueryValidator()
    {
        SellerAnalyticsRangeRules.ApplyDateRangeRules(
            this,
            q => q.From,
            q => q.To);

        RuleFor(q => q.ProductType)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": 'productType' is invalid.");
    }
}

internal sealed class PrepareSellerAnalyticsSalesExportQueryHandler(
    ISellerAnalyticsStore analyticsStore)
    : IRequestHandler<PrepareSellerAnalyticsSalesExportQuery, Result<PreparedSellerAnalyticsSalesExport>>
{
    public async Task<Result<PreparedSellerAnalyticsSalesExport>> Handle(
        PrepareSellerAnalyticsSalesExportQuery request,
        CancellationToken cancellationToken)
    {
        var fromDto = AnalyticsRange.ToUtcStart(request.From);
        var toDto = AnalyticsRange.ToUtcStart(request.To);

        var session = await analyticsStore.OpenSalesExportSession(
            request.SellerId,
            fromDto,
            toDto,
            request.ProductType,
            cancellationToken);

        if (session.ExceedsMax)
        {
            await session.DisposeAsync();
            return ResultError.Error(ErrorCodes.ERR_ANALYTICS_EXPORT_TOO_LARGE);
        }

        return Result.Success(new PreparedSellerAnalyticsSalesExport(session));
    }
}

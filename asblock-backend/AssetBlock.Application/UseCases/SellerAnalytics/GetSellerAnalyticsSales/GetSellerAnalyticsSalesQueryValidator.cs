using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsSales;

internal sealed class GetSellerAnalyticsSalesQueryValidator : AbstractValidator<GetSellerAnalyticsSalesQuery>
{
    public GetSellerAnalyticsSalesQueryValidator()
    {
        RuleFor(q => q.Request.To)
            .Must((q, to) => to > q.Request.From)
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + ": 'to' must be after 'from'.");

        RuleFor(q => q.Request)
            .Must(r => r.To.DayNumber - r.From.DayNumber <= AnalyticsConstants.MAX_DAYS)
            .WithMessage(
                ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + $": range must not exceed {AnalyticsConstants.MAX_DAYS} days.");

        RuleFor(q => q.Request.To)
            .Must(to => to <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + ": 'to' must not be after tomorrow UTC.");

        RuleFor(q => q.Request.ProductType)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": 'productType' is invalid.");

        RuleFor(q => q.Request.PageSize)
            .InclusiveBetween(1, AnalyticsConstants.MAX_SALES_PAGE_SIZE)
            .WithMessage(
                ErrorCodes.ERR_ANALYTICS_INVALID_FILTER +
                $": 'pageSize' must be between 1 and {AnalyticsConstants.MAX_SALES_PAGE_SIZE}.");

        RuleFor(q => q.Request.Cursor)
            .Must(cursor => cursor is null || cursor.Length <= AnalyticsConstants.MAX_CURSOR_LENGTH)
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_CURSOR + ": cursor exceeds maximum length.");

        RuleFor(q => q.Request.Cursor)
            .Must(cursor => cursor is null || SalesCursorCodec.TryDecode(cursor, out _, out _))
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_CURSOR + ": cursor is malformed.");
    }
}

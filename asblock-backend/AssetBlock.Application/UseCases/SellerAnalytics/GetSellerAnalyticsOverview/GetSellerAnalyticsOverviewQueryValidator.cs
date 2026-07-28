using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsOverview;

internal sealed class GetSellerAnalyticsOverviewQueryValidator : AbstractValidator<GetSellerAnalyticsOverviewQuery>
{
    public GetSellerAnalyticsOverviewQueryValidator()
    {
        RuleFor(q => q.To)
            .Must((q, to) => to > q.From)
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + ": 'to' must be after 'from'.");

        RuleFor(q => q)
            .Must(q => q.To.DayNumber - q.From.DayNumber >= 1)
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + ": range must be at least 1 day.");

        RuleFor(q => q)
            .Must(q => q.To.DayNumber - q.From.DayNumber <= AnalyticsConstants.MAX_DAYS)
            .WithMessage(
                ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + $": range must not exceed {AnalyticsConstants.MAX_DAYS} days.");

        RuleFor(q => q.To)
            .Must(to => to <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + ": 'to' must not be after tomorrow UTC.");

        RuleFor(q => q)
            .Must(q =>
            {
                var days = q.To.DayNumber - q.From.DayNumber;
                try
                {
                    var compFrom = q.From.AddDays(-days);
                    return compFrom >= DateOnly.MinValue;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return false;
                }
            })
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + ": comparison period is not representable.");
    }
}

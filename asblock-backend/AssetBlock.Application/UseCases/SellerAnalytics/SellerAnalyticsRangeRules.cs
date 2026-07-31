using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.SellerAnalytics;

internal static class SellerAnalyticsRangeRules
{
    internal static void ApplyDateRangeRules<T>(
        AbstractValidator<T> validator,
        Func<T, DateOnly> fromSelector,
        Func<T, DateOnly> toSelector)
    {
        validator.RuleFor(q => q)
            .Must(q => toSelector(q) > fromSelector(q))
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + ": 'to' must be after 'from'.");

        validator.RuleFor(q => q)
            .Must(q => toSelector(q).DayNumber - fromSelector(q).DayNumber >= 1)
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + ": range must be at least 1 day.");

        validator.RuleFor(q => q)
            .Must(q => toSelector(q).DayNumber - fromSelector(q).DayNumber <= AnalyticsConstants.MAX_DAYS)
            .WithMessage(
                ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + $": range must not exceed {AnalyticsConstants.MAX_DAYS} days.");

        validator.RuleFor(q => q)
            .Must(q => toSelector(q) <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE + ": 'to' must not be after tomorrow UTC.");

        validator.RuleFor(q => q)
            .Must(q =>
            {
                var from = fromSelector(q);
                var to = toSelector(q);
                var days = to.DayNumber - from.DayNumber;
                try
                {
                    var compFrom = from.AddDays(-days);
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

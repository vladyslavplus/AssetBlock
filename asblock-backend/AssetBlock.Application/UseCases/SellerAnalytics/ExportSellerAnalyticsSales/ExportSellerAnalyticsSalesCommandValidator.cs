using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;

internal sealed class ExportSellerAnalyticsSalesCommandValidator : AbstractValidator<ExportSellerAnalyticsSalesCommand>
{
    public ExportSellerAnalyticsSalesCommandValidator()
    {
        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");

        SellerAnalyticsRangeRules.ApplyDateRangeRules(
            this,
            c => c.From,
            c => c.To);

        RuleFor(c => c.ProductType)
            .IsInEnum()
            .WithMessage(ErrorCodes.ERR_ANALYTICS_INVALID_FILTER + ": 'productType' is invalid.");

        RuleFor(c => c.OutputStream)
            .NotNull().WithMessage("OutputStream is required.");

        RuleFor(c => c.Session)
            .NotNull().WithMessage("Session is required.");
    }
}

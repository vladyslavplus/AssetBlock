using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.AuditLogs.GetAuditLogs;

internal sealed class GetAuditLogsQueryValidator : AbstractValidator<GetAuditLogsQuery>
{
    public GetAuditLogsQueryValidator()
    {
        RuleFor(q => q.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(q => q.Request).SetValidator(new PagedRequestValidator());

                RuleFor(q => q.Request.Action)
                    .MaximumLength(AuditFieldLimits.ACTION_MAX_LENGTH)
                    .When(q => q.Request.Action is not null);

                RuleFor(q => q.Request.ResourceType)
                    .MaximumLength(AuditFieldLimits.RESOURCE_TYPE_MAX_LENGTH)
                    .When(q => q.Request.ResourceType is not null);

                RuleFor(q => q.Request.ResourceId)
                    .MaximumLength(AuditFieldLimits.RESOURCE_ID_MAX_LENGTH)
                    .When(q => q.Request.ResourceId is not null);

                RuleFor(q => q.Request)
                    .Must(r => r.From is null || r.To is null || r.From <= r.To)
                    .WithMessage("From must be less than or equal to To.")
                    .When(q => q.Request.From is not null || q.Request.To is not null);
            });
    }
}
